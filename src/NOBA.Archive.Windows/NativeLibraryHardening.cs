using System.Runtime.Versioning;

namespace NOBA.Archive.Windows;

/// <summary>
/// Startup hardening for native dependencies. The current directory must never be searched for a DLL:
/// that is the whole DLL-hijacking vector, and a native archive backend is exactly the sort of thing
/// an attacker would like to substitute (THREAT_MODEL.md T8).
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeLibraryHardening
{
    /// <summary>
    /// Restricts the process DLL search path to System32, the loading module's directory, and any
    /// directory explicitly added. Call once, before loading any native backend.
    /// </summary>
    public static bool RestrictDllSearchPath() =>
        NativeMethods.SetDefaultDllDirectories(
            NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32 |
            NativeMethods.LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
            NativeMethods.LOAD_LIBRARY_SEARCH_USER_DIRS);
}
