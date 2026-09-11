using System.Diagnostics;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Application.Policies;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Handlers;

public sealed record ExtractRequest
{
    public required string ArchivePath { get; init; }

    public required ExtractionOptions Options { get; init; }

    /// <summary>Bypasses content sniffing ("Open as..."). Null lets detection decide.</summary>
    public ArchiveFormat? ForceFormat { get; init; }

    /// <summary>
    /// Forces the legacy entry-name code page. Must match whatever the listing used, or the user
    /// would extract files under different names than the ones they were shown.
    /// </summary>
    public int? LegacyNameCodePage { get; init; }
}

public sealed record ExtractOutcome
{
    public required ArchiveFormat Format { get; init; }

    public long EntriesWritten { get; init; }

    public long EntriesSkipped { get; init; }

    public long DirectoriesCreated { get; init; }

    public ByteSize BytesWritten { get; init; } = ByteSize.Zero;

    /// <summary>
    /// Entries the security policy refused. Reported, never silently dropped: a user is entitled to know
    /// that an archive tried something, and what.
    /// </summary>
    public IReadOnlyList<PathRejection> Blocked { get; init; } = [];

    public bool HadBlockedEntries => Blocked.Count > 0;
}

/// <summary>
/// Drives an extraction: decide the format, resolve a provider, and hand every entry to the extraction
/// writer.
/// </summary>
/// <remarks>
/// This class chooses <em>what</em> to extract and under which policy; it never opens a destination file
/// itself. The provider decodes into a stream, the writer owns the filesystem, and neither can be
/// bypassed — which is what makes ADR-0005 a property of the design rather than a convention.
/// </remarks>
public sealed class ExtractArchiveHandler
{
    private readonly IArchiveProviderRegistry _registry;
    private readonly IFormatDetector _detector;
    private readonly IFileSystem _fileSystem;
    private readonly IExtractionWriter _writer;
    private readonly IResourceGuard _guard;
    private readonly IPasswordProvider _passwords;

    public ExtractArchiveHandler(
        IArchiveProviderRegistry registry,
        IFormatDetector detector,
        IFileSystem fileSystem,
        IExtractionWriter writer,
        IResourceGuard guard,
        IPasswordProvider passwords)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(passwords);

