using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Security;
using Xunit;

namespace NOBA.Archive.Security.Tests;

/// <summary>SEC-008 and SEC-011: bombs are caught, and legitimate large archives are not.</summary>
public sealed class ResourceGuardTests
{
    private const long OneGiB = 1024L * 1024 * 1024;

    private static ResourceGuard Guard(ResourceGuardOptions? options = null) =>
        new(options ?? ResourceGuardOptions.Default, 16 * OneGiB);

    [Fact]
    public void Trips_on_an_extreme_single_entry_ratio()
    {
        var verdict = Guard().ObserveEntry(ByteSize.FromKibibytes(1), ByteSize.FromMebibytes(500));

        Assert.True(verdict.IsTripped);
        Assert.Equal(GuardTrip.EntryExpansionRatio, verdict.Trip);
        Assert.NotEmpty(verdict.Explanation);
    }

    /// <summary>A small file that compresses well is not a bomb; below the floor, ratios are noise.</summary>
    [Fact]
    public void Ignores_ratios_below_the_floor()
    {
        var verdict = Guard().ObserveEntry(ByteSize.FromBytes(12), ByteSize.FromKibibytes(4));

        Assert.False(verdict.IsTripped);
    }

    /// <summary>
    /// DROP 13: a solid 7z block attributes its whole packed size to the first entry and reports
    /// zero for every sibling - a provider fact, not a hypothetical - so a reported zero paired with
    /// a large expansion must trip, not sail through as "nothing to divide by".
    /// </summary>
    [Fact]
    public void Trips_when_compressed_size_is_reported_as_zero_past_the_floor()
    {
        var verdict = Guard().ObserveEntry(ByteSize.Zero, ByteSize.FromGibibytes(4));

        Assert.True(verdict.IsTripped);
        Assert.Equal(GuardTrip.EntryExpansionRatio, verdict.Trip);
    }

    /// <summary>A genuinely empty entry (zero in, zero out) is still exactly what the floor is for.</summary>
    [Fact]
    public void Does_not_trip_on_a_zero_compressed_size_below_the_floor()
    {
        var verdict = Guard().ObserveEntry(ByteSize.Zero, ByteSize.FromBytes(12));

        Assert.False(verdict.IsTripped);
    }

    [Fact]
    public void Allows_a_legitimately_large_archive()
    {
        var verdict = Guard().ObserveTotals(
            entryCount: 5,
            totalCompressed: ByteSize.FromGibibytes(40),
            totalExpanded: ByteSize.FromGibibytes(120));

        Assert.False(verdict.IsTripped, verdict.Explanation);
    }

    [Fact]
    public void Trips_on_an_absurd_entry_count()
    {
        var verdict = Guard().ObserveTotals(2_000_000, ByteSize.FromMebibytes(1), ByteSize.FromMebibytes(2));

        Assert.True(verdict.IsTripped);
        Assert.Equal(GuardTrip.EntryCount, verdict.Trip);
    }

    [Fact]
    public void Trips_on_an_overall_expansion_ratio()
    {
        var verdict = Guard().ObserveTotals(10, ByteSize.FromMebibytes(1), ByteSize.FromGibibytes(4));

        Assert.True(verdict.IsTripped);
        Assert.Equal(GuardTrip.TotalExpansionRatio, verdict.Trip);
    }

    [Fact]
    public void Refuses_a_memory_request_beyond_the_budget()
    {
        var guard = Guard();

        Assert.True(guard.CheckMemoryBudget(ByteSize.FromGibibytes(8)).IsTripped);
        Assert.False(guard.CheckMemoryBudget(ByteSize.FromMebibytes(256)).IsTripped);
    }

    [Fact]
    public void Limits_how_deep_nested_archives_are_opened()
    {
        var guard = Guard();

        Assert.False(guard.CheckNestedDepth(2).IsTripped);
        Assert.True(guard.CheckNestedDepth(3).IsTripped);
    }

    /// <summary>Thresholds are configurable so a real workload can proceed after an informed decision.</summary>
    [Fact]
    public void Honours_relaxed_thresholds()
    {
        var relaxed = Guard(ResourceGuardOptions.Default with { MaxEntryExpansionRatio = 1_000_000 });

        Assert.False(relaxed.ObserveEntry(ByteSize.FromKibibytes(1), ByteSize.FromMebibytes(500)).IsTripped);
    }
}
