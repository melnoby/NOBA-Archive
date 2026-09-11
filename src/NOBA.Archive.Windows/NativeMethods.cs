using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NOBA.Archive.Windows;

/// <summary>
/// The Win32 surface NOBA Archive needs. Everything here is <c>SafeHandle</c>-based; there is no raw
/// pointer ownership anywhere in the product.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal const uint FILE_NAME_NORMALIZED = 0x0;
    internal const uint VOLUME_NAME_DOS = 0x0;

    internal const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x0000_0800;
    internal const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x0000_0100;
    internal const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x0000_0400;

    /// <summary>
    /// Resolves the path a handle actually points at. This is the check that defeats TOCTOU and
    /// junction swaps between validating a path and writing to it (THREAT_MODEL.md T1, T2).
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    /// <summary>
    /// Removes the current directory from the DLL search order. Called once at startup, before any
    /// native archive backend is loaded.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDefaultDllDirectories(uint directoryFlags);
}