        _registry = registry;
        _detector = detector;
        _fileSystem = fileSystem;
        _writer = writer;
        _guard = guard;
        _passwords = passwords;
    }

    public async Task<ExtractOutcome> HandleAsync(
        ExtractRequest request,
        OperationId operationId,
        IProgress<OperationProgress>? progress,
        IPauseToken? pauseToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = await ArchiveOpener
            .ResolveFormatAsync(request.ArchivePath, request.ForceFormat, _fileSystem, _detector, cancellationToken)
            .ConfigureAwait(false);

        var provider = _registry.Resolve(format, ExtractionOptions.RequiredCapabilities())
            ?? throw new ArchiveOperationException(CapabilityGate.Check(
                _registry.CapabilitiesFor(format),
                ExtractionOptions.RequiredCapabilities(),
                format)!);

        var options = request.Options with { SourceArchivePath = request.ArchivePath };

        await using var stream = _fileSystem.OpenRead(request.ArchivePath);
        await using var session = await provider
            .OpenAsync(
                ArchiveSource.FromStream(stream, Path.GetFileName(request.ArchivePath)),
                new OpenOptions { ForceFormat = format, LegacyNameCodePage = request.LegacyNameCodePage },
                _passwords,
                cancellationToken)
            .ConfigureAwait(false);

        var selection = options.SelectedEntries is null
            ? null
            : new HashSet<string>(options.SelectedEntries.Select(static p => p.Value), StringComparer.Ordinal);

        var aggregator = new ProgressAggregator(operationId, OperationType.Extract)
        {
            ItemsTotal = session.Statistics.EntryCount > 0 ? session.Statistics.EntryCount : null,
            BytesTotal = session.Statistics.TotalUncompressedSize.Bytes > 0
                ? session.Statistics.TotalUncompressedSize.Bytes
                : null,
        };

        // The per-entry ratio guard cannot see an archive of a million small entries that each expand
        // modestly. The declared totals are attacker-controlled and so can only ever refuse, never
        // reassure — which is exactly what this pre-check is for: an archive that admits up front to
        // being absurd is refused before a single byte is written.
        _guard.Reset();
        Trip(_guard.ObserveTotals(
            session.Statistics.EntryCount,
            session.Statistics.TotalCompressedSize,
            session.Statistics.TotalUncompressedSize));

        var clock = Stopwatch.StartNew();
        aggregator.Start(clock.Elapsed);

        var blocked = new List<PathRejection>();
        long written = 0;
        long skipped = 0;
        long directories = 0;
        long bytes = 0;
        long observedCompressed = 0;

        var files = new List<ArchiveEntry>();

        await foreach (var entry in session.ListEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pauseToken is not null)
            {
                await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            }

            if (selection is not null && (entry.Path is null || !selection.Contains(entry.Path.Value.Value)))
            {
                continue;
            }

            if (!entry.IsDirectory)
            {
                // Files are collected first so a batch-capable backend can be given the whole set at
                // once. Directories are made eagerly: they are cheap, and they must exist before the
                // files that live in them arrive.
                files.Add(entry);
                continue;
            }

            var directoryResult = await _writer
                .CreateDirectoryAsync(entry, options, cancellationToken)
                .ConfigureAwait(false);

            if (directoryResult.Outcome == EntryWriteOutcome.Blocked)
            {
                blocked.Add(directoryResult.Rejection!);
            }
            else if (directoryResult.Outcome != EntryWriteOutcome.Skipped)
            {
                directories++;
            }

            aggregator.ReportItem(entry.DisplayPath, clock.Elapsed);
            progress?.Report(aggregator.Snapshot(OperationState.Running, clock.Elapsed));
        }

        void Record(EntryWriteResult result, ArchiveEntry entry)
        {
            switch (result.Outcome)
            {
                case EntryWriteOutcome.Blocked:
                    blocked.Add(result.Rejection!);
                    break;
                case EntryWriteOutcome.Skipped:
                    skipped++;
                    break;
                default:
                    written++;
                    bytes += result.BytesWritten.Bytes;
                    observedCompressed += entry.CompressedSize.Bytes;
                    aggregator.ReportBytes(result.BytesWritten.Bytes, clock.Elapsed);

                    // The running half of the archive-wide guard, and the half that matters: the
                    // pre-flight check above reads the archive's own declared totals, which an
                    // attacker writes. These are bytes we watched arrive. An archive that understates
                    // itself gets past the first check and is caught here, mid-extraction, before the
                    // total becomes large enough to matter.
                    Trip(_guard.ObserveTotals(
                        written + skipped + directories,
                        ByteSize.FromBytes(observedCompressed),
                        ByteSize.FromBytes(bytes)));
                    break;
            }

            aggregator.ReportItem(entry.DisplayPath, clock.Elapsed);
            progress?.Report(aggregator.Snapshot(OperationState.Running, clock.Elapsed));
        }

        if (session.SupportsBatchExtraction && files.Count > 1 && !options.PreferSingleEntryExtraction)
        {
            // One pass over the archive. For a solid 7z this is the difference between decoding each
            // block once and decoding it once per entry (R18).
            var sessions = new Dictionary<string, IEntryWriteSession>(StringComparer.Ordinal);

            try
            {
                await session.ExtractManyAsync(
                    files,
                    async (entry, ct) =>
                    {
                        // Between entries: the cheap check. A pause pressed while 7-Zip is between
                        // files takes effect here without a byte having to be offered first.
                        if (pauseToken is not null)
                        {
                            await pauseToken.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        }

                        ct.ThrowIfCancellationRequested();

                        var write = await _writer.BeginEntryAsync(entry, options, ct).ConfigureAwait(false);
                        if (write.Refusal is { } refusal)
                        {
                            // Declining here is what makes the backend unable to write a refused entry:
                            // it is handed no stream at all, and never learns why.
                            await write.DisposeAsync().ConfigureAwait(false);
                            Record(refusal, entry);
                            return null;
                        }

                        sessions[entry.RawPath] = write;

                        // And within an entry: the check that makes the guarantee true for an archive
                        // that is one large file, where "between entries" never comes around again.
                        return write.Content is { } content
                            ? PausableStream.Wrap(content, pauseToken, ct)
                            : null;
                    },
                    async (entry, ok, ct) =>
                    {
                        if (!sessions.Remove(entry.RawPath, out var write))
                        {
                            return;
                        }

                        await using (write.ConfigureAwait(false))
                        {
                            if (!ok)
                            {
                                // The backend reported a failure for this entry. Abandoning discards
                                // the temp file, so a corrupt entry never appears under its real name.
                                await write.AbandonAsync().ConfigureAwait(false);
                                return;
                            }

                            Record(await write.CompleteAsync(ct).ConfigureAwait(false), entry);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Anything 7-Zip never reported on is abandoned rather than left open or published.
                foreach (var orphan in sessions.Values)
                {
                    await orphan.AbandonAsync().ConfigureAwait(false);
                    await orphan.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        else
        {
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pauseToken is not null)
                {
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                }

                var result = await _writer.WriteEntryAsync(
                    entry,
                    options,
                    (stream, ct) => session.ExtractToAsync(
                        entry, PausableStream.Wrap(stream, pauseToken, ct), null, ct),
                    cancellationToken).ConfigureAwait(false);

                Record(result, entry);
            }
        }

        progress?.Report(aggregator.Snapshot(OperationState.Completed, clock.Elapsed));

        return new ExtractOutcome
        {
            Format = format,
            EntriesWritten = written,
            EntriesSkipped = skipped,
            DirectoriesCreated = directories,
            BytesWritten = ByteSize.FromBytes(bytes),
            Blocked = blocked,
        };
    }

    private static void Trip(GuardVerdict verdict)
    {
        if (verdict.IsTripped)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.BombSuspected,
                verdict.Explanation));
        }
    }
}

/// <summary>Shared format resolution: content is authoritative, the extension is a hint.</summary>
internal static class ArchiveOpener
{
    public static async ValueTask<ArchiveFormat> ResolveFormatAsync(
        string archivePath,
        ArchiveFormat? forced,
        IFileSystem fileSystem,
        IFormatDetector detector,
        CancellationToken cancellationToken)
    {
        if (forced is { } explicitFormat)
        {
            return explicitFormat;
        }

        if (!fileSystem.FileExists(archivePath))
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.IoFailure,
                $"'{archivePath}' was not found.",
                archivePath));
        }

        await using var probe = fileSystem.OpenRead(archivePath);
        var detection = await detector
            .DetectAsync(probe, Path.GetFileName(archivePath), cancellationToken)
            .ConfigureAwait(false);

        if (!detection.IsKnown)
        {
            // Naming what it actually is turns a dead end into a next step: the user can rename it,
            // open it in the right application, or tell us to force a format.
            var message = detection.IdentifiedAs is { } identified
                ? $"This file is {identified}, not an archive — the extension says otherwise."
                : "The file is not a recognised archive.";

            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedFormat,
                message,
                archivePath));
        }

        return detection.Format;
    }
}
