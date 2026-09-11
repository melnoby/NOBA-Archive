using System.Globalization;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// The only component in NOBA Archive that writes to an extraction destination (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// A provider decodes bytes into a stream this class chose. It never learns a destination path, so a
/// path-handling defect in a backend — including in <c>7z.dll</c>, which has shipped several — cannot
/// place a file outside the folder the user picked. The 2025 archiver CVE wave
/// (<c>CVE-2025-11001</c>, <c>CVE-2025-55188</c>, <c>CVE-2025-8088</c>) was entirely this class of bug.
/// </para>
/// <para>
/// Every entry goes through the same sequence, and the order matters:
/// </para>
/// <list type="number">
/// <item>Refuse an entry the archive already declared unusable, or one the path policy rejects.</item>
/// <item>Refuse link entries unless the link policy explicitly allows them.</item>
/// <item>Create directories one component at a time, refusing any component that is already a reparse
/// point — that is what stops a junction planted in the destination from redirecting the write.</item>
/// <item>Resolve conflicts through the single conflict service, never ad hoc.</item>
/// <item>Write to a temp file <em>in the destination directory</em>, then <b>re-verify the opened
/// handle</b> resolves where validation said it would. This is the TOCTOU defence: validation happens
/// before the open, and anything can change in between.</item>
/// <item>Stream through the bomb guard, publish atomically, then apply metadata and Mark of the Web.</item>
/// </list>
/// </remarks>
public sealed partial class ExtractionWriter : IExtractionWriter
{
    private const string TempPrefix = ".noba-";
    private const string TempSuffix = ".tmp";

    private readonly IFileSystem _fileSystem;
    private readonly IPathPolicy _pathPolicy;
    private readonly IResourceGuard _guard;
    private readonly IExtractionPlatform _platform;
    private readonly IConflictResolutionService _conflicts;
    private readonly ILogger<ExtractionWriter> _logger;
    private int _reducedAssuranceReported;

    public ExtractionWriter(
        IFileSystem fileSystem,
        IPathPolicy pathPolicy,
        IResourceGuard guard,
        IExtractionPlatform platform,
        IConflictResolutionService conflicts,
        ILogger<ExtractionWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(logger);

        _fileSystem = fileSystem;
        _pathPolicy = pathPolicy;
        _guard = guard;
        _platform = platform;
        _conflicts = conflicts;
        _logger = logger;
    }

    /// <summary>
    /// Everything that can refuse an entry, then the open temporary file — but never publication.
    /// </summary>
    public async ValueTask<IEntryWriteSession> BeginEntryAsync(
        ArchiveEntry entry,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryResolveDestination(entry, options, out var destination, out var blocked))
        {
            return EntryWriteSession.Refused(blocked!);
        }

        var directory = Path.GetDirectoryName(destination)!;
        if (!TryPrepareDirectory(directory, options.DestinationRoot, out var directoryRejection))
        {
            return EntryWriteSession.Refused(EntryWriteResult.Blocked(directoryRejection!));
        }

