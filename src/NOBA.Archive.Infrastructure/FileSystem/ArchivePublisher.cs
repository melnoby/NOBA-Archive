using System.Globalization;
using System.Text.RegularExpressions;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// Writes a new archive to a staging file beside its destination and publishes it atomically.
/// </summary>
/// <remarks>
/// <para>
/// The mirror image of <see cref="ExtractionWriter"/>. Extraction has one component that owns the
/// filesystem so a backend cannot write outside the destination; creation has one for a different but
/// equally concrete reason: "compress and replace" must not be able to destroy the archive it was
/// updating. An interrupted create leaves the staging file, never a truncated archive under the real
/// name — and combined with <c>DeleteSourcesAfterVerifiedSuccess</c>, that is the difference between a
/// failed operation and lost data.
/// </para>
/// <para>
/// Keeping it here rather than in a provider is what lets the architecture test ban
/// <c>System.IO.File</c> from NOBA.Archive.Formats outright, so the rule is machine-checked instead of
/// remembered.
/// </para>
/// <para>
/// <b>DROP 12.</b> "An interrupted create leaves the staging file" was true for a thrown exception —
/// <see cref="ArchivePublication.DisposeAsync"/> cleans it up — but never for a killed process, which
/// skips disposal entirely. The staging name now carries the PID that created it
/// (<c>.noba-{pid}-{guid}.tmp</c>), and <see cref="BeginAsync"/> sweeps same-directory siblings whose
/// PID belongs to a process that no longer exists before opening its own. This is the same liveness
/// check <see cref="TempWorkspaceFactory.SweepOrphans"/> already uses for workspace directories
/// (<see cref="ProcessLiveness"/>), now covering the one staging file that mechanism never reached.
/// </para>
/// <para>
/// <b>DROP 13.</b> The CLI runs one process per invocation and builds no <c>IOperationQueue</c>
/// (ARCHITECTURE.md §8.4) — nothing there serialises two overlapping <c>Create</c>/<c>Manage</c>
/// calls against the same destination, and a shell context-menu double-click is exactly two such
/// calls. <see cref="BeginAsync"/> used to do a plain check-then-act: test <c>FileExists</c>, then
/// race the real write against anything else doing the same, discovering the collision only at the
/// final <c>MoveFile</c> — after however long the compression took. It now claims a sibling
/// <c>.noba-write-lock</c> file for the whole lifetime of the publication (<see cref="AcquireLock"/>),
/// the same PID-stamped-and-swept shape as the staging file above, so a second concurrent writer is
/// refused up front instead of losing a race after doing the work.
/// </para>
/// </remarks>
public sealed class ArchivePublisher : IArchivePublisher
{
    private const string LockSuffix = ".noba-write-lock";

