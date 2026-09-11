using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NOBA.Archive.Spike.SevenZip;

public sealed record FixtureArchive(string ArchivePath, Dictionary<string, byte[]> Expected);

public sealed record FixtureSet(FixtureArchive Zip, FixtureArchive SevenZip, FixtureArchive Solid, FixtureArchive Encrypted);

/// <summary>
/// Builds the archives with the 7-Zip CLI, so the fixtures are known-good rather than produced by the
/// code under test. Content includes Arabic and emoji names, an incompressible blob, and a highly
/// compressible one - the same shapes TESTING.md §2 calls for.
/// </summary>
public static class TestFixtures
{
    public static FixtureSet Create(string workRoot)
    {
        var source = Path.Combine(workRoot, "src");
        Directory.CreateDirectory(source);

        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        Add(source, expected, "readme.txt", Encoding.UTF8.GetBytes("hello from the spike\n"));
        Add(source, expected, "تقرير.txt", Encoding.UTF8.GetBytes("محتوى عربي للاختبار\n"));
        Add(source, expected, "data 🗜️.bin", RandomNumberGenerator.GetBytes(64 * 1024));
        Add(source, expected, "zeros.bin", new byte[256 * 1024]);
        Add(source, expected, "empty.bin", []);

        return new FixtureSet(
            Build(workRoot, source, expected, "plain.zip", "-tzip"),
            Build(workRoot, source, expected, "plain.7z", "-t7z", "-ms=off"),
            Build(workRoot, source, expected, "solid.7z", "-t7z", "-ms=on", "-mx=9"),
            Build(workRoot, source, expected, "secret.7z", "-t7z", "-mhe=on", "-pspike-password"));
    }

    /// <summary>Locates the 7-Zip CLI used to build known-good fixtures.</summary>
    private static string LocateCli()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "7z";
        }

        string[] candidates =
        [
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Falls back to whatever the shell resolves, which is the one place a PATH lookup is fine:
        // this is test-fixture tooling, not the product.
        return "7z.exe";
    }

    private static void Add(string directory, Dictionary<string, byte[]> expected, string name, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(directory, name), content);
        expected[name] = content;
    }

    private static FixtureArchive Build(
        string workRoot,
        string source,
        Dictionary<string, byte[]> expected,
        string archiveName,
        params string[] switches)
    {
        var archivePath = Path.Combine(workRoot, archiveName);
        var arguments = new List<string> { "a", "-bso0", "-bsp0", archivePath };
        arguments.AddRange(switches);
        arguments.Add(Path.Combine(source, "*"));

        var startInfo = new ProcessStartInfo(LocateCli()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the 7z CLI to build fixtures.");
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(archivePath))
        {
            throw new InvalidOperationException(
                $"Building '{archiveName}' failed (exit {process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }

        return new FixtureArchive(archivePath, new Dictionary<string, byte[]>(expected, StringComparer.Ordinal));
    }
}
