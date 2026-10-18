using System.Security.Cryptography;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The PRF-### suite (DROP 11 — Performance and Scale).
/// </summary>
/// <remarks>
/// <para>
/// <c>CRT-013</c>'s own doc comment already states this project's rule for performance work on
/// shared CI hardware: "there is no time assertion, because a wall clock on shared CI hardware
/// measures the hardware." That rule is not an excuse to measure nothing — it is why every assertion
/// here is a <em>ratio</em> between two runs of the same code at two scales, read against the
/// allocated-byte count (<see cref="GC.GetTotalAllocatedBytes(bool)"/>), not a wall clock. An
/// allocation count does not depend on how fast or loaded the machine happens to be right now, so a
/// real regression — an accidental full buffer, an accidental O(n²) loop — moves the ratio by far
/// more than ordinary noise does, and the bound each test picks is wide enough to absorb that noise
/// without absorbing the regression too.
/// </para>
/// <para>
/// <c>docs/PERFORMANCE.md</c> is where the actual wall-clock numbers this drop measured live
/// (<c>scripts/benchmark-linux.sh</c>'s output) — this file is the regression suite that keeps those
/// numbers' *shape* true going forward, not a repeat of the measurement itself.
/// </para>
/// </remarks>
public sealed class PerformanceAndScaleTests
{
    /// <summary>
    /// PRF-001: extracting one large file does not allocate proportionally to its size — the
    /// single-large-file half of <c>ARCHITECTURE.md</c>'s "constant memory regardless of archive
    /// size" target (§8.3), turned into something that actually fails if someone adds a
    /// <c>stream.ReadAllBytes()</c>-shaped regression.
    /// </summary>
    [Fact]
    public async Task Extracting_a_larger_single_file_does_not_allocate_proportionally_more()
    {
        using var sandbox = new TempDirectory("PRF-001");

        // A tiny warm-up extraction first, off the two measurements below: JIT, static initialisers
        // and the first provider resolution all allocate once, and that one-time cost would otherwise
        // swamp the smaller of the two real measurements and make the ratio meaningless.
        await WarmUpAsync(sandbox);

        const int smallMiB = 4;
        const int largeMiB = 32; // 8x the small file.

        var smallDelta = await MeasureExtractAllocationAsync(sandbox, "small", smallMiB * 1024 * 1024);
        var largeDelta = await MeasureExtractAllocationAsync(sandbox, "large", largeMiB * 1024 * 1024);

        // 8x the bytes; allocation growth held well under that, proving the extraction path is
        // reading and writing through pooled, fixed-size buffers rather than one buffer sized to the
        // file. The bound is generous on purpose - this proves "bounded", not "flat to the byte".
        Assert.True(
            largeDelta < smallDelta * 4,
            $"small ({smallMiB} MiB) allocated {smallDelta:N0} B; large ({largeMiB} MiB, 8x the size) " +
            $"allocated {largeDelta:N0} B - more than 4x growth for an 8x size increase.");
    }

