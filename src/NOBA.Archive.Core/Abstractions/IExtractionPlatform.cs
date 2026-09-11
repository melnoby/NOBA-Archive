using Microsoft.Win32.SafeHandles;

namespace NOBA.Archive.Core.Abstractions;

/// <summary>
/// The platform-specific half of the extraction writer: resolving where an <em>already open</em> handle
/// actually points, and carrying Mark of the Web across.
/// </summary>
/// <remarks>
/// <para>
/// This exists as a seam rather than a direct call into <c>NOBA.Archive.Windows</c> so that
/// <c>NOBA.Archive.Infrastructure</c> stays platform-neutral at compile time. That keeps the engine
/// embeddable (NOBAOS_INTEGRATION.md), keeps the whole suite runnable on any CI image, and — most
/// usefully — makes the security-critical behaviour testable with a substitute implementation.
/// </para>
/// <para>
/// <b>Capability, not pretence.</b> Where the post-open check cannot be performed, the implementation
/// says so through <see cref="SupportsHandleVerification"/> instead of silently returning a value the
/// writer would trust. The writer then reports reduced assurance rather than claiming a guarantee it
/// did not obtain.
/// </para>
/// </remarks>
public interface IExtractionPlatform
{
    /// <summary>Human-readable name for logs and reports, e.g. <c>windows</c> or <c>portable</c>.</summary>
    string Name { get; }

    /// <summary>True when <see cref="GetFinalPathOrNull"/> can genuinely resolve an open handle.</summary>
    bool SupportsHandleVerification { get; }

    /// <summary>
    /// The canonical path an open handle resolves to, or null when it cannot be determined.
    /// This is what defeats TOCTOU and junction swaps between validating a path and writing to it.
    /// </summary>
    string? GetFinalPathOrNull(SafeFileHandle handle);

    bool SupportsMarkOfTheWeb { get; }

    /// <summary>The archive's zone marker, or null when it carries none.</summary>
    string? ReadZoneIdentifier(string filePath);

    /// <summary>Applies a zone marker to an extracted file. Losing MOTW is a security defect (T7).</summary>
    void WriteZoneIdentifier(string filePath, string zoneContent);
}
