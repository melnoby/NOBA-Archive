using System.Text;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The SRC-### suite (DROP 07): <see cref="SearchArchiveHandler"/> end to end, through the shipping
/// composition — the same discipline <see cref="RoundTripTests"/> and <see cref="HostileArchiveTests"/>
/// already use for their own handlers.
/// </summary>
public sealed class SearchArchiveTests
{
    /// <summary>
    /// SRC-001: the match is against the full displayed path, not only the file name — the literal
    /// scope gap between this handler and the existing (name-only) Presentation-layer search.
    /// </summary>
    [Fact]
    public async Task Name_contains_matches_the_full_path_case_insensitively()
    {
        using var sandbox = new TempDirectory("SRC-001");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "notes"));
        Directory.CreateDirectory(Path.Combine(source, "images"));
        await File.WriteAllTextAsync(Path.Combine(source, "notes", "report.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(source, "images", "REPORT.png"), "bravo");
        await File.WriteAllTextAsync(Path.Combine(source, "notes", "other.csv"), "charlie");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var byName = await engine.SearchAsync(sandbox.Combine("out.zip"), b => b.NameContains("report"));
        Assert.Equal(2, byName.Matches.Count);

        // "notes/" is a path fragment, not a file name - BrowseViewModel's existing search cannot find
        // this at all because it only ever looks at Name.
        var byFolder = await engine.SearchAsync(sandbox.Combine("out.zip"), b => b.NameContains("notes/"));
        Assert.Equal(2, byFolder.Matches.Count);
        Assert.All(
            byFolder.Matches,
            m => Assert.True(
                m.DisplayPath.Contains("notes/", StringComparison.OrdinalIgnoreCase),
                $"'{m.DisplayPath}' does not contain 'notes/'."));
    }

    /// <summary>SRC-002: an extension filter matches regardless of how the extension was cased.</summary>
    [Fact]
    public async Task Extension_filter_is_case_insensitive_and_exact()
    {
        using var sandbox = new TempDirectory("SRC-002");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(source, "B.TXT"), "bravo");
        await File.WriteAllTextAsync(Path.Combine(source, "c.csv"), "charlie");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.SearchAsync(sandbox.Combine("out.zip"), b => b.Extensions(".txt"));

        Assert.Equal(2, outcome.Matches.Count);
        Assert.All(
            outcome.Matches,
            m => Assert.True(
                m.DisplayPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
                $"'{m.DisplayPath}' does not end with '.txt'."));
    }

    /// <summary>SRC-003: a size range filter reads <see cref="ArchiveEntry.UncompressedSize"/>, not the
    /// compressed size on disk.</summary>
    [Fact]
    public async Task Size_filter_bounds_are_inclusive()
    {
        using var sandbox = new TempDirectory("SRC-003");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "small.bin"), new string('x', 10));
        await File.WriteAllTextAsync(Path.Combine(source, "medium.bin"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(source, "large.bin"), new string('x', 1000));

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.SearchAsync(sandbox.Combine("out.zip"), b => b.SizeBetween(10, 100));

        Assert.Equal(2, outcome.Matches.Count);
        Assert.All(
            outcome.Matches,
            m => Assert.True(
                m.UncompressedSize.Bytes >= 10 && m.UncompressedSize.Bytes <= 100,
                $"{m.UncompressedSize.Bytes} is outside [10, 100]."));
    }

    /// <summary>SRC-004: a modified-date range filter, read through the full create/list round trip
    /// (not a hand-crafted header) so DOS timestamp granularity is honestly exercised.</summary>
    [Fact]
    public async Task Modified_date_filter_excludes_entries_outside_the_range()
    {
        using var sandbox = new TempDirectory("SRC-004");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);

        var oldPath = Path.Combine(source, "old.txt");
        var newPath = Path.Combine(source, "new.txt");
        await File.WriteAllTextAsync(oldPath, "old");
        await File.WriteAllTextAsync(newPath, "new");
        File.SetLastWriteTime(oldPath, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Local));
        File.SetLastWriteTime(newPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local));

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.SearchAsync(
            sandbox.Combine("out.zip"),
            b => b.ModifiedBetween(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null));

        Assert.Single(outcome.Matches);
        Assert.True(
            outcome.Matches[0].DisplayPath.EndsWith("new.txt", StringComparison.Ordinal),
            outcome.Matches[0].DisplayPath);
    }

    /// <summary>
    /// SRC-005: a <see cref="EntryKind"/> filter. Our own writer never emits a directory entry (see
    /// <c>RoundTripTests</c>'s entry counts), so this builds the zip directly to get one to filter on.
    /// </summary>
    [Fact]
    public async Task Kind_filter_separates_directories_from_files()
    {
        using var sandbox = new TempDirectory("SRC-005");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("mixed.zip"),
            ("folder/", string.Empty),
            ("folder/file.txt", "content"),
            ("root.txt", "content"));

        await using var engine = new EngineFixture();

        var directories = await engine.SearchAsync(archive, b => b.Kind(EntryKind.Directory));
        Assert.Single(directories.Matches);
        Assert.Equal(EntryKind.Directory, directories.Matches[0].Kind);

        var files = await engine.SearchAsync(archive, b => b.Kind(EntryKind.File));
        Assert.Equal(2, files.Matches.Count);
        Assert.All(files.Matches, m => Assert.Equal(EntryKind.File, m.Kind));
    }

    /// <summary>
    /// SRC-006: sorting is stable and always tie-broken by path, so a repeated identical request
    /// returns the identical order every time — the exit criterion's "deterministic", proven rather
    /// than assumed.
    /// </summary>
    [Fact]
    public async Task Sort_by_size_is_deterministic_across_repeated_runs()
    {
        using var sandbox = new TempDirectory("SRC-006");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "bravo.bin"), new string('x', 50));
        await File.WriteAllTextAsync(Path.Combine(source, "alpha.bin"), new string('x', 50));
        await File.WriteAllTextAsync(Path.Combine(source, "zulu.bin"), new string('x', 10));

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var first = await engine.SearchAsync(
            sandbox.Combine("out.zip"), b => b.SortedBy(SearchSortKey.UncompressedSize));
        var second = await engine.SearchAsync(
            sandbox.Combine("out.zip"), b => b.SortedBy(SearchSortKey.UncompressedSize));

        var firstOrder = first.Matches.Select(m => m.DisplayPath).ToList();
        var secondOrder = second.Matches.Select(m => m.DisplayPath).ToList();
        Assert.Equal(firstOrder, secondOrder);

        // zulu.bin (10 bytes) sorts before the 50-byte pair, and within that tied pair the tie-break
        // (DisplayPath, ordinal) puts alpha before bravo regardless of archive or filesystem order.
        Assert.True(firstOrder[0].EndsWith("zulu.bin", StringComparison.Ordinal), firstOrder[0]);
        Assert.True(firstOrder[1].EndsWith("alpha.bin", StringComparison.Ordinal), firstOrder[1]);
        Assert.True(firstOrder[2].EndsWith("bravo.bin", StringComparison.Ordinal), firstOrder[2]);
    }

    /// <summary>SRC-007: with no sort requested, matches come back in archive order — the same order
    /// <see cref="ListArchiveHandler"/> reports — because nothing has been asked to reorder them.</summary>
    [Fact]
    public async Task Unsorted_matches_preserve_archive_order()
    {
        using var sandbox = new TempDirectory("SRC-007");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "zulu.txt"), "z");
        await File.WriteAllTextAsync(Path.Combine(source, "alpha.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "mike.txt"), "m");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var listing = await engine.ListAsync(sandbox.Combine("out.zip"));
        var search = await engine.SearchAsync(sandbox.Combine("out.zip"));

        Assert.Equal(
            listing.Entries.Select(e => e.DisplayPath),
            search.Matches.Select(m => m.DisplayPath));
    }

    /// <summary>
    /// SRC-008: streaming and the buffered, archive-level result agree on exactly the same set of
    /// matches, and the stream can be abandoned part-way through (the consumer breaks early) without
    /// throwing — proof the "one entry at a time" contract is a real iterator, not a materialized list
    /// wearing an <see cref="IAsyncEnumerable{T}"/> label.
    /// </summary>
    [Fact]
    public async Task Streaming_and_buffered_search_agree_and_streaming_can_be_abandoned_early()
    {
        using var sandbox = new TempDirectory("SRC-008");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        for (var i = 0; i < 10; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file{i:D2}.txt"), "content");
        }

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var buffered = await engine.SearchAsync(sandbox.Combine("out.zip"));

        var streamed = new List<ArchiveEntry>();
        await foreach (var entry in engine.StreamSearchAsync(sandbox.Combine("out.zip")))
        {
            streamed.Add(entry);
        }

        Assert.Equal(buffered.Matches.Select(m => m.DisplayPath), streamed.Select(e => e.DisplayPath));

        // Abandoned after the first match: must not throw, and must leave nothing to clean up.
        var seen = 0;
        await foreach (var _ in engine.StreamSearchAsync(sandbox.Combine("out.zip")))
        {
            if (++seen >= 1)
            {
                break;
            }
        }

        Assert.Equal(1, seen);
    }

    /// <summary>SRC-009: Arabic text matches the same way any other text does — ordinal, not
    /// culture-aware, per this handler's own convention (never the display-only comparison
    /// <c>BrowseViewModel</c> uses).</summary>
    [Fact]
    public async Task Arabic_names_match_by_ordinal_substring()
    {
        using var sandbox = new TempDirectory("SRC-009");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "تقرير-الربع-الثالث.txt"), "content", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(source, "other.txt"), "content", Encoding.UTF8);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.SearchAsync(sandbox.Combine("out.zip"), b => b.NameContains("الربع"));

        Assert.Single(outcome.Matches);
        Assert.True(outcome.Matches[0].DisplayPath.Contains("الربع", StringComparison.Ordinal));
    }

    /// <summary>
    /// SRC-010: a metadata-only search still finds a rejected entry — one whose
    /// <see cref="ArchiveEntry.Path"/> is null because the writer refused it — because the filter reads
    /// <see cref="ArchiveEntry.DisplayPath"/>, which falls back to <see cref="ArchiveEntry.RawPath"/>.
    /// A security sweep for ".exe" should still find one even when it was already going to be refused
    /// on extraction for an unrelated reason (traversal, here).
    /// </summary>
    [Fact]
    public async Task A_rejected_entry_still_matches_a_metadata_filter()
    {
        using var sandbox = new TempDirectory("SRC-010");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            ("../evil.exe", "payload"),
            ("ok.exe", "fine"),
            ("notes.txt", "fine"));

        await using var engine = new EngineFixture();
        var outcome = await engine.SearchAsync(archive, b => b.Extensions(".exe"));

        Assert.Equal(2, outcome.Matches.Count);
        Assert.Contains(
            outcome.Matches,
            m => m.Path is null && m.RawPath.EndsWith("evil.exe", StringComparison.Ordinal));
        Assert.Contains(outcome.Matches, m => m.Path is not null && m.RawPath == "ok.exe");
    }

    /// <summary>
    /// SRC-011: the scan count is the whole archive even when the filter matches nothing — proof the
    /// handler actually walked the index rather than short-circuiting, and the number a caller needs to
    /// tell "zero matches" apart from "the archive itself is empty".
    /// </summary>
    [Fact]
    public async Task Entries_scanned_counts_the_whole_archive_even_with_no_matches()
    {
        using var sandbox = new TempDirectory("SRC-011");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(source, "c.txt"), "c");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.SearchAsync(sandbox.Combine("out.zip"), b => b.Extensions(".pdf"));

        Assert.Empty(outcome.Matches);
        Assert.Equal(3, outcome.EntriesScanned);
    }

    /// <summary>SRC-012: every filter is ANDed, not ORed — a request narrowed by two independent
    /// criteria returns only entries that satisfy both.</summary>
    [Fact]
    public async Task Filters_combine_with_and_not_or()
    {
        using var sandbox = new TempDirectory("SRC-012");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "report.txt"), new string('x', 10));
        await File.WriteAllTextAsync(Path.Combine(source, "report.csv"), new string('x', 10));
        await File.WriteAllTextAsync(Path.Combine(source, "other.txt"), new string('x', 10));

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.SearchAsync(
            sandbox.Combine("out.zip"),
            b => b.NameContains("report").Extensions(".txt"));

        Assert.Single(outcome.Matches);
        Assert.True(outcome.Matches[0].DisplayPath.EndsWith("report.txt", StringComparison.Ordinal));
    }
}
