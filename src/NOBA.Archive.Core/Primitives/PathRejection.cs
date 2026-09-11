namespace NOBA.Archive.Core.Primitives;

/// <summary>Why an archive-declared path was refused. Surfaced to the user, never swallowed.</summary>
public enum PathRejectionReason
{
    None = 0,
    Empty,
    TraversalSegment,
    RootedPath,
    DriveRelativePath,
    UncPath,
    DevicePath,
    AlternateDataStream,
    ControlCharacter,
    TooDeep,
    TooLong,
    EscapesDestination,
    InvalidCharacter,

    /// <summary>A symlink or hardlink entry refused by the link policy (THREAT_MODEL.md T2).</summary>
    LinkRefused,

    /// <summary>The opened handle did not resolve where validation said it would - a TOCTOU defence.</summary>
    HandleVerificationFailed,
}

/// <summary>
/// A rejected entry path. Kept as data rather than an exception so that listing a hostile archive
/// shows the malice instead of failing (THREAT_MODEL.md T1).
/// </summary>
public sealed record PathRejection(PathRejectionReason Reason, string RawPath, string Detail)
{
    public override string ToString() => $"{Reason}: {Detail}";
}

/// <summary>How a Win32-illegal but non-malicious name was made writable.</summary>
public sealed record PathSanitization(string OriginalSegment, string ReplacementSegment, string Reason);