        var outcome = EntryWriteOutcome.Written;
        if (_fileSystem.FileExists(destination))
        {
            var action = await _conflicts.ResolveAsync(
                new ConflictRequest(
                    destination,
                    ByteSize.FromBytes(_fileSystem.GetInfo(destination)?.Length ?? 0),
                    entry.UncompressedSize),
                options.ConflictPolicy,
                cancellationToken).ConfigureAwait(false);

            switch (action)
            {
                case ConflictAction.Skip:
                    return EntryWriteSession.Refused(EntryWriteResult.Skipped(destination));
                case ConflictAction.Cancel:
                    throw new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.Cancelled,
                        "Extraction stopped because the destination already contains this file.",
                        destination));
                case ConflictAction.Rename:
                    destination = _conflicts.BuildRenamedPath(destination, _fileSystem.FileExists);
                    outcome = EntryWriteOutcome.Renamed;
                    break;
                case ConflictAction.Overwrite:
                    outcome = EntryWriteOutcome.Overwritten;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), action, "Unknown conflict action.");
            }
        }

        var temp = Path.Combine(
            directory,
            string.Create(CultureInfo.InvariantCulture, $"{TempPrefix}{Guid.NewGuid():N}{TempSuffix}"));

        var preallocate = entry.UncompressedSize.Bytes is > 0 and < (1L << 40)
            ? entry.UncompressedSize.Bytes
            : (long?)null;

        Stream file;
        try
        {
            file = _fileSystem.CreateNew(temp, preallocate);
        }
        catch
        {
            TryDeleteTemp(temp);
            throw;
        }

        var guarded = new GuardedWriteStream(file, _guard, entry.CompressedSize, entry.RawPath);
        try
        {
            VerifyOpenHandle(file, temp, options.DestinationRoot);
        }
        catch
        {
            await guarded.DisposeAsync().ConfigureAwait(false);
            TryDeleteTemp(temp);
            throw;
        }

        return new EntryWriteSession(this, entry, options, guarded, temp, destination, outcome);
    }

    /// <summary>
    /// The per-entry path, expressed as begin then complete so there is one implementation of the
    /// rules rather than two that can drift.
    /// </summary>
    public async ValueTask<EntryWriteResult> WriteEntryAsync(
        ArchiveEntry entry,
        ExtractionOptions options,
        Func<Stream, CancellationToken, ValueTask> decodeInto,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decodeInto);

        var session = await BeginEntryAsync(entry, options, cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            if (session.Refusal is { } refusal)
            {
                return refusal;
            }

            try
            {
                await decodeInto(session.Content!, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.AbandonAsync().ConfigureAwait(false);
                throw;
            }

            return await session.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes a finished entry: move, then stamp. The stream is already closed by this point —
    /// on Windows a file cannot be moved while a handle to it is open, so the order is not a
    /// preference.
    /// </summary>
    private EntryWriteResult Publish(
        ArchiveEntry entry,
        ExtractionOptions options,
        long written,
        string temp,
        string destination,
        EntryWriteOutcome outcome)
    {
        // Publish only after the bytes are safely on disk. A crash before this point leaves a temp
        // file, never a half-written file under the name the user will trust.
        _fileSystem.MoveFile(temp, destination, overwrite: outcome == EntryWriteOutcome.Overwritten);

        ApplyMetadata(entry, options, destination);
        return new EntryWriteResult(outcome, destination, null, ByteSize.FromBytes(written));
    }

    public ValueTask<EntryWriteResult> CreateDirectoryAsync(
        ArchiveEntry entry,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (!options.PreserveDirectoryStructure)
        {
            // Flattened output has no directories to make; this is not an error.
            return ValueTask.FromResult(new EntryWriteResult(EntryWriteOutcome.Skipped, null, null, ByteSize.Zero));
        }

        if (!TryResolveDestination(entry, options, out var destination, out var blocked))
        {
            return ValueTask.FromResult(blocked!);
        }

        return TryPrepareDirectory(destination, options.DestinationRoot, out var rejection)
            ? ValueTask.FromResult(new EntryWriteResult(EntryWriteOutcome.Written, destination, null, ByteSize.Zero))
            : ValueTask.FromResult(EntryWriteResult.Blocked(rejection!));
    }

    /// <summary>Steps 1 and 2: everything that can refuse an entry before a byte is written.</summary>
    private bool TryResolveDestination(
        ArchiveEntry entry,
        ExtractionOptions options,
        out string destination,
        out EntryWriteResult? blocked)
    {
        destination = string.Empty;
        blocked = null;

        if (entry.Rejection is { } declared)
        {
            blocked = EntryWriteResult.Blocked(declared);
            return false;
        }

        if (entry.IsLink && options.LinkPolicy != LinkPolicy.WhenInsideDestination)
        {
            // Default is Never. "Always create" is not an option the product offers (T2).
            blocked = EntryWriteResult.Blocked(new PathRejection(
                PathRejectionReason.LinkRefused,
                entry.RawPath,
                $"Link entries are not created under the '{options.LinkPolicy}' policy."));
            return false;
        }

        var validation = _pathPolicy.Validate(entry.RawPath, options.DestinationRoot, options.IllegalNamePolicy);
        if (!validation.IsAccepted)
        {
            blocked = EntryWriteResult.Blocked(validation.Rejection!);
            return false;
        }

        var validated = validation.Path!.Value.FullPath;

        if (entry.IsLink &&
            !_pathPolicy.IsLinkTargetInsideDestination(
                entry.LinkTarget ?? string.Empty,
                Path.GetDirectoryName(validated) ?? options.DestinationRoot,
                options.DestinationRoot))
        {
            blocked = EntryWriteResult.Blocked(new PathRejection(
                PathRejectionReason.LinkRefused,
                entry.RawPath,
                "The link target resolves outside the destination folder."));
            return false;
        }

        // Flattening still goes through the policy first: the name has to be safe either way.
        destination = options.PreserveDirectoryStructure
            ? validated
            : Path.Combine(Path.GetFullPath(options.DestinationRoot), Path.GetFileName(validated));

        if (validation.WasSanitized)
        {
            foreach (var change in validation.Sanitizations)
            {
                LogSanitized(_logger, change.OriginalSegment, change.ReplacementSegment, change.Reason);
            }
        }

        return true;
    }

    /// <summary>
    /// Step 3: build the directory chain one component at a time, refusing any existing component that
    /// is a reparse point. Calling <c>Directory.CreateDirectory</c> on the whole path would happily
    /// follow a junction someone planted, which is exactly the redirection we are defending against.
    /// </summary>
    private bool TryPrepareDirectory(string directory, string destinationRoot, out PathRejection? rejection)
    {
        rejection = null;

        var root = Path.GetFullPath(destinationRoot);
        var full = Path.GetFullPath(directory);

        if (!_pathPolicy.IsInsideDestination(full, root))
        {
            rejection = new PathRejection(
                PathRejectionReason.EscapesDestination,
                directory,
                "The target directory falls outside the destination folder.");
            return false;
        }

        _fileSystem.CreateDirectory(root);

        var relative = Path.GetRelativePath(root, full);
        if (relative is "." or "")
        {
            return true;
        }

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);

            if (_fileSystem.IsReparsePoint(current))
            {
                rejection = new PathRejection(
                    PathRejectionReason.LinkRefused,
                    directory,
                    $"'{segment}' already exists as a link or junction; writing through it is refused.");
                return false;
            }

            _fileSystem.CreateDirectory(current);
        }

        return true;
    }

    /// <summary>Steps 5 and 6: open, re-verify the handle, then stream through the guard.</summary>
    /// <summary>
    /// The TOCTOU defence. Validation ran before the file was opened; between those two moments a
    /// symlink or junction can appear. Asking the operating system where the <em>open handle</em>
    /// actually points is the only answer that cannot be raced.
    /// </summary>
    private void VerifyOpenHandle(Stream file, string expectedPath, string destinationRoot)
    {
        if (!_platform.SupportsHandleVerification)
        {
            if (Interlocked.Exchange(ref _reducedAssuranceReported, 1) == 0)
            {
                LogReducedAssurance(_logger, _platform.Name);
            }

            return;
        }

        if (file is not FileStream stream)
        {
            return;
        }

        var actual = _platform.GetFinalPathOrNull(stream.SafeFileHandle);
        if (actual is null)
        {
            // Fail closed: an unverifiable handle is treated as a failed verification, not a passed one.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.PathRejected,
                "The opened file could not be verified against the destination folder.",
                expectedPath));
        }

        var root = Path.GetFullPath(destinationRoot);
        if (!_pathPolicy.IsInsideDestination(actual, root))
        {
            // Both paths go in the message on purpose. A bare "refused" here is indistinguishable
            // between a real attack and a namespace mismatch — a mapped network drive or a SUBST drive
            // resolves through GetFinalPathNameByHandle to its underlying \\server\share form, which
            // will not match a destination the user expressed as Z:\. Without both strings, that
            // diagnosis takes a debugger; with them it takes one line of a log.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.PathRejected,
                $"The opened file resolved to '{actual}', which is outside the destination folder " +
                $"'{root}'; the write was refused.",
                actual));
        }
    }

    private void ApplyMetadata(ArchiveEntry entry, ExtractionOptions options, string destination)
    {
        if (options.RestoreTimestamps)
        {
            try
            {
                _fileSystem.SetTimestamps(destination, entry.Created, entry.Modified, entry.Accessed);
            }
            catch (ArchiveOperationException ex)
            {
                // Cosmetic: a file with the wrong timestamp is still correct content.
                LogMetadataSkipped(_logger, "timestamps", destination, ex);
            }
        }

        if (options.RestoreAttributes && entry.Attributes is { } attributes)
        {
            try
            {
                _fileSystem.SetAttributes(destination, attributes);
            }
            catch (ArchiveOperationException ex)
            {
                LogMetadataSkipped(_logger, "attributes", destination, ex);
            }
        }

        PropagateMarkOfTheWeb(options, destination);
    }

    /// <summary>
    /// Carries the archive's zone to the extracted file, which is what keeps SmartScreen and Protected
    /// View working downstream. Losing it is a security defect, not a missing nicety (T7).
    /// </summary>
    private void PropagateMarkOfTheWeb(ExtractionOptions options, string destination)
    {
        if (!options.PropagateMarkOfTheWeb || options.SourceArchivePath is null || !_platform.SupportsMarkOfTheWeb)
        {
            return;
        }

        try
        {
            var zone = _platform.ReadZoneIdentifier(options.SourceArchivePath);
            if (!string.IsNullOrEmpty(zone))
            {
                _platform.WriteZoneIdentifier(destination, zone);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkOfTheWebFailed(_logger, destination, ex);
        }
    }

    private void TryDeleteTemp(string temp)
    {
        try
        {
            _fileSystem.DeleteFile(temp);
        }
        catch (ArchiveOperationException ex)
        {
            LogMetadataSkipped(_logger, "temporary-file cleanup", temp, ex);
        }
    }

    /// <summary>One entry between "opened" and "published".</summary>
    private sealed class EntryWriteSession : IEntryWriteSession
    {
        private readonly ExtractionWriter? _writer;
        private readonly ArchiveEntry? _entry;
        private readonly ExtractionOptions? _options;
        private readonly GuardedWriteStream? _guarded;
        private readonly string? _temp;
        private readonly string? _destination;
        private readonly EntryWriteOutcome _outcome;
        private bool _settled;

        private EntryWriteSession(EntryWriteResult refusal) => Refusal = refusal;

        public EntryWriteSession(
            ExtractionWriter writer,
            ArchiveEntry entry,
            ExtractionOptions options,
            GuardedWriteStream guarded,
            string temp,
            string destination,
            EntryWriteOutcome outcome)
        {
            _writer = writer;
            _entry = entry;
            _options = options;
            _guarded = guarded;
            _temp = temp;
            _destination = destination;
            _outcome = outcome;
        }

        public static EntryWriteSession Refused(EntryWriteResult result) => new(result);

        public Stream? Content => _guarded;

        public EntryWriteResult? Refusal { get; }

        public async ValueTask<EntryWriteResult> CompleteAsync(CancellationToken cancellationToken)
        {
            if (Refusal is not null)
            {
                throw new InvalidOperationException("This entry was refused and has nothing to publish.");
            }

            if (_settled)
            {
                throw new InvalidOperationException("This entry has already been settled.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _settled = true;

            try
            {
                // The streaming guard checks are amortised and can fall due after the entry has
                // already ended, so the final size is measured explicitly - while the stream is still
                // open. Nothing has been published yet: a trip here deletes the temp file and the
                // destination name is never created.
                _guarded!.VerifyFinalSize();
                await _guarded.FlushAsync(cancellationToken).ConfigureAwait(false);
                var written = _guarded.BytesWritten;

                await _guarded.DisposeAsync().ConfigureAwait(false);
                return _writer!.Publish(_entry!, _options!, written, _temp!, _destination!, _outcome);
            }
            catch
            {
                // Close the handle before deleting: on Windows an open handle makes the delete fail
                // and leaves the temp file behind in the user's folder.
                await _guarded!.DisposeAsync().ConfigureAwait(false);
                _writer!.TryDeleteTemp(_temp!);
                throw;
            }
        }

        public async ValueTask AbandonAsync()
        {
            if (Refusal is not null || _settled)
            {
                return;
            }

            _settled = true;
            await _guarded!.DisposeAsync().ConfigureAwait(false);
            _writer!.TryDeleteTemp(_temp!);
        }

        // Disposing without settling abandons. Forgetting to publish loses an entry; forgetting to
        // clean up leaves a stray temp file in the user's folder, and only one of those is silent.
        public ValueTask DisposeAsync() => AbandonAsync();
    }
}
