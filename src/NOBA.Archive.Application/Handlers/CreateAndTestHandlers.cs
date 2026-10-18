using System.Diagnostics;
using System.Runtime.CompilerServices;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Application.Policies;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Handlers;

public sealed record CreateRequest
{
    public required string TargetPath { get; init; }

    /// <summary>Files and directories to include. Directories are added recursively.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public required CompressionOptions Options { get; init; }

    public bool Overwrite { get; init; }
}

public sealed record CreateOutcome
{
    public required string ArchivePath { get; init; }

    public long EntriesAdded { get; init; }

    public ByteSize BytesRead { get; init; } = ByteSize.Zero;

    public ByteSize ArchiveSize { get; init; } = ByteSize.Zero;

    public bool SourcesDeleted { get; init; }

    public ArchiveTestResult? Verification { get; init; }
}

/// <summary>
/// Creates an archive, and — when asked to delete the sources afterwards — only does so once the result
/// has been re-opened and integrity-tested.
/// </summary>
/// <remarks>
/// The verify-before-delete order is the whole point of APP-030/031. "Compress and delete originals" is
/// the single most destructive thing this product does, so it does not rely on the writer having
/// returned without an exception; it re-reads what landed on disk first.
/// </remarks>
public sealed class CreateArchiveHandler
{
    private readonly IArchiveProviderRegistry _registry;
    private readonly IFileSystem _fileSystem;
    private readonly IArchivePublisher _publisher;
    private readonly IPasswordProvider _passwords;
    private readonly TestArchiveHandler _verifier;

    public CreateArchiveHandler(
        IArchiveProviderRegistry registry,
        IFileSystem fileSystem,
        IArchivePublisher publisher,
        IPasswordProvider passwords,
        TestArchiveHandler verifier)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(verifier);

