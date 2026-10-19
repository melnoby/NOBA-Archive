using System.Diagnostics;
using NOBA.Archive.Infrastructure.FileSystem;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// DROP 13, closing <c>L40</c>. Every "crash recovery" test elsewhere in this codebase, including
/// DROP 12's <c>ArchivePublisherTests</c>, proves the sweep <em>mechanism</em> by hand-placing a file
/// stamped with a PID that does not exist and checking it gets removed - a real, deterministic proof
/// of the liveness check, but never of the one thing that check exists to survive: an actual process
/// dying mid-write. This spawns the real <c>noba-ar</c> binary, kills it with a real SIGKILL while it
/// is provably still writing, and only then lets the production sweep run against what that kill
/// really left behind.
/// </summary>
public sealed class RealProcessCrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noba-crash-" + Guid.NewGuid().ToString("N"));

    public RealProcessCrashTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Large enough, and incompressible enough, that the staging file is guaranteed to exist for
    /// long enough to observe and kill - not a timing assertion on how long the <em>whole</em>
    /// operation takes (which a shared CI machine cannot promise), just a lower bound on how much
    /// work stands between "staging file created" and "archive published" so a 5ms poll cannot miss
    /// the window entirely.
    /// </summary>
    [Fact]
    public async Task A_real_kill_mid_create_leaves_orphans_the_next_invocation_really_sweeps()
    {
        var inputDir = Path.Combine(_root, "inputs");
        Directory.CreateDirectory(inputDir);
        var rng = new Random(20261019);
        for (var i = 0; i < 16; i++)
        {
            var bytes = new byte[2 * 1024 * 1024];
            rng.NextBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(inputDir, $"file{i:D3}.bin"), bytes);
        }

        var destination = Path.Combine(_root, "out.zip");
        var cliPath = LocateCliExecutable();

        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("create");
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add(inputDir);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("zip");
        startInfo.ArgumentList.Add("--level");
        startInfo.ArgumentList.Add("9");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start noba-ar.");

        string? staging;
        try
        {
            // Deterministic: wait for the one real signal that matters (the staging file exists),
            // never a fixed sleep. The only failure mode this cannot rule out - the whole operation
            // finishing before the first poll - is why the input above is sized the way it is.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            staging = null;
            while (DateTime.UtcNow < deadline)
            {
                staging = Directory.EnumerateFiles(_root, ".noba-*.tmp").FirstOrDefault();
                if (staging is not null)
                {
                    break;
                }

                if (process.HasExited)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException(
                        $"noba-ar exited (code {process.ExitCode}) before staging a file: {stderr}");
                }

                await Task.Delay(5);
            }

            Assert.NotNull(staging);
            var lockFile = destination + ".noba-write-lock";
            Assert.True(File.Exists(lockFile), "The write-lock should already exist alongside the staging file.");

            // A real SIGKILL on Unix - no finally block, no IAsyncDisposable, no chance for
            // ArchivePublication.DisposeAsync to run. This is the one thing every other orphan test
            // in this codebase cannot produce.
            process.Kill(entireProcessTree: true);
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("The killed process did not exit within 5s.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        var lockFileAfterKill = destination + ".noba-write-lock";
        Assert.True(File.Exists(staging), "The staging file must survive a killed process.");
        Assert.True(File.Exists(lockFileAfterKill), "The write-lock must survive a killed process.");
        Assert.False(File.Exists(destination), "A killed write must never produce the destination archive.");

        // The real production class, not a stand-in: a fresh publish against the same destination
        // must sweep both real orphans and succeed, exactly as the simulated tests already assert it
        // would - now proven against artifacts an actual kill produced rather than ones this test
        // placed by hand. Succeeding at all is already most of the proof: AcquireLock only reaches a
        // second successful CreateNew after TryReclaimOrphanedLock has removed the dead PID's lock -
        // a lock still read as live would have thrown AccessDenied here instead of returning.
        var publisher = new ArchivePublisher(new PhysicalFileSystem());
        var publication = await publisher.BeginAsync(destination, overwrite: false, CancellationToken.None);

        Assert.False(File.Exists(staging), "The dead process's staging file should have been swept.");

        // This publication's own lock is legitimately still open here - checking the lock file is
        // gone has to wait until this object itself releases it, same as any other publication.
        await publication.DisposeAsync();
        Assert.False(File.Exists(lockFileAfterKill), "This publication's own lock should be released on dispose.");
    }

    private static string LocateCliExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NOBA.Archive.sln")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new InvalidOperationException(
                "Could not locate the repository root (NOBA.Archive.sln) from the test output directory.");
        }

        // Mirrors this very test assembly's own bin layout (bin/<config>/net10.0/) rather than
        // hard-coding a configuration, so this works whichever configuration actually built it.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(root.FullName, "src", "NOBA.Archive.Cli", "bin", configuration, "net10.0", "noba-ar");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "noba-ar is not built next to this test run. Build the solution before running this test.",
                path);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
