using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// DROP 13 follow-up, closing <c>L39</c>. <c>ArchivePublisherTests</c> already proved this exact shape
/// for <c>ArchivePublisher</c>'s own staging file (DROP 12): a process killed mid-write leaves
/// <c>.noba-{pid}-{guid}.tmp</c> behind, because disposal never runs, and the next writer into that
/// same directory sweeps it before opening its own. These three prove the mirror image now holds for
/// <c>ExtractionWriter</c>'s per-entry temp file, the one half of "a killed process leaves a
/// <c>.noba-*.tmp</c> behind forever" DROP 12 deliberately left open.
/// </summary>
public sealed class ExtractionOrphanSweepTests
{
    /// <summary>A crash leaves a temp file behind; the next extraction into that directory reclaims it.</summary>
    [Fact]
    public async Task Sweeps_a_temp_file_left_by_a_process_that_no_longer_exists()
    {
        using var sandbox = new TempDirectory("EXT-ORPHAN-01");
        var source = await WriteFile(sandbox.Combine("notes.txt"), "content");

        var buildEngine = new EngineFixture();
        var archive = sandbox.Combine("source.zip");
        await buildEngine.CreateAsync(archive, [source]);
        await buildEngine.DisposeAsync();

        var destination = sandbox.Combine("out");
        Directory.CreateDirectory(destination);

        // Far past any PID this machine will hand out in the test's lifetime - same convention
        // ArchivePublisherTests already uses for "definitely not a real process".
        var orphan = Path.Combine(destination, ".noba-2147483645-" + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(orphan, "abandoned mid-write");

        await using var engine = new EngineFixture();
        await engine.ExtractAsync(archive, destination);

        Assert.False(File.Exists(orphan), "a temp file left by a dead process should have been swept");
        Assert.True(File.Exists(Path.Combine(destination, "notes.txt")));
    }

    /// <summary>A live process's own temp file must survive a concurrent extraction's sweep.</summary>
    [Fact]
    public async Task Leaves_a_temp_file_owned_by_a_live_process_alone()
    {
        using var sandbox = new TempDirectory("EXT-ORPHAN-02");
        var source = await WriteFile(sandbox.Combine("notes.txt"), "content");

        var buildEngine = new EngineFixture();
        var archive = sandbox.Combine("source.zip");
        await buildEngine.CreateAsync(archive, [source]);
        await buildEngine.DisposeAsync();

        var destination = sandbox.Combine("out");
        Directory.CreateDirectory(destination);

        var owned = Path.Combine(destination, $".noba-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(owned, "still in flight");

        await using var engine = new EngineFixture();
        await engine.ExtractAsync(archive, destination);

        Assert.True(File.Exists(owned), "a live process's own temp file must not be swept");
    }

    /// <summary>A file that merely looks similar - wrong shape - is not touched by the sweep at all.</summary>
    [Fact]
    public async Task Leaves_an_unrelated_file_alone()
    {
        using var sandbox = new TempDirectory("EXT-ORPHAN-03");
        var source = await WriteFile(sandbox.Combine("notes.txt"), "content");

        var buildEngine = new EngineFixture();
        var archive = sandbox.Combine("source.zip");
        await buildEngine.CreateAsync(archive, [source]);
        await buildEngine.DisposeAsync();

        var destination = sandbox.Combine("out");
        Directory.CreateDirectory(destination);

        var unrelated = Path.Combine(destination, "keep-me.txt");
        await File.WriteAllTextAsync(unrelated, "keep me");

        await using var engine = new EngineFixture();
        await engine.ExtractAsync(archive, destination);

        Assert.True(File.Exists(unrelated));
    }

    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
