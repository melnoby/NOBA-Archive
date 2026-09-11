using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Abstractions;

public enum GuardTrip
{
    None = 0,
    EntryExpansionRatio,
    TotalExpansionRatio,
    EntryCount,
    TotalExpandedSize,
    MemoryBudget,
    NestedArchiveDepth,
}

public sealed record GuardVerdict(GuardTrip Trip, string Explanation, double ObservedValue, double Threshold)
{
    public static GuardVerdict Ok { get; } = new(GuardTrip.None, string.Empty, 0, 0);

    public bool IsTripped => Trip != GuardTrip.None;
}

/// <summary>
/// Archive-bomb protection. Counters are fed from observed bytes as they stream, never from declared
/// metadata, because declared sizes are attacker-controlled (THREAT_MODEL.md T3).
/// </summary>
public interface IResourceGuard
{
    GuardVerdict ObserveEntry(ByteSize compressed, ByteSize expanded);

    GuardVerdict ObserveTotals(long entryCount, ByteSize totalCompressed, ByteSize totalExpanded);

    GuardVerdict CheckMemoryBudget(ByteSize requested);

    GuardVerdict CheckNestedDepth(int depth);

    void Reset();
}
