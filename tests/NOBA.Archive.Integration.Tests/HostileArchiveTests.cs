using System.Text;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The SEC-### suite (TESTING.md §4): hostile archives, end to end, through the shipping composition.
/// </summary>
/// <remarks>
/// <para>
/// Every test here asserts two things, and the second is the important one:
/// </para>
/// <list type="number">
/// <item>the entry was refused and <em>reported</em> — silent dropping is its own defect;</item>
/// <item><b>nothing exists outside the destination folder</b>. A rejection that still wrote a file
/// somewhere would satisfy the first assertion and fail the product.</item>
/// </list>
/// <para>
/// The sandbox root contains the destination as a subfolder, so an escape by one level lands inside the
/// sandbox where a test can see it, rather than in the real filesystem where it could not.
/// </para>
/// </remarks>
public sealed class HostileArchiveTests
{
    private const string Payload = "owned";

    /// <summary>SEC-001..010: every traversal shape a real archiver has been caught by.</summary>
    [Theory]
    [InlineData("SEC-001", "../escaped.txt")]
    [InlineData("SEC-002", "../../../../escaped.txt")]
    [InlineData("SEC-003", @"..\..\escaped.txt")]
    [InlineData("SEC-004", @"C:\Windows\Temp\evil.dll")]
    [InlineData("SEC-005", "/etc/cron.d/evil")]
    [InlineData("SEC-006", @"\\attacker\share\payload.exe")]
    [InlineData("SEC-007", "readme.txt:payload.ps1")]
    [InlineData("SEC-008", "docs/../../escaped.txt")]
    [InlineData("SEC-009", "safe/./../../escaped.txt")]
    [InlineData("SEC-010", "....//....//escaped.txt")]
    public async Task A_hostile_entry_is_refused_and_writes_nothing_outside_the_destination(string id, string entryName)
    {
        using var sandbox = new TempDirectory(id);
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            (entryName, Payload),
            ("innocent.txt", "fine"));

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, sandbox.Combine("dest"));

        Assert.True(outcome.HadBlockedEntries, $"{id}: '{entryName}' was not refused.");
        Assert.Contains(outcome.Blocked, r => string.Equals(r.RawPath, entryName, StringComparison.Ordinal));

        // The benign sibling still lands: refusing one entry must not abandon the whole archive.
        Assert.Equal(1, outcome.EntriesWritten);
        AssertNothingEscaped(sandbox, id);
    }

    /// <summary>
    /// SEC-011: an entry that resolves inside the destination but only after a component that is
    /// already a symlink. Creating the parent chain in one call would follow it.
    /// </summary>
    [Fact]
    public async Task A_planted_link_in_the_destination_chain_is_refused()
    {
        using var sandbox = new TempDirectory("SEC-011");
        var destination = sandbox.Combine("dest");
        var outside = sandbox.Combine("outside");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(destination, "logs"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without Developer Mode cannot create symlinks unelevated. Skipping is honest;
            // claiming a pass we did not execute is not.
            Assert.Skip("This environment does not permit creating symbolic links.");
            return;
        }

        var archive = ArchiveFactory.WriteZip(sandbox.Combine("hostile.zip"), ("logs/pwned.txt", Payload));

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.True(outcome.HadBlockedEntries);
        Assert.Equal(PathRejectionReason.LinkRefused, outcome.Blocked[0].Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    /// <summary>SEC-012: expansion ratio far past the guard threshold, caught mid-stream.</summary>
    [Fact]
    public async Task A_compression_bomb_is_stopped_while_it_expands()
    {
        using var sandbox = new TempDirectory("SEC-012");
        var archive = ArchiveFactory.WriteRatioBomb(sandbox.Combine("bomb.zip"), 512L * 1024 * 1024);

        await using var engine = new EngineFixture();

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, sandbox.Combine("dest")));

        Assert.Equal(ArchiveErrorCode.BombSuspected, error.Error.Code);

        // The point of the guard is that it fires before the disk fills: nothing near 512 MB survived.
        var written = Directory.Exists(sandbox.Combine("dest"))
            ? Directory.EnumerateFiles(sandbox.Combine("dest"), "*", SearchOption.AllDirectories)
                .Sum(p => new FileInfo(p).Length)
            : 0;
        Assert.True(written < 64L * 1024 * 1024, $"{written} bytes reached the disk before the guard tripped.");
    }

    /// <summary>
    /// SEC-013: the declared uncompressed size is attacker-controlled, so the guard must count real
    /// bytes. A bomb that declares itself tiny is still a bomb.
    /// </summary>
    [Fact]
    public async Task The_guard_counts_produced_bytes_not_declared_ones()
    {
        using var sandbox = new TempDirectory("SEC-013");
        var archive = ArchiveFactory.WriteRatioBomb(sandbox.Combine("bomb.zip"), 256L * 1024 * 1024);

        await using var engine = new EngineFixture();

        // Listing reports what the archive claims; extraction must not trust it.
        var listing = await engine.ListAsync(archive);
        var declared = listing.Entries[0].UncompressedSize;

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, sandbox.Combine("dest")));

        Assert.Equal(ArchiveErrorCode.BombSuspected, error.Error.Code);
        Assert.True(declared.Bytes > 0);
    }

    /// <summary>
    /// SEC-021: many entries, each individually innocuous, that together expand far past the archive
    /// total. The per-entry ratio guard cannot see this shape at all — only the running total can.
    /// </summary>
    [Fact]
    public async Task An_archive_that_is_only_a_bomb_in_aggregate_is_stopped()
    {
        using var sandbox = new TempDirectory("SEC-021");

        // 40 entries x 8 MB of zeroes: each expands ~1000:1 at most, but the archive as a whole
        // expands far past the total-ratio threshold long before the last entry.
        var entries = Enumerable.Range(0, 40)
            .Select(i => ($"part{i:D2}.bin", new byte[8 * 1024 * 1024]))
            .ToArray();
        var archive = ArchiveFactory.WriteZipBytes(sandbox.Combine("aggregate.zip"), entries);

        await using var engine = new EngineFixture();

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, sandbox.Combine("dest")));

        Assert.Equal(ArchiveErrorCode.BombSuspected, error.Error.Code);
    }

    /// <summary>SEC-014: names that render deceptively are a display problem, not a write permission.</summary>
    [Theory]
    [InlineData("invoice\u202Efdp.exe")]
    [InlineData("report\u200B.pdf")]
    [InlineData("\u0640\u0640\u0640.txt")]
    public async Task A_deceptive_but_contained_name_lands_inside_the_destination(string entryName)
    {
        using var sandbox = new TempDirectory("SEC-014");
        var archive = ArchiveFactory.WriteZip(sandbox.Combine("odd.zip"), (entryName, Payload));

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, sandbox.Combine("dest"));

        // Refusing these outright would break legitimate archives; containing them is the guarantee.
        Assert.Equal(1, outcome.EntriesWritten + outcome.Blocked.Count);
        AssertNothingEscaped(sandbox, "SEC-014");
    }

    /// <summary>SEC-015: degenerate names must not become a write to the destination root itself.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../..")]
    public async Task A_degenerate_name_never_becomes_the_destination_root(string entryName)
    {
        using var sandbox = new TempDirectory("SEC-015");
        var destination = sandbox.Combine("dest");
        var archive = ArchiveFactory.WriteZip(sandbox.Combine("odd.zip"), (entryName, Payload));

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Equal(0, outcome.EntriesWritten);
        Assert.False(File.Exists(destination), "the destination root was replaced by a file.");
        AssertNothingEscaped(sandbox, "SEC-015");
    }

    /// <summary>
    /// SEC-016: the managed backend cannot decrypt. It must say so, per entry, rather than write
    /// ciphertext to disk under the entry's name — which is what "extract without a password" would
    /// otherwise silently produce.
    /// </summary>
    [Fact]
    public async Task An_encrypted_entry_is_reported_and_never_written_as_ciphertext()
    {
        using var sandbox = new TempDirectory("SEC-016");
        var archive = sandbox.Combine("encrypted.zip");
        EncryptedZipFactory.WriteZipCryptoArchive(archive, "secret.txt", "hunter2", Payload);

        await using var engine = new EngineFixture();

        var listing = await engine.ListAsync(archive);
        Assert.True(listing.Entries.Single().IsEncrypted, "the encryption probe missed the entry.");

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, sandbox.Combine("dest")));
        Assert.Equal(ArchiveErrorCode.UnsupportedMethod, error.Error.Code);

        var leaked = Path.Combine(sandbox.Combine("dest"), "secret.txt");
        Assert.False(File.Exists(leaked), "ciphertext was published under the entry's real name.");
    }

    /// <summary>SEC-017: corruption is a diagnosis, not a crash — and not a partially published file.</summary>
    [Fact]
    public async Task A_corrupted_payload_fails_the_test_command_and_publishes_nothing()
    {
        using var sandbox = new TempDirectory("SEC-017");
        var archive = ArchiveFactory.WriteZipBytes(
            sandbox.Combine("corrupt.zip"),
            ("data.bin", CompressibleBytes(512 * 1024)));
        ArchiveFactory.CorruptPayload(archive);

        await using var engine = new EngineFixture();

        var result = await engine.TestAsync(archive);
        Assert.Equal(ArchiveTestStatus.Corrupted, result.Status);
        Assert.False(result.IsUsable);

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, sandbox.Combine("dest")));
        Assert.Equal(ArchiveErrorCode.CorruptData, error.Error.Code);

        Assert.Empty(sandbox.Snapshot("dest"));
    }

    /// <summary>SEC-018: a truncated archive has no central directory at all.</summary>
    [Fact]
    public async Task A_truncated_archive_is_rejected_as_corrupt()
    {
        using var sandbox = new TempDirectory("SEC-018");
        var archive = ArchiveFactory.WriteZipBytes(
            sandbox.Combine("truncated.zip"),
            ("data.bin", CompressibleBytes(256 * 1024)));
        ArchiveFactory.Truncate(archive, 0.4);

        await using var engine = new EngineFixture();

        var error = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ListAsync(archive));
        Assert.True(
            error.Error.Code is ArchiveErrorCode.CorruptData or ArchiveErrorCode.UnsupportedFormat,
            $"a truncated archive produced {error.Error.Code}.");
    }

    /// <summary>
    /// SEC-019: a hostile archive with nothing but hostile entries must still complete rather than
    /// throw, so the caller gets the full report of what was attempted.
    /// </summary>
    [Fact]
    public async Task An_entirely_hostile_archive_completes_with_a_full_report()
    {
        using var sandbox = new TempDirectory("SEC-019");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("all-hostile.zip"),
            ("../a.txt", Payload),
            (@"..\b.txt", Payload),
            ("/etc/c.txt", Payload));

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, sandbox.Combine("dest"));

        Assert.Equal(0, outcome.EntriesWritten);
        Assert.Equal(3, outcome.Blocked.Count);
        Assert.All(outcome.Blocked, r => Assert.False(string.IsNullOrWhiteSpace(r.Detail)));
        AssertNothingEscaped(sandbox, "SEC-019");
    }

    /// <summary>
    /// SEC-020: the archive's own listing already carries the rejection, so a UI can grey the entry out
    /// before the user ever presses Extract.
    /// </summary>
    [Fact]
    public async Task Listing_reports_hostile_entries_without_extracting_anything()
    {
        using var sandbox = new TempDirectory("SEC-020");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            ("../escaped.txt", Payload),
            ("ok.txt", "fine"));

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        var hostile = listing.Entries.Single(e => e.RawPath.Contains("escaped", StringComparison.Ordinal));
        Assert.True(hostile.IsRejected);
        Assert.Null(hostile.Path);
        Assert.Equal(hostile.RawPath, hostile.DisplayPath);

        var safe = listing.Entries.Single(e => e.RawPath == "ok.txt");
        Assert.False(safe.IsRejected);
        Assert.NotNull(safe.Path);
    }

    /// <summary>
    /// The assertion that actually protects the user: after a hostile extraction, the only thing that
    /// changed anywhere in the sandbox is content under the destination folder.
    /// </summary>
    private static void AssertNothingEscaped(TempDirectory sandbox, string id)
    {
        var stray = Directory
            .EnumerateFileSystemEntries(sandbox.Path, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(sandbox.Combine("dest"), StringComparison.Ordinal))
            .Where(p => !p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(stray.Count == 0, $"{id}: files appeared outside the destination: {string.Join(", ", stray)}");
    }

    internal static byte[] CompressibleBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    /// <summary>
    /// SEC-022: a file whose extension lies. Found by running the alpha over a real Downloads folder,
    /// not by imagining a case: a password-protected Office document had been saved as <c>3.zip</c>.
    /// </summary>
    /// <remarks>
    /// The bytes are a Compound File and contain no ZIP structure whatsoever — no local header, no
    /// end-of-central-directory record. Before this, detection found no signature, fell back to the
    /// <c>.zip</c> extension, and the ZIP backend reported that the central directory was missing.
    /// That reads as "your archive is corrupt", so the honest answer — "this was never a ZIP" — was
    /// available in the first eight bytes and thrown away.
    /// </remarks>
    [Fact]
    public async Task A_compound_file_named_zip_is_identified_not_called_corrupt()
    {
        using var sandbox = new TempDirectory("SEC-022");
        var path = sandbox.Combine("3.zip");

        // The CFB header, then the two stream names that mark MS-OFFCRYPTO encryption.
        var bytes = new byte[1024];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        Encoding.Unicode.GetBytes("DataSpaces").CopyTo(bytes, 512);
        Encoding.Unicode.GetBytes("EncryptedPackage").CopyTo(bytes, 700);
        await File.WriteAllBytesAsync(path, bytes);

        await using var engine = new EngineFixture();

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ListAsync(path));

        Assert.Equal(ArchiveErrorCode.UnsupportedFormat, failure.Error.Code);
        Assert.Contains("Compound File", failure.Error.TechnicalDetail);
        Assert.DoesNotContain("central directory", failure.Error.TechnicalDetail.ToLowerInvariant());
    }

    /// <summary>SEC-023: and a genuinely truncated ZIP still reports as damaged, not as "not an archive".</summary>
    [Fact]
    public async Task A_truncated_zip_is_still_reported_as_damaged()
    {
        using var sandbox = new TempDirectory("SEC-023");
        var good = ArchiveFactory.WriteZip(sandbox.Combine("good.zip"), ("a.txt", "content"));
        var bytes = await File.ReadAllBytesAsync(good);

        var truncated = sandbox.Combine("truncated.zip");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 2)]);

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ListAsync(truncated));

        // It really does start with PK\x03\x04, so this one is damaged - a different problem, and it
        // must not be swept into the "not an archive" bucket by the new signature check.
        Assert.NotEqual(ArchiveErrorCode.UnsupportedFormat, failure.Error.Code);
    }
}
