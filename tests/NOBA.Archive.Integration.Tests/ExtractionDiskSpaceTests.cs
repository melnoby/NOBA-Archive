using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// EXT-017: THREAT_MODEL.md T3 lists "total expanded size vs free disk → block with the disk-space
/// dialog" as its own mitigation, separate from the ratio/count/memory heuristics the rest of T3 covers
/// — and, unlike those, documented as a refusal with no override, because a real volume is a physical
/// limit rather than a heuristic a legitimate huge archive could trip by accident.
/// </summary>
/// <remarks>
/// Before this test there was nothing anywhere exercising the "exceeds free space" row of that table
/// specifically: the only guard with real coverage (<c>ResourceGuardTests.cs</c>) checks a fixed,
/// configured ceiling (2048 GiB by default), never the destination volume's actual free space, and
/// <c>DiskSpaceEstimator</c> — the one piece of code that reads it — was registered in the container
/// but called from nowhere. This proves the two are now connected, and proves it the same way
/// <c>CreationFailureInjectionTests</c> proves the Create side: not at the OS level (nothing here can
/// safely fill a shared test machine's disk), but at the one seam <see cref="IFileSystem"/> a real
/// free-space reading would arrive through.
/// </remarks>
public sealed class ExtractionDiskSpaceTests
{
    /// <summary>
    /// The destination volume does not have enough free space for what the archive declares, so the
    /// extraction is refused before a single byte - or even a directory - is written.
    /// </summary>
    [Fact]
    public async Task An_archive_that_would_not_fit_is_refused_before_anything_is_written()
    {
        using var sandbox = new TempDirectory("EXT-017");
        var source = sandbox.Combine("payload", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, new string('a', 4096));

        var buildEngine = new EngineFixture();
        var archive = sandbox.Combine("source.zip");
        await buildEngine.CreateAsync(archive, [Path.GetDirectoryName(source)!]);
        await buildEngine.DisposeAsync();

        var destination = sandbox.Combine("out");

        // Smaller than the archive's own declared uncompressed total, so the pre-flight check has to
        // trip regardless of how little this particular test archive actually contains.
        var starvedSpace = new VolumeSpace(TotalBytes: 1024, AvailableFreeBytes: 1);
        var fileSystem = new VolumeSpaceOverrideFileSystem(new PhysicalFileSystem(), starvedSpace);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, destination));

        Assert.Equal(ArchiveErrorCode.DiskFull, failure.Error.Code);
        Assert.False(Directory.Exists(destination), "a refused extraction created the destination tree");
    }
}

/// <summary>Wraps a real <see cref="IFileSystem"/> and reports a fixed <see cref="VolumeSpace"/> instead
/// of the real one, so a test can simulate a starved volume without touching an actual disk.</summary>
internal sealed class VolumeSpaceOverrideFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly VolumeSpace _space;

    public VolumeSpaceOverrideFileSystem(IFileSystem inner, VolumeSpace space)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _space = space;
    }

    public VolumeSpace GetVolumeSpace(string path) => _space;

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public bool IsReparsePoint(string path) => _inner.IsReparsePoint(path);

    public FileSystemEntryInfo? GetInfo(string path) => _inner.GetInfo(path);

    public IEnumerable<FileSystemEntryInfo> EnumerateEntries(string directory, bool recursive, CancellationToken cancellationToken) =>
        _inner.EnumerateEntries(directory, recursive, cancellationToken);

    public Stream OpenRead(string path) => _inner.OpenRead(path);

    public Stream CreateNew(string path, long? preallocateBytes = null) => _inner.CreateNew(path, preallocateBytes);

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        _inner.MoveFile(sourcePath, destinationPath, overwrite);

    public void ReplaceFile(string sourcePath, string destinationPath, string? backupPath) =>
        _inner.ReplaceFile(sourcePath, destinationPath, backupPath);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public void DeleteDirectory(string path, bool recursive) => _inner.DeleteDirectory(path, recursive);

    public void SetTimestamps(string path, DateTimeOffset? created, DateTimeOffset? modified, DateTimeOffset? accessed) =>
        _inner.SetTimestamps(path, created, modified, accessed);

    public void SetAttributes(string path, FileAttributes attributes) => _inner.SetAttributes(path, attributes);
}
