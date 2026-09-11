using System.Text;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// What extraction has to get right for a person to trust it: <c>EXT-001…016</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shapes that arrive off a real disk and the ways a destination can already be occupied. ZIP
/// throughout, for the same reason the creation matrix uses it: the managed reader needs no native
/// library, so none of these can be skipped on a machine that is missing one, and a skipped safety test
/// is an untested control.
/// </para>
/// <para>
/// Every conflict case asserts what happened to the file that was <em>already there</em>, not only what
/// the operation reported. "It said Skip" and "the existing file is untouched" are different claims,
/// and only the second one is the promise the product makes.
/// </para>
/// </remarks>
public sealed class ExtractionMatrixTests
{
    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    /// <summary>Builds a ZIP holding exactly the named entries, through the engine's own writer.</summary>
    private static async Task<string> BuildArchive(
        EngineFixture engine,
        TempDirectory sandbox,
        params string[] relativePaths)
    {
        var staging = sandbox.Combine("staging", Guid.NewGuid().ToString("N")[..8]);
        var root = Path.Combine(staging, "payload");

        foreach (var relative in relativePaths)
        {
            await WriteFile(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), $"content of {relative}");
        }

        var archive = Path.Combine(staging, "source.zip");
        await engine.CreateAsync(archive, [root]);
        return archive;
    }

    /// <summary>Every file under a folder, relative and separator-normalised, sorted, joined.</summary>
    private static string Tree(string root)
    {
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        return string.Join(
            " | ",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .Order(StringComparer.Ordinal));
    }

    /// <summary>EXT-001: one file comes out under its own name, in the folder that was chosen.</summary>
    [Fact]
    public async Task A_single_file_lands_where_it_was_asked_to()
    {
        using var sandbox = new TempDirectory("EXT-001");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "notes.txt");
        var destination = sandbox.Combine("out");

        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Equal(1, outcome.EntriesWritten);
        Assert.Equal("payload/notes.txt", Tree(destination));
        Assert.Equal(
            "content of notes.txt",
            await File.ReadAllTextAsync(Path.Combine(destination, "payload", "notes.txt")));
    }

    /// <summary>EXT-002: several files, and a tree, come out at full depth.</summary>
    [Fact]
    public async Task Multiple_files_and_nested_directories_survive()
    {
        using var sandbox = new TempDirectory("EXT-002");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(
            engine, sandbox, "a.txt", "b.txt", "one/deep.txt", "one/two/three/leaf.txt");
        var destination = sandbox.Combine("out");

        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Equal(4, outcome.EntriesWritten);
        Assert.Equal(
            "payload/a.txt | payload/b.txt | payload/one/deep.txt | payload/one/two/three/leaf.txt",
            Tree(destination));
    }

    /// <summary>EXT-003: Arabic names come out identical, and so does an Arabic destination folder.</summary>
    [Fact]
    public async Task Arabic_paths_and_an_arabic_destination_both_work()
    {
        using var sandbox = new TempDirectory("EXT-003");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "تقرير.txt", "أرشيف/ملاحظات ٢٠٢٦.txt");
        var destination = sandbox.Combine("المخرجات");

        await engine.ExtractAsync(archive, destination);

        Assert.Equal("payload/أرشيف/ملاحظات ٢٠٢٦.txt | payload/تقرير.txt", Tree(destination));
        Assert.Equal(
            "content of تقرير.txt",
            await File.ReadAllTextAsync(Path.Combine(destination, "payload", "تقرير.txt")));
    }

    /// <summary>EXT-004: names from the rest of Unicode survive the round trip too.</summary>
    [Fact]
    public async Task Unicode_paths_survive()
    {
        using var sandbox = new TempDirectory("EXT-004");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(
            engine, sandbox, "日本語.txt", "Ελληνικά.txt", "café-decomposed.txt");
        var destination = sandbox.Combine("out");

        await engine.ExtractAsync(archive, destination);

        Assert.Equal(
            "payload/café-decomposed.txt | payload/Ελληνικά.txt | payload/日本語.txt",
            Tree(destination));
    }

    /// <summary>
    /// EXT-005: extracting into a folder that already exists adds to it rather than refusing.
    /// </summary>
    /// <remarks>
    /// A directory that is already there is not a conflict — it is the normal case, because most people
    /// extract into a folder they already keep things in. Only a colliding <em>file</em> is a decision,
    /// and the files already in the folder are left exactly as they were.
    /// </remarks>
    [Fact]
    public async Task An_existing_destination_directory_is_not_a_conflict()
    {
        using var sandbox = new TempDirectory("EXT-005");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "new.txt");
        var destination = sandbox.Combine("out");
        await WriteFile(Path.Combine(destination, "unrelated.txt"), "was here first");

        await engine.ExtractAsync(archive, destination, b => b.With(o => o with { ConflictPolicy = ConflictPolicy.Fail }));

        Assert.Equal("payload/new.txt | unrelated.txt", Tree(destination));
        Assert.Equal("was here first", await File.ReadAllTextAsync(Path.Combine(destination, "unrelated.txt")));
    }

    /// <summary>EXT-006: Skip keeps what is on disk, and says how many it left alone.</summary>
    [Fact]
    public async Task Skip_keeps_the_existing_file()
    {
        using var sandbox = new TempDirectory("EXT-006");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "collides.txt", "fresh.txt");
        var destination = sandbox.Combine("out");
        var existing = await WriteFile(
            Path.Combine(destination, "payload", "collides.txt"), "the original, which must survive");

        var outcome = await engine.ExtractAsync(
            archive, destination, b => b.With(o => o with { ConflictPolicy = ConflictPolicy.Skip }));

        Assert.Equal("the original, which must survive", await File.ReadAllTextAsync(existing));
        Assert.Equal(1, outcome.EntriesWritten);
        Assert.Equal(1, outcome.EntriesSkipped);
        Assert.Equal("payload/collides.txt | payload/fresh.txt", Tree(destination));
    }

    /// <summary>EXT-007: Rename keeps both, and the original still holds the original bytes.</summary>
    [Fact]
    public async Task Rename_keeps_both_files()
    {
        using var sandbox = new TempDirectory("EXT-007");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "collides.txt");
        var destination = sandbox.Combine("out");
        var existing = await WriteFile(Path.Combine(destination, "payload", "collides.txt"), "the original");

        var outcome = await engine.ExtractAsync(
            archive, destination, b => b.With(o => o with { ConflictPolicy = ConflictPolicy.Rename }));

        Assert.Equal(1, outcome.EntriesWritten);
        Assert.Equal("the original", await File.ReadAllTextAsync(existing));

        var files = Directory.GetFiles(Path.Combine(destination, "payload"));
        Assert.Equal(2, files.Length);
        Assert.Contains(
            "content of collides.txt",
            string.Join("\n", await Task.WhenAll(files.Select(f => File.ReadAllTextAsync(f)))));
    }

    /// <summary>EXT-008: Overwrite replaces — but only because it was asked for by name.</summary>
    [Fact]
    public async Task Overwrite_replaces_only_when_it_was_chosen()
    {
        using var sandbox = new TempDirectory("EXT-008");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "collides.txt");
        var destination = sandbox.Combine("out");
        var existing = await WriteFile(Path.Combine(destination, "payload", "collides.txt"), "the original");

        await engine.ExtractAsync(
            archive, destination, b => b.With(o => o with { ConflictPolicy = ConflictPolicy.Overwrite }));

        Assert.Equal("content of collides.txt", await File.ReadAllTextAsync(existing));
        Assert.Single(Directory.GetFiles(Path.Combine(destination, "payload")));
    }

    /// <summary>
    /// EXT-009: Fail stops, and never destroys the file it stopped over.
    /// </summary>
    /// <remarks>
    /// It is a partial extraction, and this asserts that rather than wishing it away: whatever was
    /// written before the collision stays. What must not have happened is the collision itself being
    /// resolved — the existing file has to come out the other side byte for byte.
    /// </remarks>
    [Fact]
    public async Task Fail_stops_without_touching_the_existing_file()
    {
        using var sandbox = new TempDirectory("EXT-009");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "collides.txt");
        var destination = sandbox.Combine("out");
        var existing = await WriteFile(Path.Combine(destination, "payload", "collides.txt"), "the original");

        await Assert.ThrowsAnyAsync<Exception>(async () => await engine.ExtractAsync(
            archive, destination, b => b.With(o => o with { ConflictPolicy = ConflictPolicy.Fail })));

        Assert.Equal("the original", await File.ReadAllTextAsync(existing));
    }

    /// <summary>
    /// EXT-010: running the same extraction twice is governed by the policy, not by luck.
    /// </summary>
    /// <remarks>
    /// The second run is the commonest collision there is — someone extracts, forgets, and extracts
    /// again — and with the safe policies nothing on disk changes.
    /// </remarks>
    [Fact]
    public async Task Extracting_twice_is_governed_by_the_policy()
    {
        using var sandbox = new TempDirectory("EXT-010");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "a.txt", "b.txt");
        var destination = sandbox.Combine("out");

        await engine.ExtractAsync(archive, destination);
        var before = Tree(destination);

        var second = await engine.ExtractAsync(
            archive, destination, b => b.With(o => o with { ConflictPolicy = ConflictPolicy.Skip }));

        Assert.Equal(0, second.EntriesWritten);
        Assert.Equal(2, second.EntriesSkipped);
        Assert.Equal(before, Tree(destination));
    }

    /// <summary>
    /// EXT-011: an entry whose path escapes the destination is refused, and reported.
    /// </summary>
    /// <remarks>
    /// The archive is a hostile fixture rather than one this engine wrote — our own writer would refuse
    /// to produce it. What is asserted is both halves of the promise: nothing landed outside the chosen
    /// folder, and the refusal is visible rather than silent.
    /// </remarks>
    [Fact]
    public async Task An_entry_that_escapes_the_destination_is_refused_and_reported()
    {
        using var sandbox = new TempDirectory("EXT-011");
        await using var engine = new EngineFixture();

        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            ("../escaped.txt", "outside"),
            ("safe.txt", "inside"));

        var destination = sandbox.Combine("out");
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.True(outcome.HadBlockedEntries, "the traversal entry was not reported");
        Assert.Equal("safe.txt", Tree(destination));
        Assert.False(
            File.Exists(sandbox.Combine("escaped.txt")),
            "an entry was written outside the destination");
    }

    /// <summary>EXT-012: a file that is not an archive fails, and writes nothing.</summary>
    [Fact]
    public async Task An_invalid_archive_fails_and_writes_nothing()
    {
        using var sandbox = new TempDirectory("EXT-012");
        await using var engine = new EngineFixture();

        var notAnArchive = await WriteFile(sandbox.Combine("notes.zip"), "this is just text");
        var destination = sandbox.Combine("out");

        await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(notAnArchive, destination));

        Assert.Equal(string.Empty, Tree(destination));
    }

    /// <summary>
    /// EXT-013: a destination that cannot exist fails, and leaves the thing in its way alone.
    /// </summary>
    /// <remarks>
    /// The destination's parent is an existing <em>file</em> here, which no filesystem will turn into a
    /// directory.
    /// </remarks>
    [Fact]
    public async Task An_impossible_destination_fails_cleanly()
    {
        using var sandbox = new TempDirectory("EXT-013");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "a.txt");
        var blocker = await WriteFile(sandbox.Combine("not-a-folder"), "I am a file");

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await engine.ExtractAsync(archive, Path.Combine(blocker, "out")));

        Assert.Equal("I am a file", await File.ReadAllTextAsync(blocker));
    }

    /// <summary>
    /// EXT-014: a cancelled extraction writes nothing further, and leaves no temporary behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancelled before the call rather than mid-flight, which makes it deterministic: a race against a
    /// fast ZIP read would pass on a slow machine and prove nothing on a fast one. What it establishes
    /// is the contract at the handler boundary — a cancelled extraction raises
    /// <see cref="OperationCanceledException"/> and produces no outcome to mistake for a result.
    /// </para>
    /// <para>
    /// <b>What this does not prove:</b> that writes stop promptly once an extraction is under way. That
    /// is <c>OPX-006</c> and <c>OPX-016</c>, which watch the destination while the writer is running,
    /// and <c>OPQ-022/023</c>, which prove the state a cancelled operation ends in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cancelled_extraction_produces_no_outcome_and_no_temporary()
    {
        using var sandbox = new TempDirectory("EXT-014");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "a.txt", "b.txt");
        var destination = sandbox.Combine("out");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.ExtractAsync(archive, destination, cancellationToken: cancellation.Token));

        foreach (var leftover in Directory.Exists(destination)
            ? Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            : [])
        {
            Assert.False(
                Path.GetFileName(leftover).StartsWith(".noba-", StringComparison.Ordinal),
                $"a temporary file survived cancellation: {leftover}");
        }
    }

    /// <summary>
    /// EXT-015: many entries all arrive, once each.
    /// </summary>
    /// <remarks>
    /// Not a performance test — there is no time assertion, because a wall clock on shared hardware
    /// measures the hardware. It establishes that nothing is dropped or written twice at a size where a
    /// per-entry mistake stops being visible by eye.
    /// </remarks>
    [Fact]
    public async Task Many_entries_all_arrive_exactly_once()
    {
        using var sandbox = new TempDirectory("EXT-015");
        await using var engine = new EngineFixture();

        const int count = 1_500;
        var names = Enumerable.Range(0, count).Select(i => $"g{i % 20:D2}/f{i:D5}.txt").ToArray();

        var archive = await BuildArchive(engine, sandbox, names);
        var destination = sandbox.Combine("out");

        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Equal(count, outcome.EntriesWritten);

        var written = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
        Assert.Equal(count, written.Length);
        Assert.Equal(count, written.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// EXT-016: extracting a selection writes only the selection.
    /// </summary>
    /// <remarks>
    /// The failure worth guarding against is the opposite of the obvious one: not that too little comes
    /// out, but that the selection is ignored and the whole archive lands in the folder — which looks
    /// like success until somebody notices the folder is full of things they did not ask for.
    /// </remarks>
    [Fact]
    public async Task Extracting_a_selection_writes_only_the_selection()
    {
        using var sandbox = new TempDirectory("EXT-016");
        await using var engine = new EngineFixture();

        var archive = await BuildArchive(engine, sandbox, "wanted.txt", "unwanted.txt", "deep/also-wanted.txt");
        var destination = sandbox.Combine("out");

        var chosen = new List<ArchivePath>();
        foreach (var candidate in new[] { "payload/wanted.txt", "payload/deep/also-wanted.txt" })
        {
            Assert.True(ArchivePath.TryCreate(candidate, out var path, out _));
            chosen.Add(path);
        }

        var outcome = await engine.ExtractAsync(
            archive, destination, b => b.With(o => o with { SelectedEntries = chosen }));

        Assert.Equal(2, outcome.EntriesWritten);
        Assert.Equal("payload/deep/also-wanted.txt | payload/wanted.txt", Tree(destination));
    }
}
