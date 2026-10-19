using NOBA.Archive.Infrastructure.FileSystem;
using Xunit;

namespace NOBA.Archive.Infrastructure.Tests;

/// <summary>
/// DROP 13 follow-up. Found by reproduction, not by inspection: two concurrent real creations writing
/// different archives into the <em>same</em> directory (<c>OperationLifecycleTests</c>'
/// <c>Two_real_creations_run_concurrently_without_cross_talk</c>) intermittently failed with
/// <c>BackendFailure: Could not find file '...b.zip.noba-write-lock'</c> - one operation's
/// <c>ArchivePublisher</c> sweep (<see cref="ArchivePublisher"/>) enumerating the shared directory
/// while the sibling operation deleted its own lock file a moment later, racing the lazy
/// <c>FileInfo.Length</c> read <see cref="PhysicalFileSystem.EnumerateEntries"/> used to do with no
/// try/catch around it at all. This had been dismissed once before, in DROP 12, as unrelated hardware
/// timing; it was not - rerunning the same scenario until it failed again and reading the exact error
/// found a real, fixable race.
/// </summary>
public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noba-physfs-" + Guid.NewGuid().ToString("N"));

    public PhysicalFileSystemTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Deterministic, not timing-dependent: the enumerator is pumped by hand one step at a time, and
    /// the entry it has not yet reached is deleted in between steps - the exact window the real race
    /// above hit by accident, reproduced on purpose instead of waited for.
    /// </summary>
    [Fact]
    public void Skips_an_entry_deleted_between_being_listed_and_having_its_properties_read()
    {
        var survivor = Path.Combine(_root, "a-survivor.txt");
        var victim = Path.Combine(_root, "b-victim.txt");
        File.WriteAllText(survivor, "keep");
        File.WriteAllText(victim, "doomed");

        var fileSystem = new PhysicalFileSystem();
        using var enumerator = fileSystem
            .EnumerateEntries(_root, recursive: false, CancellationToken.None)
            .GetEnumerator();

        Assert.True(enumerator.MoveNext());
        var firstName = enumerator.Current.Name;

        // Whichever of the two the OS happened to list first - directory order is not guaranteed -
        // delete the OTHER one now, before this loop ever reads its length or attributes.
        var remainingPath = firstName == Path.GetFileName(survivor) ? victim : survivor;
        File.Delete(remainingPath);

        // The real assertion: pumping the rest of the enumerator must not throw, even though the
        // entry it is about to describe no longer exists on disk.
        var seenNames = new List<string> { firstName };
        while (enumerator.MoveNext())
        {
            seenNames.Add(enumerator.Current.Name);
        }

        Assert.False(seenNames.Contains(Path.GetFileName(remainingPath)));
    }

    /// <summary>The ordinary case is untouched: nothing races, every real entry is still reported.</summary>
    [Fact]
    public void Reports_every_entry_when_nothing_races_the_enumeration()
    {
        File.WriteAllText(Path.Combine(_root, "one.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "two.txt"), "2");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var fileSystem = new PhysicalFileSystem();
        var names = fileSystem
            .EnumerateEntries(_root, recursive: false, CancellationToken.None)
            .Select(static e => e.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["one.txt", "sub", "two.txt"], names);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
