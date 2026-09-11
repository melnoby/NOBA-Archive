using System.Diagnostics;
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
