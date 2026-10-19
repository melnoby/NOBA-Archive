using System.Globalization;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Security;

/// <summary>
/// Bomb protection driven by observed bytes, never by declared metadata - declared sizes are
/// attacker-controlled (THREAT_MODEL.md T3).
/// </summary>
public sealed class ResourceGuard : IResourceGuard
{
    private readonly ResourceGuardOptions _options;
    private readonly long _physicalMemoryBytes;

    public ResourceGuard()
        : this(ResourceGuardOptions.Default, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)
    {
    }

    public ResourceGuard(ResourceGuardOptions options, long physicalMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalMemoryBytes);
        _options = options;
        _physicalMemoryBytes = physicalMemoryBytes;
    }

    public GuardVerdict ObserveEntry(ByteSize compressed, ByteSize expanded)
    {
        if (expanded < _options.RatioFloor)
        {
            return GuardVerdict.Ok;
        }

        if (compressed.IsZero)
        {
            // DROP 13: a zero here used to mean "treat as safe", on the assumption that nothing
            // this cheap to store could be a bomb. That is true for a genuinely empty entry, which
            // the floor check above already lets through with expanded size zero too - but a
            // provider can report zero for an entry that is NOT empty: a solid 7z block attributes
            // the whole block's packed size to its first entry and reports zero for every sibling
            // (SevenZipArchiveProvider.cs / InArchive.cs), and a crafted archive can put the real
            // packed size on an innocuous first entry while every later entry in the same block
            // individually expands to gigabytes. None of them ever reached a ratio check at all
            // under the old "zero is safe" rule - this was the one entry-ratio guard THREAT_MODEL.md
            // T3 names, provably never firing for most entries of a normal solid archive. A reported
            // zero paired with an expansion at or past the floor is now treated as unmeasurable, not
            // safe, and trips the same way an extreme ratio would.
            return new GuardVerdict(
                GuardTrip.EntryExpansionRatio,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This entry expands to {expanded} from a reported compressed size of zero."),
                double.PositiveInfinity,
                _options.MaxEntryExpansionRatio);
        }

        var ratio = (double)expanded.Bytes / compressed.Bytes;
        return ratio > _options.MaxEntryExpansionRatio
            ? new GuardVerdict(
                GuardTrip.EntryExpansionRatio,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This entry expands {ratio:F0}:1 ({compressed} compressed, {expanded} expanded)."),
                ratio,
                _options.MaxEntryExpansionRatio)
            : GuardVerdict.Ok;
    }

    public GuardVerdict ObserveTotals(long entryCount, ByteSize totalCompressed, ByteSize totalExpanded)
    {
        if (entryCount > _options.MaxEntryCount)
        {
            return new GuardVerdict(
                GuardTrip.EntryCount,
                string.Create(CultureInfo.InvariantCulture, $"The archive declares {entryCount:N0} entries."),
                entryCount,
                _options.MaxEntryCount);
        }

        if (totalExpanded > _options.MaxTotalExpandedSize)
        {
            return new GuardVerdict(
                GuardTrip.TotalExpandedSize,
                $"Extracting this archive would produce {totalExpanded}.",
                totalExpanded.Bytes,
                _options.MaxTotalExpandedSize.Bytes);
        }

        if (totalExpanded >= _options.RatioFloor && !totalCompressed.IsZero)
        {
            var ratio = (double)totalExpanded.Bytes / totalCompressed.Bytes;
            if (ratio > _options.MaxTotalExpansionRatio)
            {
                return new GuardVerdict(
                    GuardTrip.TotalExpansionRatio,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"This archive expands {ratio:F0}:1 overall ({totalCompressed} to {totalExpanded})."),
                    ratio,
                    _options.MaxTotalExpansionRatio);
            }
        }

        return GuardVerdict.Ok;
    }

    public GuardVerdict CheckMemoryBudget(ByteSize requested)
    {
        var budget = (long)(_physicalMemoryBytes * _options.MaxMemoryFraction);
        return requested.Bytes > budget
            ? new GuardVerdict(
                GuardTrip.MemoryBudget,
                $"This archive asks for {requested} of memory; the budget is {ByteSize.FromBytes(budget)}.",
                requested.Bytes,
                budget)
            : GuardVerdict.Ok;
    }

    public GuardVerdict CheckNestedDepth(int depth) =>
        depth > _options.MaxNestedArchiveDepth
            ? new GuardVerdict(
                GuardTrip.NestedArchiveDepth,
                $"Nested archives are only opened {_options.MaxNestedArchiveDepth} levels deep.",
                depth,
                _options.MaxNestedArchiveDepth)
            : GuardVerdict.Ok;

    /// <summary>No cross-entry state is retained, so resetting is a no-op that keeps the contract honest.</summary>
    public void Reset()
    {
    }
}
