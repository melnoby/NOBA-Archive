using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// EXT-018/019: the two write failures <c>docs/PRODUCT_PLAN.md</c>'s L17 names as in scope for
/// extraction — disk-full and permission-denied, both <em>during the write itself</em> — were
/// previously unexercised. EXT-017 (<see cref="ExtractionDiskSpaceTests"/>) proves the declared-total-
/// vs-free-space pre-flight guard refuses before a byte is written. That guard reads real free space
/// at the moment the extraction starts; it cannot see the volume run out a second later, and it says
/// nothing about a destination file that simply cannot be opened. These two prove the write path
/// itself — <c>ExtractionWriter</c>, past the pre-flight check — still fails cleanly in both cases.
/// </summary>
/// <remarks>
/// Same fault-injection seam as <see cref="CreationFailureInjectionTests"/>, reused rather than
/// duplicated: <see cref="FaultInjectingFileSystem"/> already wraps <c>CreateNew</c> — the call
/// <c>ExtractionWriter.BeginEntryAsync</c> makes to open its staging file, the exact same method
/// <c>CreateArchiveHandler</c> calls to open its own staging file — which is why one fault-injection
/// class serves both call sites untouched.
/// </remarks>
public sealed class ExtractionFailureInjectionTests
{
    /// <summary>Nothing published under the entry's real name, and no staging file left behind.</summary>
    private static void AssertNothingLandedAt(string destinationFile, TempDirectory sandbox)
    {
        Assert.False(File.Exists(destinationFile), $"a refused extraction published '{destinationFile}'");

        foreach (var leftover in Directory.EnumerateFiles(sandbox.Path, ".noba-*.tmp", SearchOption.AllDirectories))
        {
            Assert.Fail($"a staging file survived the failure: {leftover}");
        }
    }

    /// <summary>
    /// EXT-018: the pre-flight estimate passes — the sandbox disk has plenty of real free space — but
    /// the volume still runs out partway through writing this entry's bytes, not at the moment its
    /// staging file was opened.
    /// </summary>
    [Fact]
    public async Task A_volume_that_fills_up_mid_write_during_extraction_fails_cleanly_and_leaves_nothing()
    {
        using var sandbox = new TempDirectory("EXT-018");
        var source = sandbox.Combine("large.bin");

        // Incompressible and comfortably larger than the write budget below, so the fault fires while
        // this entry is still being written, not only once it is already complete.
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(8 * 1024 * 1024));

        var buildEngine = new EngineFixture();
        var archive = sandbox.Combine("source.zip");
        await buildEngine.CreateAsync(archive, [source]);
        await buildEngine.DisposeAsync();

        var destination = sandbox.Combine("out");

        var diskFull = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.DiskFull,
            "There is not enough space on the destination volume.",
            destination));

        var fileSystem = new FaultInjectingFileSystem(
            new PhysicalFileSystem(),
            writeFaultAfterBytes: 32 * 1024,
            writeFaultError: diskFull);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, destination));

        Assert.Equal(ArchiveErrorCode.DiskFull, failure.Error.Code);
        AssertNothingLandedAt(Path.Combine(destination, "large.bin"), sandbox);
    }

    /// <summary>
    /// EXT-019: this entry's destination cannot be opened for writing at all — an ACL or a read-only
    /// location, discovered before a single byte of it is produced.
    /// </summary>
    [Fact]
    public async Task A_destination_that_cannot_be_opened_during_extraction_fails_cleanly_and_leaves_nothing()
    {
        using var sandbox = new TempDirectory("EXT-019");
        var source = await WriteFile(sandbox.Combine("notes.txt"), "content");

        var buildEngine = new EngineFixture();
        var archive = sandbox.Combine("source.zip");
        await buildEngine.CreateAsync(archive, [source]);
        await buildEngine.DisposeAsync();

        var destination = sandbox.Combine("out");

        var accessDenied = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.AccessDenied,
            "Access to the destination is denied.",
            destination));

        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem(), createNewError: accessDenied);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, destination));

        Assert.Equal(ArchiveErrorCode.AccessDenied, failure.Error.Code);
        AssertNothingLandedAt(Path.Combine(destination, "notes.txt"), sandbox);
    }

    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }
}
