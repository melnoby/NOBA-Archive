using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// The real filesystem. Every <c>System.IO.File</c> and <c>Directory</c> call in the product lives
/// behind this type, which is what gives us one place for error translation and path security.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private const int DefaultBufferSize = 1 << 20;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public FileSystemEntryInfo? GetInfo(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return new FileSystemEntryInfo(
                    info.FullName,
                    info.Name,
                    IsDirectory: false,
                    info.Length,
                    info.LastWriteTimeUtc,
                    info.Attributes);
            }

            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return new FileSystemEntryInfo(
                    info.FullName,
                    info.Name,
                    IsDirectory: true,
                    Length: 0,
                    info.LastWriteTimeUtc,
                    info.Attributes);
            }

            return null;
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public IEnumerable<FileSystemEntryInfo> EnumerateEntries(string directory, bool recursive, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            // Never walk into a reparse point during enumeration: that is how a junction turns a
            // bounded scan into an unbounded one, and how it escapes the folder the user chose.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerable<FileSystemInfo> source;
        try
        {
            source = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options);
        }
        catch (Exception ex)
        {
            throw Translate(ex, directory);
        }

        foreach (var info in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = info is DirectoryInfo;
            yield return new FileSystemEntryInfo(
                info.FullName,
                info.Name,
                isDirectory,
                isDirectory ? 0 : ((FileInfo)info).Length,
                info.LastWriteTimeUtc,
                info.Attributes);
        }
    }

    public Stream OpenRead(string path)
    {
        try
        {
            return new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = DefaultBufferSize,
                    Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                });
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public Stream CreateNew(string path, long? preallocateBytes = null)
    {
        try
        {
            var options = new FileStreamOptions
            {
                // CreateNew, never Create: an accidental overwrite must be impossible here. Overwriting
                // only ever happens through an explicit, user-chosen policy.
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = DefaultBufferSize,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            };

            if (preallocateBytes is > 0)
            {
                options.PreallocationSize = preallocateBytes.Value;
            }

            return new FileStream(path, options);
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public void CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite);
        }
        catch (Exception ex)
        {
            throw Translate(ex, destinationPath);
        }
    }

    public void ReplaceFile(string sourcePath, string destinationPath, string? backupPath)
    {
        try
        {
            // File.Replace requires the destination to exist; callers publishing a brand-new file
            // must use MoveFile. Keeping the distinction explicit avoids a whole class of surprise.
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
        }
        catch (Exception ex)
        {
            throw Translate(ex, destinationPath);
        }
    }

    public void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        try
        {
            Directory.Delete(path, recursive);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone: deleting is idempotent by design, cleanup paths depend on it.
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public void SetTimestamps(string path, DateTimeOffset? created, DateTimeOffset? modified, DateTimeOffset? accessed)
    {
        try
        {
            if (created is { } c)
            {
                File.SetCreationTimeUtc(path, c.UtcDateTime);
            }

            if (modified is { } m)
            {
                File.SetLastWriteTimeUtc(path, m.UtcDateTime);
            }

            if (accessed is { } a)
            {
                File.SetLastAccessTimeUtc(path, a.UtcDateTime);
            }
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        try
        {
            // Never restore ReparsePoint from archive metadata: link creation is a separate, guarded
            // decision, not an attribute we replay.
            File.SetAttributes(path, attributes & ~FileAttributes.ReparsePoint);
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    public VolumeSpace GetVolumeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            var drive = new DriveInfo(string.IsNullOrEmpty(root) ? Path.GetFullPath(path) : root);
            return new VolumeSpace(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    /// <summary>Turns an I/O exception into a typed <see cref="ArchiveError"/>; no raw exception reaches the UI.</summary>
    internal static ArchiveOperationException Translate(Exception ex, string path)
    {
        var code = ex switch
        {
            UnauthorizedAccessException => ArchiveErrorCode.AccessDenied,
            PathTooLongException => ArchiveErrorCode.PathTooLong,
            FileNotFoundException or DirectoryNotFoundException => ArchiveErrorCode.IoFailure,
            IOException io when IsDiskFull(io) => ArchiveErrorCode.DiskFull,
            IOException => ArchiveErrorCode.IoFailure,
            NotSupportedException or ArgumentException => ArchiveErrorCode.PathRejected,
            _ => ArchiveErrorCode.IoFailure,
        };

        return new ArchiveOperationException(ArchiveError.Create(code, ex.Message, path, ex));
    }

    private static bool IsDiskFull(IOException ex)
    {
        // ERROR_DISK_FULL (0x70) and ERROR_HANDLE_DISK_FULL (0x27), as surfaced in HResult.
        const int DiskFull = unchecked((int)0x80070070);
        const int HandleDiskFull = unchecked((int)0x80070027);
        const int PosixNoSpace = 28;
        return ex.HResult == DiskFull || ex.HResult == HandleDiskFull || ex.HResult == PosixNoSpace;
    }
}