        _registry = registry;
        _fileSystem = fileSystem;
        _publisher = publisher;
        _passwords = passwords;
        _verifier = verifier;
    }

    public async Task<CreateOutcome> HandleAsync(
        CreateRequest request,
        OperationId operationId,
        IProgress<OperationProgress>? progress,
        IPauseToken? pauseToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = request.Options.Format;
        var required = request.Options.RequiredCapabilities();
        var provider = _registry.Resolve(format, required)
            ?? throw new ArchiveOperationException(
                CapabilityGate.Check(_registry.CapabilitiesFor(format), required, format)!);

        var items = EnumerateSources(request.Sources, cancellationToken).ToList();
        if (items.Count == 0)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "No files were found to add to the archive."));
        }

        var aggregator = new ProgressAggregator(operationId, OperationType.Create)
        {
            ItemsTotal = items.Count,
            BytesTotal = items.Sum(static i => i.Length),
        };

        var clock = Stopwatch.StartNew();
        aggregator.Start(clock.Elapsed);

        long added = 0;
        long bytes = 0;

        // The publication is opened before the builder so an "already exists" refusal costs nothing,
        // and is published only after CompleteAsync — an exception anywhere in between abandons the
        // staged file and leaves any existing archive exactly as it was.
        await using var publication = await _publisher
            .BeginAsync(request.TargetPath, request.Overwrite, cancellationToken)
            .ConfigureAwait(false);

        await using (var builder = await provider
            .CreateAsync(publication.Output, request.Options, _passwords, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pauseToken is not null)
                {
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!ArchivePath.TryCreate(item.EntryPath, out var entryPath, out var rejection))
                {
                    // A local file whose name cannot be represented inside an archive is a real error,
                    // not something to paper over with a silent rename.
                    throw new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.PathRejected,
                        rejection!.Detail,
                        item.FullPath));
                }

                // Opened on demand, not here: a pull-model backend asks for each entry's bytes while it
                // compresses, and holding every source file open until then would exhaust handles on a
                // folder of any size. The builder disposes what the callback returns.
                var content = new EntryContentSource(
                    ct =>
                    {
                        ct.ThrowIfCancellationRequested();

                        // Wrapped on the way out, so a pause reaches inside a single large file rather
                        // than only landing between entries. The builder owns and disposes what it is
                        // given, and the wrapper hands that ownership through.
                        return ValueTask.FromResult<Stream>(new PausableStream(
                            _fileSystem.OpenRead(item.FullPath), pauseToken, ct, leaveOpen: false));
                    },
                    item.Length);

                await builder
                    .AddAsync(entryPath, content, item.LastWriteTime, null, cancellationToken)
                    .ConfigureAwait(false);

                added++;
                bytes += item.Length;
                aggregator.ReportBytes(item.Length, clock.Elapsed);
                aggregator.ReportItem(item.EntryPath, clock.Elapsed);
                progress?.Report(aggregator.Snapshot(OperationState.Running, clock.Elapsed));
            }

            await builder.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await publication.PublishAsync(cancellationToken).ConfigureAwait(false);

        var archiveSize = ByteSize.FromBytes(_fileSystem.GetInfo(publication.TargetPath)?.Length ?? 0);

        ArchiveTestResult? verification = null;
        var deleted = false;
        if (request.Options.DeleteSourcesAfterVerifiedSuccess)
        {
            verification = await _verifier
                .HandleAsync(
                    new TestRequest { ArchivePath = request.TargetPath },
                    operationId,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!verification.IsUsable)
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.CorruptData,
                    "The new archive did not pass verification, so no source files were deleted.",
                    request.TargetPath));
            }

            foreach (var item in items)
            {
                _fileSystem.DeleteFile(item.FullPath);
            }

            deleted = true;
        }

        progress?.Report(aggregator.Snapshot(OperationState.Completed, clock.Elapsed));

        return new CreateOutcome
        {
            ArchivePath = publication.TargetPath,
            EntriesAdded = added,
            BytesRead = ByteSize.FromBytes(bytes),
            ArchiveSize = archiveSize,
            SourcesDeleted = deleted,
            Verification = verification,
        };
    }

    private IEnumerable<SourceItem> EnumerateSources(IReadOnlyList<string> sources, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source);

            if (_fileSystem.FileExists(full))
            {
                var info = _fileSystem.GetInfo(full);
                yield return new SourceItem(full, Path.GetFileName(full), info?.Length ?? 0, info?.LastWriteTime);
                continue;
            }

            if (!_fileSystem.DirectoryExists(full))
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.IoFailure,
                    $"'{source}' was not found.",
                    source));
            }

            var root = full.TrimEnd(Path.DirectorySeparatorChar);
            var rootName = Path.GetFileName(root);
            foreach (var entry in _fileSystem.EnumerateEntries(root, recursive: true, cancellationToken))
            {
                if (entry.IsDirectory || entry.IsReparsePoint)
                {
                    // Reparse points are not followed when building an archive either: a junction in
                    // the source tree would otherwise pull in content from outside it.
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry.FullPath).Replace(Path.DirectorySeparatorChar, '/');
                yield return new SourceItem(
                    entry.FullPath,
                    string.IsNullOrEmpty(rootName) ? relative : $"{rootName}/{relative}",
                    entry.Length,
                    entry.LastWriteTime);
            }
        }
    }

    private sealed record SourceItem(string FullPath, string EntryPath, long Length, DateTimeOffset? LastWriteTime);
}

public sealed record TestRequest
{
    public required string ArchivePath { get; init; }

    public ArchiveFormat? ForceFormat { get; init; }
}

/// <summary>Verifies an archive's integrity as far as the format and backend allow.</summary>
public sealed class TestArchiveHandler
{
    private readonly IArchiveProviderRegistry _registry;
    private readonly IFormatDetector _detector;
    private readonly IFileSystem _fileSystem;
    private readonly IPasswordProvider _passwords;

    public TestArchiveHandler(
        IArchiveProviderRegistry registry,
        IFormatDetector detector,
        IFileSystem fileSystem,
        IPasswordProvider passwords)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(passwords);

