using System.Diagnostics;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Application.Policies;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Handlers;

/// <summary>One existing entry's new path.</summary>
public sealed record EntryRename(ArchivePath From, ArchivePath To);

public sealed record ManageRequest
{
    public required string ArchivePath { get; init; }

    public ArchiveFormat? ForceFormat { get; init; }

    /// <summary><see cref="CompressionOptions.Format"/> must equal the archive's own format — updating
    /// an archive cannot change what format it is.</summary>
    public required CompressionOptions Options { get; init; }

    /// <summary>Existing entries, by their current path in the archive, to drop.</summary>
    public IReadOnlyList<ArchivePath> EntriesToRemove { get; init; } = [];

    /// <summary>Existing entries to rename. An entry named here must not also appear in <see cref="EntriesToRemove"/>.</summary>
    public IReadOnlyList<EntryRename> EntriesToRename { get; init; } = [];

    /// <summary>Files and directories to add. Directories are added recursively, exactly like <see cref="CreateRequest.Sources"/>.</summary>
    public IReadOnlyList<string> SourcesToAdd { get; init; } = [];
}

public sealed record ManageOutcome
{
    public required string ArchivePath { get; init; }

    public long EntriesKept { get; init; }

    public long EntriesRemoved { get; init; }

    public long EntriesRenamed { get; init; }

    public long EntriesAdded { get; init; }

    public ByteSize ArchiveSize { get; init; } = ByteSize.Zero;
}

/// <summary>
/// Adds, removes, and renames entries in an existing archive — "updating an archive" as DROP 03 defines
/// it.
/// </summary>
/// <remarks>
/// <para>
/// There is no provider-level in-place update anywhere in this codebase, and
/// <c>FormatCapabilityReport.CanUpdate</c>'s own remarks explain why: a promise a backend cannot keep is
/// worse than a missing feature. This handler keeps the promise a different way — it never asks a
/// provider to mutate bytes it already wrote. It opens the existing archive read-only, extracts every
/// entry that survives the request (skipping removals, applying renames) into real files via
/// <see cref="ITempWorkspaceFactory"/>, then builds a brand-new archive from those files plus whatever
/// is being added, through the exact same <see cref="IArchiveProvider.CreateAsync"/> and
/// <see cref="IArchivePublisher"/> path <see cref="CreateArchiveHandler"/> already uses and
/// CRT-014/015/016 already proved safe against disk-full, access-denied, and a locked source.
/// </para>
/// <para>
/// The ordering is what makes an interrupted update harmless. Every request-shape error (an unknown
/// entry, a name collision, a path that cannot be represented) is caught before the old archive is even
/// opened for reading or the publication is begun; the old archive is read to completion and closed
/// before the staging file beside it is opened; and the original is overwritten only once, atomically,
/// by <see cref="IArchivePublication.PublishAsync"/> after the new archive is fully built. A fault
/// anywhere in between leaves the staging file abandoned and the original archive exactly as it was —
/// the same guarantee creation already gives, run once in each direction.
/// </para>
/// </remarks>
public sealed class ManageArchiveHandler
{
    /// <summary>
    /// What an archive has to support for this handler to touch it at all.
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="ArchiveCapabilities.AddEntries"/> rather than
    /// <see cref="ArchiveCapabilities.RemoveEntries"/> or <see cref="ArchiveCapabilities.RenameEntries"/>:
    /// no provider in this build sets those two raw flags — <c>SevenZipArchiveProvider</c> and
    /// <c>TarArchiveProvider</c> both say why in their own comments, namely that native in-place update
    /// is not implemented for any format — and this handler does not need them to be set. Removing an
    /// entry means not re-adding it; renaming means re-adding it under a different path. The only raw
    /// capabilities that work actually exercises are reading the old archive and writing a new one.
    /// </remarks>
    internal const ArchiveCapabilities RewriteRequirements =
        ArchiveCapabilities.Open
        | ArchiveCapabilities.ListEntries
        | ArchiveCapabilities.Extract
        | ArchiveCapabilities.Create
        | ArchiveCapabilities.AddEntries;

    private readonly IArchiveProviderRegistry _registry;
    private readonly IFormatDetector _detector;
    private readonly IFileSystem _fileSystem;
    private readonly IArchivePublisher _publisher;
    private readonly IPasswordProvider _passwords;
    private readonly ITempWorkspaceFactory _tempWorkspaces;

