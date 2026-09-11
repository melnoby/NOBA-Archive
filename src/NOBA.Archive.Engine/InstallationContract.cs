namespace NOBA.Archive.Engine;

/// <summary>
/// How a host - NobaOS above all - locates an installed NOBA Archive.
/// </summary>
/// <remarks>
/// <para>
/// The installer writes these values; a host reads them. That is the whole contract.
/// </para>
/// <para>
/// <b>PATH is not part of it.</b> Adding the install directory to the system <c>PATH</c> is an optional
/// installer feature for people who want <c>noba-ar</c> available from any shell. It is never how NobaOS
/// finds the product, and nothing in the engine or the CLI reads <c>PATH</c> at run time. A machine with
/// the feature switched off is fully supported, and integration must keep working there.
/// </para>
/// </remarks>
public static class InstallationContract
{
    /// <summary>Registry key, per machine, written by the installer.</summary>
    public const string RegistryKeyPath = @"SOFTWARE\NOBA\Archive";

    /// <summary>Absolute path of the install directory.</summary>
    public const string InstallPathValueName = "InstallPath";

    /// <summary>Absolute path of <c>noba-ar.exe</c>, so a host never has to guess the file name.</summary>
    public const string CliPathValueName = "CliPath";

    /// <summary>Product version, so a host can check compatibility before invoking.</summary>
    public const string VersionValueName = "Version";

    /// <summary>The executable's file name, for hosts that resolve it under a known install directory.</summary>
    public const string CliFileName = "noba-ar.exe";

    /// <summary>
    /// The resolution order a host should follow. Every step is explicit; none of them consults
    /// <c>PATH</c>, and none of them depends on the working directory.
    /// </summary>
    public static IReadOnlyList<string> DocumentedResolutionOrder { get; } =
    [
        "A path the host was configured with explicitly (NobaOS deployment configuration).",
        $@"HKLM\{RegistryKeyPath}\{CliPathValueName}, written by the installer.",
        $@"HKLM\{RegistryKeyPath}\{InstallPathValueName} combined with '{CliFileName}'.",
        "Fail with a clear 'NOBA Archive is not installed' error - never fall back to a PATH lookup.",
    ];
}
