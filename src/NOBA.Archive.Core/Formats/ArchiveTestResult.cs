namespace NOBA.Archive.Core.Formats;

public enum ArchiveTestStatus
{
    Valid = 0,
    ValidWithWarnings,
    Corrupted,
    Unsupported,
    PasswordRequired,
    UnknownError,
}

public sealed record ArchiveTestFinding(string EntryPath, string Message, bool IsError);

public sealed record ArchiveTestResult
{
    public required ArchiveTestStatus Status { get; init; }

    public long EntriesTested { get; init; }

    public IReadOnlyList<ArchiveTestFinding> Findings { get; init; } = [];

    public bool IsUsable => Status is ArchiveTestStatus.Valid or ArchiveTestStatus.ValidWithWarnings;

    public static ArchiveTestResult Valid(long entriesTested) =>
        new() { Status = ArchiveTestStatus.Valid, EntriesTested = entriesTested };

    public static ArchiveTestResult FromFindings(long entriesTested, IReadOnlyList<ArchiveTestFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var status = findings.Any(f => f.IsError)
            ? ArchiveTestStatus.Corrupted
            : findings.Count > 0
                ? ArchiveTestStatus.ValidWithWarnings
                : ArchiveTestStatus.Valid;
        return new ArchiveTestResult { Status = status, EntriesTested = entriesTested, Findings = findings };
    }
}
