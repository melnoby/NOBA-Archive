using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Security;

/// <summary>
/// Archive-bomb thresholds. Every one is configurable: a legitimate 200 GB VM image inside a 7z must
/// not be blocked, it must prompt once with the real numbers.
/// </summary>
public sealed record ResourceGuardOptions
{
    public double MaxEntryExpansionRatio { get; init; } = 1000d;

    public double MaxTotalExpansionRatio { get; init; } = 500d;

    public long MaxEntryCount { get; init; } = 1_000_000;

    public ByteSize MaxTotalExpandedSize { get; init; } = ByteSize.FromGibibytes(2048);

    /// <summary>Fraction of physical RAM a single allocation request may claim.</summary>
    public double MaxMemoryFraction { get; init; } = 0.25d;

    public int MaxNestedArchiveDepth { get; init; } = 2;

    /// <summary>Below this, ratios are meaningless - a 12-byte file expanding to 4 KB is not a bomb.</summary>
    public ByteSize RatioFloor { get; init; } = ByteSize.FromKibibytes(64);

    public static ResourceGuardOptions Default { get; } = new();
}
