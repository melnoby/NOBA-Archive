using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// What "create" has to survive: <c>CRT-001…013</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shapes a person actually selects — one file, a handful of files, a folder, several folders, a
/// tree, names in Arabic and in scripts nobody tested with — and the ways the request can be wrong.
/// <see cref="SevenZipCreationTests"/> proves the 7Z <em>writer</em>; this proves the workflow around
/// it, so it deliberately uses ZIP: the managed writer needs no native library, which means none of
/// these can be skipped on a machine that is missing one. A skipped safety test is an untested control.
/// </para>
/// <para>
/// Every negative case asserts twice: the operation failed, <em>and</em> nothing was left on disk. An
/// error message with a half-written archive beside it is still data loss waiting to be believed.
/// </para>
/// </remarks>
public sealed class CreationMatrixTests
{
    /// <summary>
    /// Entry paths in the archive, files only, sorted, joined.
    /// </summary>
    /// <remarks>
    /// One string rather than a list on purpose: the assertion is then an unambiguous string comparison
    /// in both test runners, and a failure prints the whole archive instead of "collections differ".
    /// </remarks>
    private static async Task<string> EntriesOf(EngineFixture engine, string archive)
    {
        var listing = await engine.ListAsync(archive);
        return string.Join(
            " | ",
            listing.Entries
                .Where(static e => !e.IsDirectory)
                .Select(static e => e.DisplayPath)
                .Order(StringComparer.Ordinal));
    }

    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    /// <summary>Nothing published, and no staging file masquerading as content.</summary>
    private static void AssertNothingLandedAt(string archive, TempDirectory sandbox)
    {
        Assert.False(File.Exists(archive), $"a refused creation published '{archive}'");

        foreach (var leftover in Directory.EnumerateFiles(sandbox.Path, ".noba-*.tmp", SearchOption.AllDirectories))
        {
            Assert.Fail($"a staging file survived the failure: {leftover}");
        }
    }

    /// <summary>CRT-001: one file goes in under its own name, with no folder invented around it.</summary>
    [Fact]
    public async Task A_single_file_is_archived_under_its_own_name()
    {
        using var sandbox = new TempDirectory("CRT-001");
        var file = await WriteFile(sandbox.Combine("notes.txt"), "hello");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(archive, [file]);

        Assert.Equal(1, outcome.EntriesAdded);
        Assert.Equal("notes.txt", await EntriesOf(engine, archive));
    }

    /// <summary>CRT-002: several separately chosen files sit side by side, not nested under anything.</summary>
    [Fact]
    public async Task Multiple_files_are_archived_side_by_side()
    {
        using var sandbox = new TempDirectory("CRT-002");
        var a = await WriteFile(sandbox.Combine("a.txt"), "one");
        var b = await WriteFile(sandbox.Combine("deeper", "b.txt"), "two");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(archive, [a, b]);

        Assert.Equal(2, outcome.EntriesAdded);

        // b came from a subfolder of the sandbox, but it was chosen as a file: the folder it happened to
        // live in is not part of what the user asked for, so it does not appear in the archive.
        Assert.Equal("a.txt | b.txt", await EntriesOf(engine, archive));
    }

    /// <summary>CRT-003: a folder brings its own name and its whole tree, at full depth.</summary>
    [Fact]
    public async Task A_folder_keeps_its_name_and_its_nesting()
    {
        using var sandbox = new TempDirectory("CRT-003");
        var root = sandbox.Combine("project");
        await WriteFile(Path.Combine(root, "readme.md"), "top");
        await WriteFile(Path.Combine(root, "src", "main.cs"), "one deep");
        await WriteFile(Path.Combine(root, "src", "deep", "deeper", "leaf.txt"), "three deep");

        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(archive, [root]);

        Assert.Equal(3, outcome.EntriesAdded);
        Assert.Equal(
            "project/readme.md | project/src/deep/deeper/leaf.txt | project/src/main.cs",
            await EntriesOf(engine, archive));
    }

    /// <summary>CRT-004: two folders keep their own roots rather than being merged into one.</summary>
    /// <remarks>
    /// Merging them would silently collide any two files with the same relative name, and the archive
    /// would be short an entry with nothing said about it.
    /// </remarks>
    [Fact]
    public async Task Multiple_folders_keep_separate_roots()
    {
        using var sandbox = new TempDirectory("CRT-004");
        var first = sandbox.Combine("first");
        var second = sandbox.Combine("second");
        await WriteFile(Path.Combine(first, "shared-name.txt"), "from the first folder");
        await WriteFile(Path.Combine(second, "shared-name.txt"), "from the second folder");

        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(archive, [first, second]);

        Assert.Equal(2, outcome.EntriesAdded);
        Assert.Equal(
            "first/shared-name.txt | second/shared-name.txt",
            await EntriesOf(engine, archive));

        // And both survive the round trip with their own content, which is what "not merged" means.
        var destination = sandbox.Combine("dest");
        await engine.ExtractAsync(archive, destination);

        Assert.Equal(
            "from the first folder",
            await File.ReadAllTextAsync(Path.Combine(destination, "first", "shared-name.txt")));
        Assert.Equal(
            "from the second folder",
            await File.ReadAllTextAsync(Path.Combine(destination, "second", "shared-name.txt")));
    }

