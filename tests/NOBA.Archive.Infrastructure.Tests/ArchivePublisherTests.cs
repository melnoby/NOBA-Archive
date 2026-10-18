using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Infrastructure.FileSystem;
using Xunit;

namespace NOBA.Archive.Infrastructure.Tests;

/// <summary>
/// DROP 12: a thrown exception already left no staging file behind (<c>ArchivePublication.DisposeAsync</c>
/// cleans it up) — these prove the case that never ran that cleanup at all, a process that was killed
/// rather than one that unwound normally. Same liveness check <c>TempWorkspaceTests</c> already proves
/// for workspace directories (<see cref="ProcessLiveness"/>), now for the staging file beside a
/// destination archive.
/// </summary>
public sealed class ArchivePublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noba-publisher-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileSystem _fileSystem = new();

    public ArchivePublisherTests() => Directory.CreateDirectory(_root);

    private string Combine(string name) => Path.Combine(_root, name);

    /// <summary>A crash leaves a staging file behind; the next publish to the same directory reclaims it.</summary>
    [Fact]
    public async Task Sweeps_a_staging_file_left_by_a_process_that_no_longer_exists()
    {
        // Far past any PID this machine will hand out in the test's lifetime - same convention
        // TempWorkspaceTests already uses for "definitely not a real process".
        var orphan = Combine(".noba-2147483645-" + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(orphan, "abandoned mid-write");
        Assert.True(File.Exists(orphan));

        var publisher = new ArchivePublisher(_fileSystem);
        await using var publication = await publisher.BeginAsync(Combine("out.zip"), overwrite: false, CancellationToken.None);

        Assert.False(File.Exists(orphan));
    }

    /// <summary>A live process's own staging file must survive a concurrent publish's sweep.</summary>
    [Fact]
    public async Task Leaves_a_staging_file_owned_by_a_live_process_alone()
    {
        var owned = Combine($".noba-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(owned, "still in flight");

        var publisher = new ArchivePublisher(_fileSystem);
        await using var publication = await publisher.BeginAsync(Combine("out.zip"), overwrite: false, CancellationToken.None);

        Assert.True(File.Exists(owned));
    }

    /// <summary>A file that merely looks similar - wrong shape - is not touched by the sweep at all.</summary>
    [Fact]
    public async Task Leaves_an_unrelated_file_alone()
    {
        var unrelated = Combine("notes.txt");
        await File.WriteAllTextAsync(unrelated, "keep me");

        var publisher = new ArchivePublisher(_fileSystem);
        await using var publication = await publisher.BeginAsync(Combine("out.zip"), overwrite: false, CancellationToken.None);

        Assert.True(File.Exists(unrelated));
    }

    /// <summary>The ordinary path still works: publish lands the archive and leaves no staging file.</summary>
    [Fact]
    public async Task A_normal_publish_still_lands_the_archive_and_leaves_nothing_staged()
    {
        var target = Combine("out.zip");
        var publisher = new ArchivePublisher(_fileSystem);
        await using var publication = await publisher.BeginAsync(target, overwrite: false, CancellationToken.None);

        await using (var writer = new StreamWriter(publication.Output, leaveOpen: true))
        {
            await writer.WriteAsync("content");
        }

        await publication.PublishAsync(CancellationToken.None);

        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_root, ".noba-*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