        _registry = registry;
        _detector = detector;
        _fileSystem = fileSystem;
        _passwords = passwords;
    }

    public async Task<ArchiveTestResult> HandleAsync(
        TestRequest request,
        OperationId operationId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = await ArchiveOpener
            .ResolveFormatAsync(request.ArchivePath, request.ForceFormat, _fileSystem, _detector, cancellationToken)
            .ConfigureAwait(false);

        var provider = _registry.Resolve(format, ArchiveCapabilities.Open | ArchiveCapabilities.Test);
        if (provider is null)
        {
            return new ArchiveTestResult { Status = ArchiveTestStatus.Unsupported };
        }

        await using var stream = _fileSystem.OpenRead(request.ArchivePath);
        await using var session = await provider
            .OpenAsync(
                ArchiveSource.FromStream(stream, Path.GetFileName(request.ArchivePath)),
                new OpenOptions { ForceFormat = format },
                _passwords,
                cancellationToken)
            .ConfigureAwait(false);

        return await session.TestAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ListRequest
{
    public required string ArchivePath { get; init; }

    public ArchiveFormat? ForceFormat { get; init; }

    /// <summary>Forces the legacy entry-name code page ("Open as…"). Null lets detection decide.</summary>
    public int? LegacyNameCodePage { get; init; }
}

public sealed record ListOutcome(
    ArchiveFormat Format,
    ArchiveStatistics Statistics,
    IReadOnlyList<ArchiveEntry> Entries,
    EntryNameEncodingInfo? NameEncoding = null);

/// <summary>
/// Lists an archive without extracting anything — which is also what makes searching by name cheap
/// (rule 21).
/// </summary>
public sealed class ListArchiveHandler
{
    private readonly IArchiveProviderRegistry _registry;
    private readonly IFormatDetector _detector;
    private readonly IFileSystem _fileSystem;
    private readonly IPasswordProvider _passwords;

    public ListArchiveHandler(
        IArchiveProviderRegistry registry,
        IFormatDetector detector,
        IFileSystem fileSystem,
        IPasswordProvider passwords)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(passwords);

        _registry = registry;
        _detector = detector;
        _fileSystem = fileSystem;
        _passwords = passwords;
    }

    public async Task<ListOutcome> HandleAsync(ListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = await ArchiveOpener
            .ResolveFormatAsync(request.ArchivePath, request.ForceFormat, _fileSystem, _detector, cancellationToken)
            .ConfigureAwait(false);

        var required = ArchiveCapabilities.Open | ArchiveCapabilities.ListEntries;
        var provider = _registry.Resolve(format, required)
            ?? throw new ArchiveOperationException(
                CapabilityGate.Check(_registry.CapabilitiesFor(format), required, format)!);

        await using var stream = _fileSystem.OpenRead(request.ArchivePath);
        await using var session = await provider
            .OpenAsync(
                ArchiveSource.FromStream(stream, Path.GetFileName(request.ArchivePath)),
                new OpenOptions { ForceFormat = format, LegacyNameCodePage = request.LegacyNameCodePage },
                _passwords,
                cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<ArchiveEntry>();
        await foreach (var entry in session.ListEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        return new ListOutcome(format, session.Statistics, entries, session.NameEncoding);
    }
}

/// <summary>
/// Sort keys <see cref="SearchRequest"/> can ask for. Every key breaks ties by
/// <see cref="ArchiveEntry.DisplayPath"/> (ordinal), so the same request returns the same order every
/// time even when many entries share a key — "deterministic" is DROP 07's own exit criterion, not
/// just a nice property.
/// </summary>
public enum SearchSortKey
{
    Path,
    Name,
    UncompressedSize,
    CompressedSize,
    Modified,
    CompressionRatio,
}

/// <summary>
/// Metadata-only search criteria over an archive's entry index — the contract
/// <c>docs/ARCHITECTURE.md</c> names as <c>SearchArchiveHandler</c> ("never extracts to match a
/// name") and nothing had ever implemented before DROP 07. Every filter here is ANDed together; a
/// filter left null or empty does not narrow the result.
/// </summary>
/// <remarks>
/// <see cref="NameContains"/> and <see cref="Extensions"/> match ordinally, never culture-aware — the
/// same rule <c>ArchiveNode.Summarise</c> and <c>BrowseViewModel.Matches</c> already document: a
/// culture-sensitive comparison this close to a path is a defect waiting to be exploited, and this
/// handler's matches are consumed as paths (filters, a future automation layer), not merely displayed
/// as text. Culture-aware display-name search stays exactly where it already lives, in the
/// Presentation layer, as a separate concern.
/// </remarks>
public sealed record SearchRequest
{
    public required string ArchivePath { get; init; }

    public ArchiveFormat? ForceFormat { get; init; }

    /// <summary>Forces the legacy entry-name code page ("Open as…"). Null lets detection decide.</summary>
    public int? LegacyNameCodePage { get; init; }

    /// <summary>Ordinal, case-insensitive substring match against the full displayed path (name AND
    /// path, per the DROP 07 exit criterion — not name alone).</summary>
    public string? NameContains { get; init; }

    /// <summary>
    /// Lower-cased, dot-included extensions (".txt"). An entry matches if its own extension is any one
    /// of these. Null or empty matches every extension.
    /// </summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    public EntryKind? Kind { get; init; }

    public ByteSize? MinSize { get; init; }

    public ByteSize? MaxSize { get; init; }

    public DateTimeOffset? ModifiedAfter { get; init; }

    public DateTimeOffset? ModifiedBefore { get; init; }

    /// <summary>Null means "archive order" — the only option that needs no buffering at all.</summary>
    public SearchSortKey? SortBy { get; init; }

    public bool Descending { get; init; }
}

public sealed record SearchOutcome(
    ArchiveFormat Format,
    ArchiveStatistics Statistics,
    IReadOnlyList<ArchiveEntry> Matches,
    long EntriesScanned,
    EntryNameEncodingInfo? NameEncoding = null);

/// <summary>
/// The Application-layer search contract DROP 07 exists to build: filters entries by metadata alone
/// and never extracts to judge a match, exactly like <see cref="ListArchiveHandler"/> beside it — the
/// difference is that this one can throw most of the archive away before anything downstream ever
/// sees it, which is what lets a future Intelligence layer extend it without touching the archive
/// core.
/// </summary>
public sealed class SearchArchiveHandler
{
    private readonly IArchiveProviderRegistry _registry;
    private readonly IFormatDetector _detector;
    private readonly IFileSystem _fileSystem;
    private readonly IPasswordProvider _passwords;

    public SearchArchiveHandler(
        IArchiveProviderRegistry registry,
        IFormatDetector detector,
        IFileSystem fileSystem,
        IPasswordProvider passwords)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(passwords);

        _registry = registry;
        _detector = detector;
        _fileSystem = fileSystem;
        _passwords = passwords;
    }

    /// <summary>
    /// Streams every matching entry in archive order, holding at most one entry's metadata at a time —
    /// this is the "large-archive browsing without loading everything into the UI" half of the exit
    /// criterion. There is deliberately no sort here: placing the first result in a sorted order
    /// requires having seen every match, which is exactly the buffering this method exists to avoid.
    /// Use <see cref="HandleAsync"/> when a sorted, archive-level result — counts, format, matches
    /// together — is what the caller actually wants, such as a CLI table or a results list.
    /// </summary>
    public async IAsyncEnumerable<ArchiveEntry> StreamAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = await ArchiveOpener
            .ResolveFormatAsync(request.ArchivePath, request.ForceFormat, _fileSystem, _detector, cancellationToken)
            .ConfigureAwait(false);

        var required = ArchiveCapabilities.Open | ArchiveCapabilities.ListEntries;
        var provider = _registry.Resolve(format, required)
            ?? throw new ArchiveOperationException(
                CapabilityGate.Check(_registry.CapabilitiesFor(format), required, format)!);

        await using var stream = _fileSystem.OpenRead(request.ArchivePath);
        await using var session = await provider
            .OpenAsync(
                ArchiveSource.FromStream(stream, Path.GetFileName(request.ArchivePath)),
                new OpenOptions { ForceFormat = format, LegacyNameCodePage = request.LegacyNameCodePage },
                _passwords,
                cancellationToken)
            .ConfigureAwait(false);

        await foreach (var entry in session.ListEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Matches(entry, request))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// The sorted, archive-level shape: matches plus the format/statistics/encoding a results view
    /// wants alongside them. Only the matched subset is ever held in memory — an archive with a
    /// million entries and five matches buffers five, not a million, even when
    /// <see cref="SearchRequest.SortBy"/> asks for an order <see cref="StreamAsync"/> alone cannot give.
    /// </summary>
    public async Task<SearchOutcome> HandleAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = await ArchiveOpener
            .ResolveFormatAsync(request.ArchivePath, request.ForceFormat, _fileSystem, _detector, cancellationToken)
            .ConfigureAwait(false);

        var required = ArchiveCapabilities.Open | ArchiveCapabilities.ListEntries;
        var provider = _registry.Resolve(format, required)
            ?? throw new ArchiveOperationException(
                CapabilityGate.Check(_registry.CapabilitiesFor(format), required, format)!);

        await using var stream = _fileSystem.OpenRead(request.ArchivePath);
        await using var session = await provider
            .OpenAsync(
                ArchiveSource.FromStream(stream, Path.GetFileName(request.ArchivePath)),
                new OpenOptions { ForceFormat = format, LegacyNameCodePage = request.LegacyNameCodePage },
                _passwords,
                cancellationToken)
            .ConfigureAwait(false);

        var matches = new List<ArchiveEntry>();
        long scanned = 0;
        await foreach (var entry in session.ListEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            scanned++;
            if (Matches(entry, request))
            {
                matches.Add(entry);
            }
        }

        if (request.SortBy is { } sortBy)
        {
            matches = Sort(matches, sortBy, request.Descending);
        }

        return new SearchOutcome(format, session.Statistics, matches, scanned, session.NameEncoding);
    }

    /// <summary>
    /// Every filter ANDed together, matched against metadata alone — the path string and the fields
    /// already on <see cref="ArchiveEntry"/>, never a byte of content. A rejected entry (null
    /// <see cref="ArchiveEntry.Path"/>) still has a name worth matching, so a filter that needs a path
    /// reads <see cref="ArchiveEntry.DisplayPath"/>/<see cref="ExtensionOf"/> rather than requiring
    /// <see cref="ArchiveEntry.Path"/> to be set: a security sweep for ".exe" should still find one even
    /// though the entry was already refused for an unrelated reason.
    /// </summary>
    private static bool Matches(ArchiveEntry entry, SearchRequest request)
    {
        if (request.Kind is { } kind && entry.Kind != kind)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(request.NameContains) &&
            entry.DisplayPath.IndexOf(request.NameContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (request.Extensions is { Count: > 0 } extensions &&
            !extensions.Contains(ExtensionOf(entry.DisplayPath), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.MinSize is { } minSize && entry.UncompressedSize.Bytes < minSize.Bytes)
        {
            return false;
        }

        if (request.MaxSize is { } maxSize && entry.UncompressedSize.Bytes > maxSize.Bytes)
        {
            return false;
        }

        if (request.ModifiedAfter is { } after && (entry.Modified is null || entry.Modified < after))
        {
            return false;
        }

        if (request.ModifiedBefore is { } before && (entry.Modified is null || entry.Modified > before))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Same convention as <see cref="ArchivePath.Extension"/> (lower-cased, dot included, "" when
    /// none), computed directly from the displayed path so it also works for a rejected entry, where
    /// <see cref="ArchiveEntry.Path"/> is null and <see cref="ArchivePath.Extension"/> is unreachable.
    /// Accepts either path separator: a rejected entry's raw path is whatever the archive stored,
    /// including a backslash that never made it through <see cref="ArchivePath.TryCreate"/>.
    /// </summary>
    private static string ExtensionOf(string displayPath)
    {
        var separator = displayPath.LastIndexOfAny(['/', '\\']);
        var name = separator < 0 ? displayPath : displayPath[(separator + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[dot..].ToLowerInvariant();
    }

    /// <summary>
    /// Stable (LINQ's <c>OrderBy</c>/<c>OrderByDescending</c> guarantee it) and always tie-broken by
    /// <see cref="ArchiveEntry.DisplayPath"/> (ordinal) regardless of <paramref name="descending"/>, so
    /// the same request returns the same order every time even when many entries share a sort key.
    /// </summary>
    private static List<ArchiveEntry> Sort(List<ArchiveEntry> matches, SearchSortKey key, bool descending)
    {
        IOrderedEnumerable<ArchiveEntry> ordered = key switch
        {
            SearchSortKey.Name => descending
                ? matches.OrderByDescending(static e => e.Path?.FileName ?? e.RawPath, StringComparer.Ordinal)
                : matches.OrderBy(static e => e.Path?.FileName ?? e.RawPath, StringComparer.Ordinal),
            SearchSortKey.UncompressedSize => descending
                ? matches.OrderByDescending(static e => e.UncompressedSize.Bytes)
                : matches.OrderBy(static e => e.UncompressedSize.Bytes),
            SearchSortKey.CompressedSize => descending
                ? matches.OrderByDescending(static e => e.CompressedSize.Bytes)
                : matches.OrderBy(static e => e.CompressedSize.Bytes),
            SearchSortKey.Modified => descending
                ? matches.OrderByDescending(static e => e.Modified ?? DateTimeOffset.MinValue)
                : matches.OrderBy(static e => e.Modified ?? DateTimeOffset.MinValue),
            SearchSortKey.CompressionRatio => descending
                ? matches.OrderByDescending(static e => e.CompressionRatio ?? -1d)
                : matches.OrderBy(static e => e.CompressionRatio ?? -1d),
            _ => descending
                ? matches.OrderByDescending(static e => e.DisplayPath, StringComparer.Ordinal)
                : matches.OrderBy(static e => e.DisplayPath, StringComparer.Ordinal),
        };

        return [.. ordered.ThenBy(static e => e.DisplayPath, StringComparer.Ordinal)];
    }
}