    /// <summary>CRT-005: Arabic names go in and come back identical, folder names included.</summary>
    [Fact]
    public async Task Arabic_names_round_trip_unchanged()
    {
        using var sandbox = new TempDirectory("CRT-005");
        var root = sandbox.Combine("مستندات");
        await WriteFile(Path.Combine(root, "تقرير سنوي.txt"), "محتوى");
        await WriteFile(Path.Combine(root, "أرشيف", "ملاحظات ٢٠٢٦.txt"), "المزيد");

        var archive = sandbox.Combine("عربي.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [root]);

        Assert.Equal(
            "مستندات/أرشيف/ملاحظات ٢٠٢٦.txt | مستندات/تقرير سنوي.txt",
            await EntriesOf(engine, archive));

        var destination = sandbox.Combine("dest");
        await engine.ExtractAsync(archive, destination);
        Assert.Equal("محتوى", await File.ReadAllTextAsync(
            Path.Combine(destination, "مستندات", "تقرير سنوي.txt")));
    }

    /// <summary>
    /// CRT-006: names from the rest of Unicode survive too, byte for byte.
    /// </summary>
    /// <remarks>
    /// The interesting one is the third: a decomposed "é" (e + U+0301). A layer that helpfully
    /// normalises names would produce an archive whose entry does not match the file that went into it,
    /// and the mismatch only shows up when somebody tries to find the file again. The assertion is on
    /// the exact code points.
    /// </remarks>
    [Fact]
    public async Task Unicode_names_survive_creation()
    {
        using var sandbox = new TempDirectory("CRT-006");
        var root = sandbox.Combine("unicode");

        var names = new[]
        {
            "日本語のファイル.txt",
            "Ελληνικά.txt",
            "café-decomposed.txt",
            "emoji-\U0001F5C4️.txt",
        };

        foreach (var name in names)
        {
            await WriteFile(Path.Combine(root, name), name);
        }

        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(archive, [root]);

        Assert.Equal(names.Length, outcome.EntriesAdded);
        Assert.Equal(
            string.Join(" | ", names.Select(static n => $"unicode/{n}").Order(StringComparer.Ordinal)),
            await EntriesOf(engine, archive));
    }

    /// <summary>CRT-007: nothing selected produces no archive and a reason, not an empty one.</summary>
    /// <remarks>
    /// An empty archive is a plausible-looking artefact that a user would file away and discover was
    /// worthless later. Refusing is the safe answer, and the same answer is given whether the selection
    /// was empty or every folder in it turned out to hold no files.
    /// </remarks>
    [Fact]
    public async Task An_empty_selection_is_refused()
    {
        using var sandbox = new TempDirectory("CRT-007");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, []));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
        AssertNothingLandedAt(archive, sandbox);
    }

    /// <summary>CRT-008: a folder with no files in it is the same refusal, not an empty archive.</summary>
    [Fact]
    public async Task A_folder_containing_nothing_is_refused()
    {
        using var sandbox = new TempDirectory("CRT-008");
        var empty = sandbox.Combine("empty");
        Directory.CreateDirectory(Path.Combine(empty, "still-empty"));

        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [empty]));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
        AssertNothingLandedAt(archive, sandbox);
    }

    /// <summary>
    /// CRT-009: an existing archive is never quietly replaced, and its bytes are untouched.
    /// </summary>
    /// <remarks>
    /// Asserted before any compression happens — the publisher refuses up front — so the second archive
    /// is not built and then thrown away. The content check is the one that matters: "it failed" would
    /// still be true of a run that had already truncated the file.
    /// </remarks>
    [Fact]
    public async Task An_existing_archive_is_not_overwritten_without_consent()
    {
        using var sandbox = new TempDirectory("CRT-009");
        var first = await WriteFile(sandbox.Combine("source", "original.txt"), "the first archive");
        var second = await WriteFile(sandbox.Combine("other", "replacement.txt"), "the second archive");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [first]);
        var originalBytes = await File.ReadAllBytesAsync(archive);

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [second]));

        Assert.Equal(ArchiveErrorCode.IoFailure, failure.Error.Code);
        Assert.Contains("already exists", failure.Error.TechnicalDetail);
        var afterRefusal = await File.ReadAllBytesAsync(archive);
        Assert.True(
            originalBytes.AsSpan().SequenceEqual(afterRefusal),
            "the refused creation altered the archive it refused to replace");

        // With consent, the same request goes through and the new content is what is there.
        await engine.CreateAsync(archive, [second], overwrite: true);
        Assert.Equal("replacement.txt", await EntriesOf(engine, archive));
    }

    /// <summary>
    /// CRT-010: a destination that cannot exist fails, and leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// The target's parent is an existing <em>file</em> here, which no filesystem will turn into a
    /// directory. A missing folder is deliberately not this test: creation creates the destination
    /// folder on purpose, because a user who typed a new folder name meant it.
    /// </remarks>
    [Fact]
    public async Task An_impossible_destination_fails_without_leaving_anything()
    {
        using var sandbox = new TempDirectory("CRT-010");
        var source = await WriteFile(sandbox.Combine("source", "a.txt"), "content");
        var blocker = await WriteFile(sandbox.Combine("not-a-folder"), "I am a file");
        var archive = Path.Combine(blocker, "out.zip");

        await using var engine = new EngineFixture();
        await Assert.ThrowsAnyAsync<Exception>(async () => await engine.CreateAsync(archive, [source]));

        Assert.False(File.Exists(archive), "an archive appeared under a path that is not a directory");
        Assert.Equal("I am a file", await File.ReadAllTextAsync(blocker));
    }

    /// <summary>CRT-011: a source that vanished is named in the error, and publishes nothing.</summary>
    [Fact]
    public async Task A_missing_source_is_named_and_publishes_nothing()
    {
        using var sandbox = new TempDirectory("CRT-011");
        var present = await WriteFile(sandbox.Combine("source", "a.txt"), "content");
        var absent = sandbox.Combine("source", "gone.txt");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [present, absent]));

        Assert.Equal(ArchiveErrorCode.IoFailure, failure.Error.Code);
        Assert.Contains("gone.txt", failure.Error.TechnicalDetail);
        AssertNothingLandedAt(archive, sandbox);
    }

    /// <summary>
    /// CRT-012: cancellation is not a completion — no archive, and no staging file left dressed up as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is cancelled before the call rather than mid-flight, which makes this deterministic:
    /// a race that cancels "somewhere during" a fast ZIP write would pass on a slow machine and prove
    /// nothing on a fast one. Mid-flight cancellation is covered where it can be made deterministic, in
    /// <c>OPX-014</c> and <c>OPX-016</c>.
    /// </para>
    /// <para>
    /// <b>What this does not prove:</b> that a cancellation arriving after the builder has been opened
    /// unwinds cleanly. That is INT-013's subject, and it is asserted there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_cancelled_creation_publishes_nothing()
    {
        using var sandbox = new TempDirectory("CRT-012");
        var source = await WriteFile(sandbox.Combine("source", "a.txt"), "content");
        var archive = sandbox.Combine("out.zip");

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await using var engine = new EngineFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await engine.CreateAsync(archive, [source], cancellationToken: cancellation.Token));

        AssertNothingLandedAt(archive, sandbox);
    }

    /// <summary>
    /// CRT-013: a few thousand files and one large one, counted exactly.
    /// </summary>
    /// <remarks>
    /// Not a performance test — there is no time assertion, because a wall clock on shared CI hardware
    /// measures the hardware. What it proves is that nothing is dropped or double-counted at a size
    /// where a per-entry mistake stops being visible by eye, and that a file larger than any internal
    /// buffer goes through the same path as the small ones.
    /// </remarks>
    [Fact]
    public async Task Thousands_of_files_and_one_large_one_are_all_accounted_for()
    {
        using var sandbox = new TempDirectory("CRT-013");
        var root = sandbox.Combine("bulk");
        Directory.CreateDirectory(root);

        const int count = 2_000;
        for (var i = 0; i < count; i++)
        {
            // Spread across folders, so the tree has depth as well as breadth.
            var path = Path.Combine(root, $"g{i % 25:D2}", $"f{i:D5}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, $"file number {i}");
        }

        // Incompressible and larger than any plausible buffer.
        await File.WriteAllBytesAsync(Path.Combine(root, "large.bin"), RandomNumberGenerator.GetBytes(16 * 1024 * 1024));

        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(
            archive,
            [root],
            CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Fast));

        Assert.Equal(count + 1, outcome.EntriesAdded);

        var listing = await engine.ListAsync(archive);
        var entries = listing.Entries.Where(static e => !e.IsDirectory).Select(static e => e.DisplayPath).ToList();
        Assert.Equal(count + 1, entries.Count);
        Assert.Equal(count + 1, entries.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(ArchiveTestStatus.Valid, (await engine.TestAsync(archive)).Status);
    }
}
