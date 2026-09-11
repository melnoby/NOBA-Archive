namespace NOBA.Archive.Core.Abstractions;

public sealed record FileSystemEntryInfo(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Length,
    DateTimeOffset LastWriteTime,
    FileAttributes Attributes)
{
    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
}

public sealed record VolumeSpace(long TotalBytes, long AvailableFreeBytes);

/// <summary>
/// The only sanctioned filesystem surface. <c>System.IO.File</c> and <c>Directory</c> are banned
/// outside NOBA.Archive.Infrastructure so that error translation, cancellation, and path security
/// all live in one place.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>True when the path exists and carries a reparse point (symlink or junction).</summary>
    bool IsReparsePoint(string path);

    FileSystemEntryInfo? GetInfo(string path);

    IEnumerable<FileSystemEntryInfo> EnumerateEntries(string directory, bool recursive, CancellationToken cancellationToken);

    Stream OpenRead(string path);

    /// <summary>Creates a new file and fails if it already exists. Never silently overwrites.</summary>
    Stream CreateNew(string path, long? preallocateBytes = null);

    void CreateDirectory(string path);

    /// <summary>Publishes a staged temp file. Use <paramref name="overwrite"/> only under an explicit policy.</summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Replaces an existing file, optionally keeping a backup. Throws when the destination is absent.</summary>
    void ReplaceFile(string sourcePath, string destinationPath, string? backupPath);

    void DeleteFile(string path);

    void DeleteDirectory(string path, bool recursive);

    void SetTimestamps(string path, DateTimeOffset? created, DateTimeOffset? modified, DateTimeOffset? accessed);

    void SetAttributes(string path, FileAttributes attributes);

    VolumeSpace GetVolumeSpace(string path);
}