    public ManageArchiveHandler(
        IArchiveProviderRegistry registry,
        IFormatDetector detector,
        IFileSystem fileSystem,
        IArchivePublisher publisher,
        IPasswordProvider passwords,
        ITempWorkspaceFactory tempWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(passwords);
        ArgumentNullException.ThrowIfNull(tempWorkspaces);

        _registry = registry;
        _detector = detector;
        _fileSystem = fileSystem;
        _publisher = publisher;
        _passwords = passwords;
        _tempWorkspaces = tempWorkspaces;
    }

    public async Task<ManageOutcome> HandleAsync(
        ManageRequest request,
        OperationId operationId,
        IProgress<OperationProgress>? progress,
        IPauseToken? pauseToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EntriesToRemove.Count == 0 && request.EntriesToRename.Count == 0 && request.SourcesToAdd.Count == 0)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "Nothing to do: no entries were added, removed, or renamed.",
                request.ArchivePath));
        }

        var format = await ArchiveOpener
            .ResolveFormatAsync(request.ArchivePath, request.ForceFormat, _fileSystem, _detector, cancellationToken)
            .ConfigureAwait(false);

        if (request.Options.Format != format)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                $"'{request.ArchivePath}' is {format}, but the requested options are for " +
                $"{request.Options.Format}. Updating an archive cannot change its format.",
                request.ArchivePath));
        }

        var required = RewriteRequirements | request.Options.RequiredCapabilities();
        var provider = _registry.Resolve(format, required)
            ?? throw new ArchiveOperationException(
                CapabilityGate.Check(_registry.CapabilitiesFor(format), required, format)!);

        var removals = new HashSet<ArchivePath>(request.EntriesToRemove);
        var renames = new Dictionary<ArchivePath, ArchivePath>();
        foreach (var rename in request.EntriesToRename)
        {
            if (removals.Contains(rename.From))
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.InvalidConfiguration,
                    $"'{rename.From.Value}' is both removed and renamed; it can only be one or the other.",
                    request.ArchivePath));
            }

            if (!renames.TryAdd(rename.From, rename.To))
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.InvalidConfiguration,
                    $"'{rename.From.Value}' is renamed more than once.",
                    request.ArchivePath));
            }
        }

        var aggregator = new ProgressAggregator(operationId, OperationType.Add);
        var clock = Stopwatch.StartNew();
        aggregator.Start(clock.Elapsed);

        var workspace = _tempWorkspaces.Create(operationId);
        await using (workspace.ConfigureAwait(false))
        {
            var staged = new List<StagedEntry>();
            long kept = 0;
            long removed = 0;
            long renamed = 0;

            await using (var sourceStream = _fileSystem.OpenRead(request.ArchivePath))
            {
                await using var session = await provider
                    .OpenAsync(
                        ArchiveSource.FromStream(sourceStream, Path.GetFileName(request.ArchivePath)),
                        new OpenOptions { ForceFormat = format },
                        _passwords,
                        cancellationToken)
                    .ConfigureAwait(false);

                var seenRemovals = new HashSet<ArchivePath>();
                var seenRenames = new HashSet<ArchivePath>();

                await foreach (var entry in session.ListEntriesAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (entry.IsRejected || entry.Path is not { } entryPath)
                    {
                        // A hostile or unparsable entry already in the archive is dropped rather than
                        // carried into the rewrite — the same refusal Create applies to a local source
                        // whose name cannot be represented (see CreateArchiveHandler).
                        continue;
                    }

                    if (entry.IsDirectory || entry.IsLink)
                    {
                        // Directory entries are implied by the file paths nested under them, and this
                        // handler has no extraction path for a link's target. Out of scope for DROP 03's
                        // first cut; a format whose directories carry attributes worth preserving can
                        // extend this later without changing the shape of the request.
                        continue;
                    }

                    if (removals.Contains(entryPath))
                    {
                        seenRemovals.Add(entryPath);
                        removed++;
                        continue;
                    }

                    ArchivePath targetPath;
                    if (renames.TryGetValue(entryPath, out var renamedTo))
                    {
                        if (!ArchivePath.TryCreate(renamedTo.Value, out targetPath, out var rejection))
                        {
                            throw new ArchiveOperationException(ArchiveError.Create(
                                ArchiveErrorCode.PathRejected,
                                rejection!.Detail,
                                renamedTo.Value));
                        }

                        seenRenames.Add(entryPath);
                        renamed++;
                    }
                    else
                    {
                        targetPath = entryPath;
                        kept++;
                    }

                    var tempPath = workspace.CreateFilePath(entryPath.FileName);
                    await using (var destination = _fileSystem.CreateNew(tempPath, entry.UncompressedSize.Bytes))
                    {
                        await session
                            .ExtractToAsync(entry, destination, null, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    staged.Add(new StagedEntry(targetPath, tempPath, entry.UncompressedSize.Bytes, entry.Modified));
                    aggregator.ReportItem(entryPath.Value, clock.Elapsed);
                    progress?.Report(aggregator.Snapshot(OperationState.Running, clock.Elapsed));
                }

                var missing = removals.Except(seenRemovals).Concat(renames.Keys.Except(seenRenames)).ToList();
                if (missing.Count > 0)
                {
                    throw new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.InvalidConfiguration,
                        $"'{string.Join("', '", missing.Select(static p => p.Value))}' does not match any entry in " +
                        $"'{request.ArchivePath}'.",
                        request.ArchivePath));
                }
            }

            var pathsSoFar = new HashSet<ArchivePath>(staged.Select(static s => s.TargetPath));
            var newItems = EnumerateSourcesToAdd(request.SourcesToAdd, cancellationToken).ToList();
            var newEntryPaths = new List<ArchivePath>(newItems.Count);
            foreach (var item in newItems)
            {
                if (!ArchivePath.TryCreate(item.EntryPath, out var entryPath, out var rejection))
                {
                    throw new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.PathRejected,
                        rejection!.Detail,
                        item.FullPath));
                }

                if (!pathsSoFar.Add(entryPath))
                {
                    throw new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.InvalidConfiguration,
                        $"'{entryPath.Value}' already exists in the archive.",
                        item.FullPath));
                }

                newEntryPaths.Add(entryPath);
            }

            // Opened only now: every error the request itself could cause — an unknown entry, a name
            // collision, a path that cannot be represented — has already happened, or cannot happen
            // beyond this point. Overwriting the archive starts only once nothing short of an I/O fault
            // stands between here and a finished replacement.
            await using var publication = await _publisher
                .BeginAsync(request.ArchivePath, overwrite: true, cancellationToken)
                .ConfigureAwait(false);

            long added = 0;
            await using (var builder = await provider
                .CreateAsync(publication.Output, request.Options, _passwords, cancellationToken)
                .ConfigureAwait(false))
            {
                foreach (var stagedEntry in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken is not null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var content = new EntryContentSource(
                        ct => ValueTask.FromResult<Stream>(new PausableStream(
                            _fileSystem.OpenRead(stagedEntry.TempPath), pauseToken, ct, leaveOpen: false)),
                        stagedEntry.Length);

                    await builder
                        .AddAsync(stagedEntry.TargetPath, content, stagedEntry.Modified, null, cancellationToken)
                        .ConfigureAwait(false);

                    aggregator.ReportBytes(stagedEntry.Length, clock.Elapsed);
                    progress?.Report(aggregator.Snapshot(OperationState.Running, clock.Elapsed));
                }

                for (var i = 0; i < newItems.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pauseToken is not null)
                    {
                        await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var item = newItems[i];
                    var content = new EntryContentSource(
                        ct => ValueTask.FromResult<Stream>(new PausableStream(
                            _fileSystem.OpenRead(item.FullPath), pauseToken, ct, leaveOpen: false)),
                        item.Length);

                    await builder
                        .AddAsync(newEntryPaths[i], content, item.LastWriteTime, null, cancellationToken)
                        .ConfigureAwait(false);

                    added++;
                    aggregator.ReportBytes(item.Length, clock.Elapsed);
                    aggregator.ReportItem(item.EntryPath, clock.Elapsed);
                    progress?.Report(aggregator.Snapshot(OperationState.Running, clock.Elapsed));
                }

                await builder.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await publication.PublishAsync(cancellationToken).ConfigureAwait(false);

            var archiveSize = ByteSize.FromBytes(_fileSystem.GetInfo(publication.TargetPath)?.Length ?? 0);

            progress?.Report(aggregator.Snapshot(OperationState.Completed, clock.Elapsed));

            return new ManageOutcome
            {
                ArchivePath = publication.TargetPath,
                EntriesKept = kept,
                EntriesRemoved = removed,
                EntriesRenamed = renamed,
                EntriesAdded = added,
                ArchiveSize = archiveSize,
            };
        }
    }

    private sealed record StagedEntry(ArchivePath TargetPath, string TempPath, long Length, DateTimeOffset? Modified);

    /// <summary>
    /// Mirrors <c>CreateArchiveHandler.EnumerateSources</c>/<c>SourceItem</c> deliberately rather than
    /// sharing it: that method is exercised end to end by CRT-001…013, and this handler's own
    /// correctness should not ride on refactoring already-covered code into a shared helper neither
    /// drop asked for.
    /// </summary>
    private IEnumerable<SourceItem> EnumerateSourcesToAdd(IReadOnlyList<string> sources, CancellationToken cancellationToken)
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