    private static readonly Regex StagingFileName = new(
        @"^\.noba-(?<pid>\d+)-[0-9a-fA-F]{32}\.tmp$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IFileSystem _fileSystem;

    public ArchivePublisher(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    public ValueTask<IArchivePublication> BeginAsync(
        string targetPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        var full = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
            SweepOrphanedStagingFiles(directory, cancellationToken);
        }

        var lockPath = full + LockSuffix;
        var writeLock = AcquireLock(lockPath);

        try
        {
            if (!overwrite && _fileSystem.FileExists(full))
            {
                // Refused up front: discovering it after compressing a gigabyte would be worse.
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.IoFailure,
                    $"'{targetPath}' already exists.",
                    targetPath));
            }

            var staging = Path.Combine(
                directory ?? ".",
                string.Create(CultureInfo.InvariantCulture, $".noba-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp"));

            var stream = _fileSystem.CreateNew(staging);
            return ValueTask.FromResult<IArchivePublication>(
                new ArchivePublication(_fileSystem, stream, staging, full, overwrite, writeLock, lockPath));
        }
        catch
        {
            ReleaseLock(writeLock, lockPath);
            throw;
        }
    }

    /// <summary>
    /// Claims <paramref name="lockPath"/> for the life of this publication. A live holder is refused
    /// cleanly; a lock left by a process that no longer exists is swept and retried once, the same
    /// liveness check <see cref="SweepOrphanedStagingFiles"/> uses for the staging file itself.
    /// </summary>
    private Stream AcquireLock(string lockPath)
    {
        try
        {
            var stream = _fileSystem.CreateNew(lockPath);
            WriteOwnerPid(stream);
            return stream;
        }
        catch (ArchiveOperationException) when (TryReclaimOrphanedLock(lockPath))
        {
            // Fall through to the single retry below, now that the dead holder's lock file is gone.
        }
        catch (ArchiveOperationException)
        {
            throw ClaimedElsewhere(lockPath);
        }

        try
        {
            var stream = _fileSystem.CreateNew(lockPath);
            WriteOwnerPid(stream);
            return stream;
        }
        catch (ArchiveOperationException)
        {
            // Lost the retry race to whoever else just claimed it - same clean refusal either way.
            throw ClaimedElsewhere(lockPath);
        }
    }

    private static ArchiveOperationException ClaimedElsewhere(string lockPath)
    {
        var target = lockPath[..^LockSuffix.Length];
        return new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.AccessDenied,
            $"'{target}' is already being written by another NOBA Archive operation.",
            target));
    }

    /// <summary>True when a stale lock belonging to a dead process was found and removed.</summary>
    private bool TryReclaimOrphanedLock(string lockPath)
    {
        int pid;
        try
        {
            using var read = _fileSystem.OpenRead(lockPath);
            using var reader = new StreamReader(read);
            if (!int.TryParse(reader.ReadToEnd(), CultureInfo.InvariantCulture, out pid))
            {
                return false;
            }
        }
        catch (ArchiveOperationException)
        {
            // Gone already, or unreadable for a reason that is not "a live process holds it" -
            // either way, the next CreateNew attempt is the real answer; do not claim success here.
            return false;
        }

        if (ProcessLiveness.IsAlive(pid))
        {
            return false;
        }

        try
        {
            _fileSystem.DeleteFile(lockPath);
            return true;
        }
        catch (ArchiveOperationException)
        {
            return false;
        }
    }

    private static void WriteOwnerPid(Stream stream)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private void ReleaseLock(Stream writeLock, string lockPath)
    {
        writeLock.Dispose();
        try
        {
            _fileSystem.DeleteFile(lockPath);
        }
        catch (ArchiveOperationException)
        {
            // Best-effort, same as every other cleanup in this file: an untidy lock file is not
            // worth failing an already-decided outcome over.
        }
    }

    /// <summary>
    /// Best-effort: a staging file this sweep fails to remove is untidy, not unsafe, so nothing here
    /// is allowed to fail the real operation that is about to start.
    /// </summary>
    private void SweepOrphanedStagingFiles(string directory, CancellationToken cancellationToken)
    {
        if (!_fileSystem.DirectoryExists(directory))
        {
            return;
        }

        foreach (var entry in _fileSystem.EnumerateEntries(directory, recursive: false, cancellationToken))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var match = StagingFileName.Match(entry.Name);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["pid"].Value, CultureInfo.InvariantCulture, out var pid) ||
                ProcessLiveness.IsAlive(pid))
            {
                continue;
            }

            try
            {
                _fileSystem.DeleteFile(entry.FullPath);
            }
            catch (ArchiveOperationException)
            {
                // Someone else may be touching it right now; leave it for the next sweep.
            }
        }
    }

    private sealed class ArchivePublication : IArchivePublication
    {
        private readonly IFileSystem _fileSystem;
        private readonly string _stagingPath;
        private readonly bool _overwrite;
        private readonly Stream _writeLock;
        private readonly string _lockPath;
        private bool _published;
        private bool _lockReleased;

        public ArchivePublication(
            IFileSystem fileSystem,
            Stream output,
            string stagingPath,
            string targetPath,
            bool overwrite,
            Stream writeLock,
            string lockPath)
        {
            _fileSystem = fileSystem;
            Output = output;
            _stagingPath = stagingPath;
            TargetPath = targetPath;
            _overwrite = overwrite;
            _writeLock = writeLock;
            _lockPath = lockPath;
        }

        public Stream Output { get; }

        public string TargetPath { get; }

        public async ValueTask PublishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The provider has finished with the stream by now; the bytes have to be on disk before
            // the rename, or the published archive would be missing its tail.
            await Output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Output.DisposeAsync().ConfigureAwait(false);

            _fileSystem.MoveFile(_stagingPath, TargetPath, _overwrite);
            _published = true;

            // Released here rather than waiting for DisposeAsync: the rename above just made the
            // destination the real archive, and there is nothing left for the lock to protect a
            // second writer against. Waiting for whatever later disposes this object would hold it
            // open for no reason - and in the CLI's case, that disposal can be a while after this.
            await ReleaseLockAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Output.DisposeAsync().ConfigureAwait(false);

            if (!_published)
            {
                try
                {
                    _fileSystem.DeleteFile(_stagingPath);
                }
                catch (ArchiveOperationException)
                {
                    // A leaked staging file is untidy; failing an already-failing operation over it is worse.
                }
            }

            await ReleaseLockAsync().ConfigureAwait(false);
        }

        /// <summary>Idempotent: <see cref="PublishAsync"/> already releases on success, so a later
        /// <see cref="DisposeAsync"/> on the same successful publication must be a no-op here, not a
        /// second attempt to dispose an already-disposed stream and delete an already-gone file.</summary>
        private async ValueTask ReleaseLockAsync()
        {
            if (_lockReleased)
            {
                return;
            }

            _lockReleased = true;
            await _writeLock.DisposeAsync().ConfigureAwait(false);
            try
            {
                _fileSystem.DeleteFile(_lockPath);
            }
            catch (ArchiveOperationException)
            {
                // Best-effort, same reasoning as the staging-file cleanup above.
            }
        }
    }
}