    /// <summary>
    /// PRF-002: DROP 11's own scope line asks for "many small files," "deep nesting," and "Arabic and
    /// mixed Unicode" together - not as three separate small tests, but at a scale where they
    /// interact. This is that test: 6,000 files, nesting up to 40 levels deep (<see cref="ArchivePath.MaxDepth"/>
    /// is 64), a fifth of the names Arabic, round-tripped through create, list, search and extract.
    /// <c>CRT-013</c> already proves 2,000 flat ASCII files are counted correctly; this is the larger,
    /// deeper, more linguistically honest sibling DROP 11 asks for.
    /// </summary>
    [Fact]
    public async Task Many_files_deep_nesting_and_arabic_names_round_trip_together_at_scale()
    {
        using var sandbox = new TempDirectory("PRF-002");
        var root = sandbox.Combine("bulk");
        Directory.CreateDirectory(root);

        const int count = 6_000;
        var arabicCount = 0;
        for (var i = 0; i < count; i++)
        {
            // Depth cycles 1..40: well inside ArchivePath.MaxDepth (64) once the file name itself is
            // counted as a segment too, but far past CRT-013's flat 25-folder spread.
            var depth = (i % 40) + 1;
            var segments = new string[depth];
            for (var d = 0; d < depth - 1; d++)
            {
                segments[d] = $"d{d:D2}-{i % (d + 3)}";
            }

            var isArabic = i % 5 == 0;
            segments[depth - 1] = isArabic ? $"تقرير-{i:D5}.txt" : $"file-{i:D5}.txt";
            if (isArabic)
            {
                arabicCount++;
            }

            var path = Path.Combine([root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, $"entry {i}");
        }

        var archive = sandbox.Combine("out.zip");
        await using var engine = new EngineFixture();
        var created = await engine.CreateAsync(archive, [root]);
        Assert.Equal(count, created.EntriesAdded);

        var listing = await engine.ListAsync(archive);
        var paths = listing.Entries.Where(static e => !e.IsDirectory).Select(static e => e.DisplayPath).ToList();
        Assert.Equal(count, paths.Count);
        Assert.Equal(count, paths.Distinct(StringComparer.Ordinal).Count());

        var arabicSearch = await engine.SearchAsync(archive, b => b.NameContains("تقرير"));
        Assert.Equal(arabicCount, arabicSearch.Matches.Count);

        var outcome = await engine.ExtractAsync(archive, sandbox.Combine("dest"));
        Assert.Equal(count, outcome.EntriesWritten);
        Assert.Empty(outcome.Blocked);

        Assert.Equal(ArchiveTestStatus.Valid, (await engine.TestAsync(archive)).Status);
    }

    /// <summary>
    /// PRF-003: a search over 10x the entries, with the number of matches held fixed at one, does not
    /// allocate anywhere near 10x more — and nowhere close to the ~100x an accidental O(n²) filter
    /// would produce. <see cref="SearchArchiveHandler"/> walks the entry index exactly once
    /// regardless of how many filters are applied, and this is the test that would actually fail if a
    /// future change made it walk it more than once per entry.
    /// </summary>
    [Fact]
    public async Task Search_allocation_grows_with_entries_scanned_not_quadratically()
    {
        using var sandbox = new TempDirectory("PRF-003");
        await WarmUpAsync(sandbox);

        var smallArchive = await BuildSearchFixtureAsync(sandbox, "small", 500);
        var largeArchive = await BuildSearchFixtureAsync(sandbox, "large", 5_000);

        await using var engine = new EngineFixture();

        var smallDelta = await MeasureAllocatedBytesAsync(
            () => engine.SearchAsync(smallArchive, b => b.Extensions(".marker")).AsTask());
        var largeDelta = await MeasureAllocatedBytesAsync(
            () => engine.SearchAsync(largeArchive, b => b.Extensions(".marker")).AsTask());

        // 10x the entries; a generous 25x ceiling on allocation growth - comfortably above ordinary
        // per-entry linear cost, comfortably below the ~100x an accidental quadratic pass would show.
        Assert.True(
            largeDelta < smallDelta * 25,
            $"500-entry search allocated {smallDelta:N0} B; 5,000-entry search (10x the entries, same " +
            $"single match) allocated {largeDelta:N0} B - more than 25x growth for a 10x entry increase.");
    }

    /// <summary>
    /// PRF-004: extracting 10x as many small files does not allocate disproportionately more - the
    /// many-small-files counterpart to <c>PRF-001</c>'s single-large-file case. A regression that
    /// collected every entry's bytes into one buffer before writing would show up here even though it
    /// would not show up in PRF-001, because the failure mode is "per-entry," not "per-byte."
    /// </summary>
    [Fact]
    public async Task Extracting_more_small_files_does_not_allocate_proportionally_more_per_file()
    {
        using var sandbox = new TempDirectory("PRF-004");
        await WarmUpAsync(sandbox);

        var smallArchive = await BuildManySmallFilesArchiveAsync(sandbox, "small", 500);
        var largeArchive = await BuildManySmallFilesArchiveAsync(sandbox, "large", 5_000);

        await using var engine = new EngineFixture();

        var smallDelta = await MeasureAllocatedBytesAsync(
            () => engine.ExtractAsync(smallArchive, sandbox.Combine("dest-small")).AsTask());
        var largeDelta = await MeasureAllocatedBytesAsync(
            () => engine.ExtractAsync(largeArchive, sandbox.Combine("dest-large")).AsTask());

        Assert.True(
            largeDelta < smallDelta * 25,
            $"500-file extraction allocated {smallDelta:N0} B; 5,000-file extraction (10x the files) " +
            $"allocated {largeDelta:N0} B - more than 25x growth for a 10x file-count increase.");
    }

    /// <summary>
    /// Pays the one-time JIT/static-initialiser/first-provider-resolution cost on a throwaway archive
    /// so it never lands inside a measured delta.
    /// </summary>
    private static async Task WarmUpAsync(TempDirectory sandbox)
    {
        var source = sandbox.Combine("warmup-src");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "warm up");

        var archive = sandbox.Combine("warmup.zip");
        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [source]);
        await engine.ListAsync(archive);
        await engine.SearchAsync(archive);
        await engine.ExtractAsync(archive, sandbox.Combine("warmup-dest"));
    }

    private static async Task<long> MeasureExtractAllocationAsync(TempDirectory sandbox, string label, int fileBytes)
    {
        var source = sandbox.Combine($"{label}-src");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(
            Path.Combine(source, "payload.bin"),
            RandomNumberGenerator.GetBytes(fileBytes));

        var archive = sandbox.Combine($"{label}.zip");
        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [source], CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Fast));

        return await MeasureAllocatedBytesAsync(
            () => engine.ExtractAsync(archive, sandbox.Combine($"{label}-dest")).AsTask());
    }

    private static async Task<string> BuildSearchFixtureAsync(TempDirectory sandbox, string label, int count)
    {
        var source = sandbox.Combine($"{label}-src");
        Directory.CreateDirectory(source);
        for (var i = 0; i < count; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{i:D5}.txt"), "content");
        }

        // Exactly one entry anywhere in either fixture carries the extension the search below filters
        // on, so both fixtures have the same match count (one) regardless of their size.
        await File.WriteAllTextAsync(Path.Combine(source, "the-one-match.marker"), "content");

        var archive = sandbox.Combine($"{label}.zip");
        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [source], CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Fast));
        return archive;
    }

    private static async Task<string> BuildManySmallFilesArchiveAsync(TempDirectory sandbox, string label, int count)
    {
        var source = sandbox.Combine($"{label}-src");
        Directory.CreateDirectory(source);
        for (var i = 0; i < count; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{i:D5}.txt"), $"content {i}");
        }

        var archive = sandbox.Combine($"{label}.zip");
        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [source], CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Fast));
        return archive;
    }

    /// <summary>
    /// Allocated bytes across every thread during <paramref name="action"/>, not just the calling
    /// thread - an async handler hops threads through the thread pool, so a current-thread-only
    /// counter would silently attribute most of the work to whichever continuation happened to run
    /// last. Forces two full collections first so a GC the first measurement triggers does not get
    /// billed to the second.
    /// </summary>
    private static async Task<long> MeasureAllocatedBytesAsync(Func<Task> action)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await action().ConfigureAwait(false);
        var after = GC.GetTotalAllocatedBytes(precise: true);

        return after - before;
    }
}
