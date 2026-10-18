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
/// </remarks>
public sealed class ArchivePublisher : IArchivePublisher
{
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

        if (!overwrite && _fileSystem.FileExists(full))
        {
            // Refused up front: discovering it after compressing a gigabyte would be worse.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.IoFailure,
                $"'{targetPath}' already exists.",
                targetPath));
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
            SweepOrphanedStagingFiles(directory, cancellationToken);
        }

        var staging = Path.Combine(
            directory ?? ".",
            string.Create(CultureInfo.InvariantCulture, $".noba-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp"));

        var stream = _fileSystem.CreateNew(staging);
        return ValueTask.FromResult<IArchivePublication>(
            new ArchivePublication(_fileSystem, stream, staging, full, overwrite));
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
        private bool _published;

        public ArchivePublication(
            IFileSystem fileSystem,
            Stream output,
            string stagingPath,
            string targetPath,
            bool overwrite)
        {
            _fileSystem = fileSystem;
            Output = output;
            _stagingPath = stagingPath;
            TargetPath = targetPath;
            _overwrite = overwrite;
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
        }

        public async ValueTask DisposeAsync()
        {
            await Output.DisposeAsync().ConfigureAwait(false);

            if (_published)
            {
                return;
            }

            try
            {
                _fileSystem.DeleteFile(_stagingPath);
            }
            catch (ArchiveOperationException)
            {
                // A leaked staging file is untidy; failing an already-failing operation over it is worse.
            }
        }
    }
}
