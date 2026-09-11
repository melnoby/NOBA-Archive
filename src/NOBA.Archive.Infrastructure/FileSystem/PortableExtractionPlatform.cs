using System.Globalization;
using Microsoft.Win32.SafeHandles;
using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// The non-Windows implementation, used for development and CI on Linux.
/// </summary>
/// <remarks>
/// <para>
/// Handle verification is <b>real</b> on Linux: <c>/proc/self/fd/N</c> resolves an open descriptor to
/// its actual path, which is the same guarantee <c>GetFinalPathNameByHandle</c> provides on Windows.
/// That means the symlink and TOCTOU tests exercise the real code path here rather than a stub.
/// </para>
/// <para>
/// Mark of the Web has no equivalent and is reported as unsupported. The writer records that the
/// guarantee was unavailable instead of pretending it applied.
/// </para>
/// </remarks>
public sealed class PortableExtractionPlatform : IExtractionPlatform
{
    public string Name => OperatingSystem.IsLinux() ? "portable-linux" : "portable";

    public bool SupportsHandleVerification => OperatingSystem.IsLinux();

    public bool SupportsMarkOfTheWeb => false;

    public string? GetFinalPathOrNull(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsLinux() || handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        try
        {
            var descriptor = handle.DangerousGetHandle().ToInt32();
            var link = string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{descriptor}");
            return File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? ReadZoneIdentifier(string filePath) => null;

    public void WriteZoneIdentifier(string filePath, string zoneContent)
    {
        // No equivalent outside Windows. SupportsMarkOfTheWeb already told the caller.
    }
}
