using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Windows;

/// <summary>
/// The Windows implementation of the extraction writer's platform half: real handle verification and
/// real Mark of the Web propagation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsExtractionPlatform : IExtractionPlatform
{
    private const string ZoneStreamSuffix = ":Zone.Identifier";

    public string Name => "windows";

    public bool SupportsHandleVerification => true;

    public bool SupportsMarkOfTheWeb => true;

    public string? GetFinalPathOrNull(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        try
        {
            return WindowsPathVerifier.GetFinalPath(handle);
        }
        catch (IOException)
        {
            // Reporting "unknown" is correct here: the caller must refuse the write rather than
            // proceed on an unverified handle.
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>Zone.Identifier</c> alternate data stream. Note the asymmetry with entry names:
    /// an ADS we create on a validated path is the mechanism SmartScreen relies on, whereas an ADS
    /// requested by an archive entry is an attack (CVE-2025-8088) and is always refused.
    /// </summary>
    public string? ReadZoneIdentifier(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var stream = filePath + ZoneStreamSuffix;

        try
        {
            using var reader = new StreamReader(
                new FileStream(stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteZoneIdentifier(string filePath, string zoneContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (string.IsNullOrEmpty(zoneContent))
        {
            return;
        }

        var stream = filePath + ZoneStreamSuffix;
        using var writer = new StreamWriter(
            new FileStream(stream, FileMode.Create, FileAccess.Write, FileShare.None),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(zoneContent);
    }
}
