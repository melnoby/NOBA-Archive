using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Formats;

/// <summary>What to do when the destination already has a file with the target name.</summary>
public enum ConflictPolicy
{
    /// <summary>Prompt for every conflict. Invalid for a headless host, and rejected at configuration time.</summary>
    Ask = 0,
    Overwrite,
    Skip,
    Rename,
    Fail,
}

public enum ConflictAction
{
    Overwrite = 0,
    Skip,
    Rename,
    Cancel,
}

/// <summary>One conflict decision, plus whether it latches for the rest of the operation.</summary>
public sealed record ConflictDecision(ConflictAction Action, bool ApplyToAll)
{
    public static ConflictDecision Once(ConflictAction action) => new(action, false);

    public static ConflictDecision All(ConflictAction action) => new(action, true);
}

/// <summary>How link entries in an archive are treated. "Always create" is deliberately absent.</summary>
public enum LinkPolicy
{
    /// <summary>Never materialise a link. The default (THREAT_MODEL.md T2).</summary>
    Never = 0,

    /// <summary>Create only when the target provably resolves inside the destination.</summary>
    WhenInsideDestination,

    /// <summary>Ask the user for each link entry.</summary>
    Ask,
}

/// <summary>How a Win32-illegal but non-malicious name (reserved device name, trailing dot) is handled.</summary>
public enum IllegalNamePolicy
{
    /// <summary>Deterministically rename and report. Default: a Linux tarball with <c>aux.h</c> is legitimate.</summary>
    Sanitize = 0,

    /// <summary>Skip the entry and report it.</summary>
    Skip,
}

public sealed record ExtractionOptions
{
    public required string DestinationRoot { get; init; }

    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    public LinkPolicy LinkPolicy { get; init; } = LinkPolicy.Never;

    public IllegalNamePolicy IllegalNamePolicy { get; init; } = IllegalNamePolicy.Sanitize;

    /// <summary>Reproduce the archive's directory structure. When false, everything lands flat.</summary>
    public bool PreserveDirectoryStructure { get; init; } = true;

    /// <summary>Extract each source archive into its own subdirectory.</summary>
    public bool SeparateDirectoryPerArchive { get; init; }

    public bool RestoreTimestamps { get; init; } = true;

    public bool RestoreAttributes { get; init; } = true;

    /// <summary>Propagate the archive's zone to extracted files. Losing it is a security defect (T7).</summary>
    public bool PropagateMarkOfTheWeb { get; init; } = true;

    /// <summary>
    /// Forces one backend call per entry instead of a single batch pass.
    /// </summary>
    /// <remarks>
    /// The batch path is enormously faster on solid archives — 20.1 s to 0.9 s for 3200 entries — but
    /// it hands control of the extraction loop to the backend, and this switch takes it back. It exists
    /// for two reasons: the equivalence test drives both paths over the same archive and compares the
    /// results, and a user hitting a backend-specific problem in the field has somewhere to go other
    /// than an older version.
    /// </remarks>
    public bool PreferSingleEntryExtraction { get; init; }

    /// <summary>
    /// The archive being extracted, so its Mark of the Web can be carried across. Null disables
    /// propagation for this operation.
    /// </summary>
    public string? SourceArchivePath { get; init; }

    /// <summary>Null extracts everything; otherwise only these entries.</summary>
    public IReadOnlyList<ArchivePath>? SelectedEntries { get; init; }

    public static ArchiveCapabilities RequiredCapabilities() => ArchiveCapabilities.Open | ArchiveCapabilities.Extract;
}
