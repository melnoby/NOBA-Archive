using System.Globalization;
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
/// </remarks>
public sealed class ArchivePublisher : IArchivePublisher
{
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
        }

        var staging = Path.Combine(
            directory ?? ".",
            string.Create(CultureInfo.InvariantCulture, $".noba-{Guid.NewGuid():N}.tmp"));

        var stream = _fileSystem.CreateNew(staging);
        return ValueTask.FromResult<IArchivePublication>(
            new ArchivePublication(_fileSystem, stream, staging, full, overwrite));
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
