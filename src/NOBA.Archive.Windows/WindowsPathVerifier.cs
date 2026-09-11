using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NOBA.Archive.Windows;

/// <summary>
/// Answers "where does this open handle actually point?" - the post-open half of the extraction
/// writer's containment check.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPathVerifier
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns the canonical path behind <paramref name="handle"/>, with the <c>\\?\</c> prefix
    /// stripped so it can be compared against a destination root.
    /// </summary>
    public static string GetFinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var buffer = new char[1024];
        var length = NativeMethods.GetFinalPathNameByHandle(
            handle,
            buffer,
            (uint)buffer.Length,
            NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);

        if (length == 0)
        {
            throw new IOException(
                "Could not resolve the final path of the open handle.",
                Marshal.GetLastWin32Error());
        }

        // >= not >: on success the return value excludes the null terminator, so it is always strictly
        // less than the buffer. A value equal to the buffer length means the call failed for want of
        // room and left the buffer unwritten — treating that as success would build a string from
        // uninitialised memory.
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Length,
                NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
            if (length == 0)
            {
                throw new IOException(
                    "Could not resolve the final path of the open handle.",
                    Marshal.GetLastWin32Error());
            }
        }

        return StripExtendedPrefix(new string(buffer, 0, (int)length));
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
        {
            return string.Concat(@"\\", path.AsSpan(ExtendedUncPrefix.Length));
        }

        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? path[ExtendedPrefix.Length..]
            : path;
    }
}
