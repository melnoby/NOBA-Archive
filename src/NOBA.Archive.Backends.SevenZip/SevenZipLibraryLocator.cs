using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip;

/// <summary>Where the native library was found, and how much that location should be trusted.</summary>
public enum NativeLibraryOrigin
{
    /// <summary>Not found at all.</summary>
    None = 0,

    /// <summary>Shipped beside our own binaries. The only location we control.</summary>
    Application,

    /// <summary>Named explicitly by the host or by configuration.</summary>
    Configured,

    /// <summary>A 7-Zip installation already on the machine.</summary>
    InstalledSevenZip,

    /// <summary>A system library directory.</summary>
    System,
}

public sealed record NativeLibraryLocation(string Path, NativeLibraryOrigin Origin);

/// <summary>
/// Finds <c>7z.dll</c> without ever letting the current directory decide.
/// </summary>
/// <remarks>
/// <para>
/// Loading a native library by bare name searches the current working directory on Windows, which is
/// the classic DLL-hijacking vector (THREAT_MODEL.md T8) — and an archive manager is routinely pointed
/// at a folder an attacker controls, because that is where the archive is. Every candidate here is an
/// absolute path, checked for existence, and loaded as such.
/// </para>
/// <para>
/// The search order runs from most trusted to least: what we shipped, what the host named, then an
/// existing installation. A NOBA Archive install carries its own copy, so the later entries matter
/// mainly for development machines and for the embedded engine inside NOBA OS.
/// </para>
/// </remarks>
public static class SevenZipLibraryLocator
{
    /// <summary>The file name of the native library on this platform.</summary>
    public static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "7z.dll" : "7z.so";

    /// <summary>
    /// Every place worth looking, in descending order of trust. Existence is not checked here — see
    /// <see cref="Locate"/> — so that a caller can report exactly what was tried.
    /// </summary>
    public static IReadOnlyList<NativeLibraryLocation> Candidates(string? configuredPath = null)
    {
        var found = new List<NativeLibraryLocation>();
        var fileName = LibraryFileName;

        var appDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(appDirectory))
        {
            found.Add(new NativeLibraryLocation(
                Path.Combine(appDirectory, fileName),
                NativeLibraryOrigin.Application));
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // A directory or the library itself; both are things a host may reasonably configure.
            var configured = Directory.Exists(configuredPath)
                ? Path.Combine(configuredPath, fileName)
                : configuredPath;

            found.Add(new NativeLibraryLocation(
                Path.GetFullPath(configured),
                NativeLibraryOrigin.Configured));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                var root = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(root))
                {
                    found.Add(new NativeLibraryLocation(
                        Path.Combine(root, "7-Zip", fileName),
                        NativeLibraryOrigin.InstalledSevenZip));
                }
            }
        }
        else
        {
            foreach (var directory in new[] { "/usr/lib/7zip", "/usr/lib/p7zip", "/usr/local/lib/7zip" })
            {
                found.Add(new NativeLibraryLocation(
                    Path.Combine(directory, fileName),
                    NativeLibraryOrigin.System));
            }
        }

        return found;
    }

    /// <summary>The first candidate that exists, or null. Never throws — absence is a supported state.</summary>
    public static NativeLibraryLocation? Locate(string? configuredPath = null)
    {
        foreach (var candidate in Candidates(configuredPath))
        {
            try
            {
                if (File.Exists(candidate.Path))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable candidate is simply not a candidate.
            }
        }

        return null;
    }

    /// <summary>
    /// A diagnostic listing every path tried, for the error a user actually sees when 7Z support is
    /// missing. "The native backend is unavailable" without saying where we looked is unactionable.
    /// </summary>
    public static string DescribeSearch(string? configuredPath = null) =>
        string.Join(
            Environment.NewLine,
            Candidates(configuredPath).Select(static c => $"  {c.Origin,-18} {c.Path}"));

    /// <summary>
    /// Removes the current directory from the native search path. Called once before any backend loads.
    /// </summary>
    /// <remarks>
    /// Belt and braces: every load in this assembly already uses an absolute path, but a dependency of
    /// <c>7z.dll</c> resolved by the OS loader would not, and that is outside our control.
    /// </remarks>
    public static void HardenSearchPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-KB2533623 systems. The minimum supported build has it; older ones fail closed to the
            // default search order rather than to no product.
        }
    }

    private const uint LoadLibrarySearchSystem32 = 0x0000_0800;
    private const uint LoadLibrarySearchUserDirs = 0x0000_0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
