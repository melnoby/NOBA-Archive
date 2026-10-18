using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// DROP 12 — Recovery and Failure Safety: <see cref="ManageArchiveTests"/> already has MAN-007 (a
/// write failure mid-rewrite), the mirror of CRT-014 for Create. These are the remaining mirrors —
/// access-denied and a locked source — plus cancellation, none of which Manage had any coverage for
/// before this drop despite the propagation code (<c>cancellationToken.ThrowIfCancellationRequested()</c>
/// in both the restage and rebuild loops) already being structurally present.
/// </summary>
public sealed class ManageArchiveFailureInjectionTests
{
    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    private static void AssertNoStagingFileSurvived(TempDirectory sandbox)
    {
        foreach (var leftover in Directory.EnumerateFiles(sandbox.Path, ".noba-*.tmp", SearchOption.AllDirectories))
        {
            Assert.Fail($"a staging file survived the failure: {leftover}");
        }
    }

    /// <summary>
    /// MAN-011: the mirror of CRT-015 for an update — a write fails outright rather than mid-stream
    /// (an ACL or a read-only location), discovered somewhere in the rewrite. The original archive must
    /// come through untouched either way, which is the property this test actually proves; it does not
    /// claim to isolate which specific temp file the fault landed on, since the existing fault-injection
    /// seam fires on the first <c>CreateNew</c> call regardless of whether that is a restaged entry's
    /// scratch file or the replacement archive's own staging file.
    /// </summary>
    [Fact]
    public async Task A_write_that_cannot_open_at_all_leaves_the_original_archive_untouched()
    {
        using var sandbox = new TempDirectory("MAN-011");
        var a = await WriteFile(sandbox.Combine("a.txt"), "original content");
        var archive = sandbox.Combine("out.zip");

        await using (var setup = new EngineFixture())
        {
            await setup.CreateAsync(archive, [a]);
        }

        var before = await File.ReadAllBytesAsync(archive);

        var accessDenied = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.AccessDenied,
            "Access to the destination is denied.",
            archive));

        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem(), createNewError: accessDenied);

        var newFile = await WriteFile(sandbox.Combine("new.txt"), "added content");

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(archive, add: [newFile]));

        Assert.Equal(ArchiveErrorCode.AccessDenied, failure.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoStagingFileSurvived(sandbox);
    }

    /// <summary>
    /// MAN-012: the mirror of CRT-016 for an update — the archive being updated is itself the "source"
    /// here (unlike Create, where sources are separate files), locked by another process at the moment
    /// NOBA Archive tries to read it to begin the rewrite.
    /// </summary>
    [Fact]
    public async Task An_archive_locked_by_another_process_fails_cleanly_and_leaves_it_untouched()
    {
        using var sandbox = new TempDirectory("MAN-012");
        var a = await WriteFile(sandbox.Combine("a.txt"), "original content");
        var archive = sandbox.Combine("out.zip");

        await using (var setup = new EngineFixture())
        {
            await setup.CreateAsync(archive, [a]);
        }

        var before = await File.ReadAllBytesAsync(archive);

        var sharingViolation = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.AccessDenied,
            "The process cannot access the file because it is being used by another process.",
            archive));

        var fileSystem = new FaultInjectingFileSystem(new PhysicalFileSystem(), openReadError: sharingViolation);

        var newFile = await WriteFile(sandbox.Combine("new.txt"), "added content");

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(archive, add: [newFile]));

        Assert.Equal(ArchiveErrorCode.AccessDenied, failure.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoStagingFileSurvived(sandbox);
    }

    /// <summary>
    /// MAN-013: cancellation mid-rewrite must not leave the original archive half-replaced, the same
    /// guarantee INT-013 already proves for Create. Nothing in <see cref="ManageArchiveTests"/> had ever
    /// exercised <c>ManageArchiveHandler</c>'s cancellation checks before this drop.
    /// </summary>
    [Fact]
    public async Task Cancellation_mid_rewrite_leaves_the_original_archive_untouched()
    {
        using var sandbox = new TempDirectory("MAN-013");
        var a = await WriteFile(sandbox.Combine("a.txt"), "original content");
        var archive = sandbox.Combine("out.zip");

        await using (var setup = new EngineFixture())
        {
            await setup.CreateAsync(archive, [a]);
        }

        var before = await File.ReadAllBytesAsync(archive);

        // Several new files, so the rebuild loop reports progress more than once - enough room for the
        // cancellation to land mid-loop rather than being indistinguishable from cancelling before the
        // call even started (the same reasoning INT-013 documents for Create).
        var newFiles = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            newFiles.Add(await WriteFile(sandbox.Combine($"new{i}.txt"), $"added content {i}"));
        }

        using var cancellation = new CancellationTokenSource();
        var seen = 0;
        var progress = new SynchronousProgress<OperationProgress>(_ =>
        {
            if (++seen >= 3)
            {
                cancellation.Cancel();
            }
        });

        await using var engine = new EngineFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ManageAsync(
            archive,
            add: newFiles,
            progress: progress,
            cancellationToken: cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        AssertNoStagingFileSurvived(sandbox);
    }
}
