using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The INT-### suite (TESTING.md §5): create, then extract, then compare bytes.
/// </summary>
/// <remarks>
/// Round-tripping is the only test that catches a whole family of defects at once — a truncated final
/// block, a dropped last entry, a mangled name, a timestamp written in the wrong epoch. Each of these
/// looks fine in a unit test of the component that caused it.
/// </remarks>
public sealed class RoundTripTests
{
    /// <summary>INT-001: content survives create → extract unchanged, byte for byte.</summary>
    [Fact]
    public async Task A_tree_round_trips_byte_for_byte()
    {
        using var sandbox = new TempDirectory("INT-001");
        var source = BuildSourceTree(sandbox);

        await using var engine = new EngineFixture();
        var created = await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);
        Assert.Equal(4, created.EntriesAdded);

        var outcome = await engine.ExtractAsync(sandbox.Combine("out.zip"), sandbox.Combine("dest"));
        Assert.Empty(outcome.Blocked);
        Assert.Equal(4, outcome.EntriesWritten);

        AssertTreesMatch(source, Path.Combine(sandbox.Combine("dest"), "source"));
    }

    /// <summary>INT-002: names outside ASCII survive the round trip exactly.</summary>
    [Theory]
    [InlineData("تقرير-الربع-الثالث.txt")]
    [InlineData("日本語のファイル.txt")]
    [InlineData("Ünïcödé — em dash.txt")]
    [InlineData("emoji-🗜️-name.txt")]
    [InlineData("space in name.txt")]
    public async Task A_unicode_name_round_trips_exactly(string fileName)
    {
        using var sandbox = new TempDirectory("INT-002");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, fileName), "content", Encoding.UTF8);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);
        await engine.ExtractAsync(sandbox.Combine("out.zip"), sandbox.Combine("dest"));

        var extracted = Path.Combine(sandbox.Combine("dest"), "source", fileName);
        Assert.True(File.Exists(extracted), $"'{fileName}' did not come back under its own name.");
        Assert.Equal("content", await File.ReadAllTextAsync(extracted, Encoding.UTF8));
    }

    /// <summary>INT-003: a zero-byte file is a file, not an absence.</summary>
    [Fact]
    public async Task An_empty_file_round_trips_as_an_empty_file()
    {
        using var sandbox = new TempDirectory("INT-003");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "empty.bin"), []);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);
        await engine.ExtractAsync(sandbox.Combine("out.zip"), sandbox.Combine("dest"));

        var extracted = Path.Combine(sandbox.Combine("dest"), "source", "empty.bin");
        Assert.True(File.Exists(extracted));
        Assert.Equal(0, new FileInfo(extracted).Length);
    }

    /// <summary>
    /// INT-004: a file larger than every internal buffer, so the copy loop's boundary handling is
    /// actually exercised rather than fitting in one read.
    /// </summary>
    [Fact]
    public async Task A_multi_buffer_file_round_trips_with_the_same_hash()
    {
        using var sandbox = new TempDirectory("INT-004");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);

        // 5 MB + 7 bytes: deliberately not a multiple of the 1 MB copy buffer.
        var payload = HostileArchiveTests.CompressibleBytes((5 * 1024 * 1024) + 7);
        var path = Path.Combine(source, "large.bin");
        await File.WriteAllBytesAsync(path, payload);
        var expected = Convert.ToHexString(SHA256.HashData(payload));

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);
        await engine.ExtractAsync(sandbox.Combine("out.zip"), sandbox.Combine("dest"));

        var extracted = Path.Combine(sandbox.Combine("dest"), "source", "large.bin");
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(extracted)));
        Assert.Equal(expected, actual);
    }

    /// <summary>INT-005: compressible input must actually get smaller, or compression is not happening.</summary>
    [Fact]
    public async Task Compressible_input_produces_a_smaller_archive()
    {
        using var sandbox = new TempDirectory("INT-005");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "repetitive.txt"),
            string.Concat(Enumerable.Repeat("the same line over and over\n", 20_000)));

        await using var engine = new EngineFixture();
        var created = await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        Assert.True(
            created.ArchiveSize.Bytes < created.BytesRead.Bytes / 10,
            $"{created.BytesRead} compressed only to {created.ArchiveSize}.");
    }

    /// <summary>INT-006: modification times come back, within the two-second DOS timestamp granularity.</summary>
    [Fact]
    public async Task Modification_times_survive_the_round_trip()
    {
        using var sandbox = new TempDirectory("INT-006");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        var path = Path.Combine(source, "dated.txt");
        await File.WriteAllTextAsync(path, "content");

        var stamp = new DateTime(2021, 6, 15, 10, 30, 00, DateTimeKind.Local);
        File.SetLastWriteTime(path, stamp);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);
        await engine.ExtractAsync(sandbox.Combine("out.zip"), sandbox.Combine("dest"));

        var extracted = File.GetLastWriteTime(Path.Combine(sandbox.Combine("dest"), "source", "dated.txt"));
        Assert.True(
            Math.Abs((extracted - stamp).TotalSeconds) <= 2,
            $"expected ~{stamp:O}, got {extracted:O}.");
    }

    /// <summary>INT-007: content decides the format; the extension is only a hint.</summary>
    [Fact]
    public async Task A_misnamed_archive_is_still_detected_by_content()
    {
        using var sandbox = new TempDirectory("INT-007");
        var source = BuildSourceTree(sandbox);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var misnamed = sandbox.Combine("archive.docx");
        File.Move(sandbox.Combine("out.zip"), misnamed);

        var listing = await engine.ListAsync(misnamed);
        Assert.Equal(ArchiveFormat.Zip, listing.Format);
        Assert.Equal(4, listing.Entries.Count);
    }

    /// <summary>INT-008: each conflict policy does what it says, and none of them silently guesses.</summary>
    [Theory]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.Rename)]
    public async Task Conflict_policies_behave_as_documented(ConflictPolicy policy)
    {
        using var sandbox = new TempDirectory("INT-008");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "from archive");

        await using var engine = new EngineFixture(policy);
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var destination = sandbox.Combine("dest");
        var existing = Path.Combine(destination, "source", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "already here");

        var outcome = await engine.ExtractAsync(
            sandbox.Combine("out.zip"),
            destination,
            b => b.With(o => o with { ConflictPolicy = policy }));

        switch (policy)
        {
            case ConflictPolicy.Skip:
                Assert.Equal(1, outcome.EntriesSkipped);
                Assert.Equal("already here", await File.ReadAllTextAsync(existing));
                break;
            case ConflictPolicy.Overwrite:
                Assert.Equal(1, outcome.EntriesWritten);
                Assert.Equal("from archive", await File.ReadAllTextAsync(existing));
                break;
            case ConflictPolicy.Rename:
                Assert.Equal(1, outcome.EntriesWritten);
                Assert.Equal("already here", await File.ReadAllTextAsync(existing));
                Assert.Contains(
                    Directory.EnumerateFiles(Path.GetDirectoryName(existing)!),
                    p => !string.Equals(p, existing, StringComparison.Ordinal));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
        }
    }

    /// <summary>INT-009: Fail is a real stop, not a warning.</summary>
    [Fact]
    public async Task The_fail_conflict_policy_stops_the_operation()
    {
        using var sandbox = new TempDirectory("INT-009");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "from archive");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var existing = Path.Combine(sandbox.Combine("dest"), "source", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "already here");

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(sandbox.Combine("out.zip"), sandbox.Combine("dest")));

        Assert.Equal(ArchiveErrorCode.Cancelled, error.Error.Code);
        Assert.Equal("already here", await File.ReadAllTextAsync(existing));
    }

    /// <summary>INT-010: extracting a selection extracts only that selection.</summary>
    [Fact]
    public async Task A_selection_extracts_only_the_selected_entries()
    {
        using var sandbox = new TempDirectory("INT-010");
        var source = BuildSourceTree(sandbox);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var listing = await engine.ListAsync(sandbox.Combine("out.zip"));
        var chosen = listing.Entries
            .Where(e => e.Path is not null && e.RawPath.EndsWith("a.txt", StringComparison.Ordinal))
            .Select(e => e.Path!.Value)
            .ToList();
        Assert.Single(chosen);

        var outcome = await engine.ExtractAsync(
            sandbox.Combine("out.zip"),
            sandbox.Combine("dest"),
            b => b.With(o => o with { SelectedEntries = chosen }));

        Assert.Equal(1, outcome.EntriesWritten);
        Assert.Single(sandbox.Snapshot("dest"), p => p.EndsWith(".txt", StringComparison.Ordinal));
    }

    /// <summary>INT-011: flattening drops the structure but keeps the containment guarantee.</summary>
    [Fact]
    public async Task Flattened_extraction_puts_everything_in_the_destination_root()
    {
        using var sandbox = new TempDirectory("INT-011");
        var source = BuildSourceTree(sandbox);

        await using var engine = new EngineFixture(ConflictPolicy.Rename);
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        var outcome = await engine.ExtractAsync(
            sandbox.Combine("out.zip"),
            sandbox.Combine("dest"),
            b => b.With(o => o with
            {
                PreserveDirectoryStructure = false,
                ConflictPolicy = ConflictPolicy.Rename,
            }));

        Assert.Equal(4, outcome.EntriesWritten);
        Assert.All(sandbox.Snapshot("dest"), p => Assert.DoesNotContain("/", p));
    }

    /// <summary>INT-012: progress never goes backwards and ends at the byte count actually written.</summary>
    [Fact]
    public async Task Progress_is_monotonic_and_ends_at_the_real_total()
    {
        using var sandbox = new TempDirectory("INT-012");
        var source = BuildSourceTree(sandbox);

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        // Reported inline, not through Progress<T>. Progress<T> posts each report to the thread pool,
        // so callbacks arrive concurrently and out of order - which makes List.Add a data race and
        // makes "the last snapshot" meaningless. This test asserts on the sequence the handler
        // produced, so it has to observe that sequence rather than a reshuffled delivery of it.
        var snapshots = new List<OperationProgress>();
        var outcome = await engine.ExtractAsync(
            sandbox.Combine("out.zip"),
            sandbox.Combine("dest"),
            progress: new SynchronousProgress<OperationProgress>(snapshots.Add));

        Assert.NotEmpty(snapshots);
        for (var i = 1; i < snapshots.Count; i++)
        {
            Assert.True(
                snapshots[i].BytesDone >= snapshots[i - 1].BytesDone,
                $"bytes went backwards at snapshot {i}.");
            Assert.True(snapshots[i].ItemsDone >= snapshots[i - 1].ItemsDone);
        }

        Assert.Equal(OperationState.Completed, snapshots[^1].State);
        Assert.Equal(outcome.BytesWritten.Bytes, snapshots[^1].BytesDone);
    }

    /// <summary>
    /// INT-013: cancellation must not leave a half-written file under the name the user will trust.
    /// The temp-then-publish sequence is what makes this true.
    /// </summary>
    [Fact]
    public async Task Cancellation_leaves_no_published_partial_file()
    {
        using var sandbox = new TempDirectory("INT-013");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(source);
        for (var i = 0; i < 12; i++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(source, $"file{i:D2}.bin"),
                HostileArchiveTests.CompressibleBytes(512 * 1024));
        }

        await using var engine = new EngineFixture();
        await engine.CreateAsync(sandbox.Combine("out.zip"), [source]);

        using var cancellation = new CancellationTokenSource();
        var seen = 0;

        // Inline for the same reason as INT-012, plus one specific to cancellation: a report posted to
        // the thread pool might not run until after the extraction has already finished, so the
        // cancellation would never land and the test would assert on an operation that completed.
        var progress = new SynchronousProgress<OperationProgress>(_ =>
        {
            if (++seen >= 3)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.ExtractAsync(
            sandbox.Combine("out.zip"),
            sandbox.Combine("dest"),
            progress: progress,
            cancellationToken: cancellation.Token));

        // Whatever was published is complete; nothing is truncated, and no temp file is left behind
        // under a name that looks like real content.
        foreach (var file in sandbox.Snapshot("dest"))
        {
            Assert.False(
                file.Contains(".noba-", StringComparison.Ordinal),
                $"a staging file survived cancellation: {file}");
        }
    }

    /// <summary>INT-014: sources are deleted only after the new archive has been re-read and verified.</summary>
    [Fact]
    public async Task Sources_are_deleted_only_after_verification()
    {
        using var sandbox = new TempDirectory("INT-014");
        var source = BuildSourceTree(sandbox);

        await using var engine = new EngineFixture();
        var options = CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Balanced)
            with { DeleteSourcesAfterVerifiedSuccess = true };

        var created = await engine.CreateAsync(sandbox.Combine("out.zip"), [source], options);

        Assert.True(created.SourcesDeleted);
        Assert.NotNull(created.Verification);
        Assert.Equal(ArchiveTestStatus.Valid, created.Verification!.Status);
        Assert.Empty(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories));

        // And the archive that replaced them actually contains everything.
        var listing = await engine.ListAsync(sandbox.Combine("out.zip"));
        Assert.Equal(4, listing.Entries.Count);
    }

    /// <summary>
    /// INT-015: a create that fails must leave an existing archive untouched rather than truncating it.
    /// </summary>
    [Fact]
    public async Task A_failed_create_does_not_destroy_the_existing_archive()
    {
        using var sandbox = new TempDirectory("INT-015");
        var source = BuildSourceTree(sandbox);
        var target = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(target, [source]);
        var originalBytes = await File.ReadAllBytesAsync(target);

        // A source that does not exist fails partway through, after the builder has been opened.
        await Assert.ThrowsAsync<ArchiveOperationException>(async () => await engine.CreateAsync(
            target,
            [source, sandbox.Combine("does-not-exist")],
            overwrite: true));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(target));
    }

    /// <summary>INT-016: creating over an existing archive without --overwrite is refused, not silent.</summary>
    [Fact]
    public async Task Creating_over_an_existing_archive_requires_overwrite()
    {
        using var sandbox = new TempDirectory("INT-016");
        var source = BuildSourceTree(sandbox);
        var target = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(target, [source]);

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(target, [source]));
        Assert.Equal(ArchiveErrorCode.IoFailure, error.Error.Code);
    }

    private static string BuildSourceTree(TempDirectory sandbox)
    {
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));

        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(source, "b.dat"), "bravo bravo bravo");
        File.WriteAllText(Path.Combine(source, "nested", "c.txt"), "charlie");
        File.WriteAllBytes(
            Path.Combine(source, "nested", "deeper", "d.bin"),
            HostileArchiveTests.CompressibleBytes(70_000));

        return source;
    }

    private static void AssertTreesMatch(string expectedRoot, string actualRoot)
    {
        var expected = Directory
            .EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(expectedRoot, p).Replace('\\', '/'), StringComparer.Ordinal);
        var actual = Directory
            .EnumerateFiles(actualRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(actualRoot, p).Replace('\\', '/'), StringComparer.Ordinal);

        Assert.Equal(
            string.Join(",", expected.Keys.Order(StringComparer.Ordinal)),
            string.Join(",", actual.Keys.Order(StringComparer.Ordinal)));

        foreach (var (relative, expectedPath) in expected)
        {
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expectedPath))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual[relative]))));
        }
    }
}
