using System.Text;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Backends.SevenZip;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// RAR, through the whole engine rather than through the provider.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these runs the request through <see cref="EngineFixture"/> on purpose. Phase 3 shipped
/// RAR with a capability declaration and a licence test, both of which pass against a provider that
/// cannot open a single RAR file — the format is never named anywhere in them. The gap was in the
/// wiring between the handler and the backend, which is a place no unit test looks.
/// </para>
/// <para>
/// A skip here is an untested backend, never a working one.
/// </para>
/// </remarks>
public sealed class RarBackendTests
{
    private static bool RequireNativeBackend()
    {
        if (SevenZipLibraryLocator.Locate() is not null)
        {
            return true;
        }

        Assert.Skip(
            "The 7-Zip native library is not installed. Searched:" +
            Environment.NewLine + SevenZipLibraryLocator.DescribeSearch());
        return false;
    }

    /// <summary>
    /// Whether this build of the native library carries the RAR decoder.
    /// </summary>
    /// <remarks>
    /// Windows 7-Zip carries it; some Linux packages split it into a separate codec and report
    /// "unsupported method" for every entry. Listing works either way — RAR headers are parsed by the
    /// handler itself — so the listing tests are unconditional and only the decode tests consult this.
    /// </remarks>
    private static async ValueTask<bool> RequireRarDecoder(EngineFixture engine, string archive)
    {
        var result = await engine.TestAsync(archive);
        if (result.Status != ArchiveTestStatus.Unsupported)
        {
            return true;
        }

        Assert.Skip("This build of the 7-Zip library has no RAR decoder: " + result.Findings[0].Message);
        return false;
    }

    /// <summary>
    /// RAR-001: the engine lists a RAR archive.
    /// </summary>
    /// <remarks>
    /// The regression test for the wiring defect. The handler resolved the format to pick a provider
    /// and then opened with <c>OpenOptions</c> that did not carry it; the native backend, which fronts
    /// three handlers, defaulted to 7Z. 7-Zip answers S_FALSE for "not my format", the backend read
    /// that as "the header is encrypted", and a perfectly ordinary RAR came back demanding a password.
    /// Before the fix this test fails with <see cref="ArchiveErrorCode.PasswordRequired"/>.
    /// </remarks>
    [Fact]
    public async Task The_engine_lists_a_rar_archive()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-001");
        var archive = RarFixtures.WritePlainRar(sandbox.Combine("plain.rar"));

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Equal(ArchiveFormat.Rar, listing.Format);
        Assert.Equal(
            RarFixtures.PlainEntryNames.ToArray(),
            listing.Entries.Select(static e => e.DisplayPath).ToArray());
    }

    /// <summary>RAR-002: the Arabic entry name survives, independently of the machine's code page.</summary>
    [Fact]
    public async Task An_arabic_entry_name_reads_correctly()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-002");
        var archive = RarFixtures.WritePlainRar(sandbox.Combine("plain.rar"));

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Contains(listing.Entries, static e => e.DisplayPath == "docs/تقرير.txt");
    }

    /// <summary>RAR-003: extraction produces the exact bytes.</summary>
    [Fact]
    public async Task Extraction_round_trips_content_exactly()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-003");
        var archive = RarFixtures.WritePlainRar(sandbox.Combine("plain.rar"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        if (!await RequireRarDecoder(engine, archive))
        {
            return;
        }

        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.False(outcome.HadBlockedEntries);
        Assert.Equal(
            RarFixtures.ArabicEntryContent,
            await File.ReadAllTextAsync(Path.Combine(destination, "docs", "تقرير.txt"), Encoding.UTF8));
        Assert.Equal(
            "hello from rar",
            await File.ReadAllTextAsync(Path.Combine(destination, "a.txt"), Encoding.UTF8));
    }

    /// <summary>
    /// RAR-004: the compressed size is read, so the expansion-ratio guard has something to work with.
    /// </summary>
    /// <remarks>
    /// The native backend never read <c>kpidPackSize</c>, so every 7Z and RAR entry reported zero
    /// compressed bytes. <see cref="NOBA.Archive.Security.ResourceGuard"/> skips the ratio check when
    /// the compressed total is zero — a sensible rule that, combined with the omission, meant the
    /// archive-wide bomb guard silently did nothing on exactly the two formats that compress hardest.
    /// A guard that fails open leaves no trace, so this asserts the input exists rather than the
    /// behaviour that consumes it.
    /// </remarks>
    [Fact]
    public async Task Compressed_sizes_are_reported()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-004");
        var archive = RarFixtures.WritePlainRar(sandbox.Combine("plain.rar"));

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        var file = listing.Entries.Single(static e => e.DisplayPath == "a.txt");
        Assert.True(file.CompressedSize.Bytes > 0, "compressed size was not read from the archive");
        Assert.True(listing.Statistics.TotalCompressedSize.Bytes > 0, "archive-wide compressed total was zero");
    }

    /// <summary>
    /// RAR-005: a per-entry backend failure on the batch path is reported, not swallowed.
    /// </summary>
    /// <remarks>
    /// The R18 batch path learns of a failed entry through <c>SetOperationResult</c> and used only to
    /// abandon the temp file, while <c>Extract</c> itself still returned S_OK. The result was an
    /// extraction that wrote nothing and reported success — and only above one file, because that is
    /// when the batch path engages, so the single-entry path went on throwing for the same archive.
    /// Two paths that disagree about failure are worse than either.
    /// </remarks>
    [Fact]
    public async Task A_failed_entry_is_reported_on_the_batch_path()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-005");
        var archive = RarFixtures.WriteOneBadEntryRar(sandbox.Combine("bad.rar"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        if (!await RequireRarDecoder(engine, archive))
        {
            return;
        }

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, destination));

        Assert.True(
            failure.Error.TechnicalDetail.Contains("broken.txt", StringComparison.Ordinal),
            failure.Error.TechnicalDetail);

        // Whatever else happens, the entry whose bytes failed their own checksum is not on disk.
        Assert.False(File.Exists(Path.Combine(destination, "broken.txt")));
    }

    /// <summary>RAR-006: a traversal entry in a RAR is refused, exactly as in a 7z.</summary>
    [Fact]
    public async Task A_traversal_entry_is_refused()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-006");
        var archive = RarFixtures.WriteTraversalRar(sandbox.Combine("hostile.rar"));
        var destination = sandbox.Combine("dest");
        Directory.CreateDirectory(destination);

        await using var engine = new EngineFixture();

        try
        {
            var outcome = await engine.ExtractAsync(archive, destination);
            Assert.Single(outcome.Blocked);
            Assert.Equal(PathRejectionReason.TraversalSegment, outcome.Blocked[0].Reason);
        }
        catch (ArchiveOperationException)
        {
            // No RAR decoder here: the innocent entry fails on method rather than policy. The refusal
            // still has to have happened, and it is checked below in the only way that matters.
        }

        Assert.False(File.Exists(Path.Combine(sandbox.Path, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "escaped.txt")));
    }

    /// <summary>
    /// RAR-007: a file that is not an archive is named as such, not treated as encrypted.
    /// </summary>
    /// <remarks>
    /// From a real folder: a <c>.rar</c> that was raw NTFS file-table data left by disk-recovery
    /// software. The honest answer is "this was never an archive". The answer before this change was
    /// to ask the user for a password, which is both wrong and unanswerable.
    /// </remarks>
    [Fact]
    public async Task A_file_that_is_not_an_archive_is_identified_rather_than_called_encrypted()
    {
        using var sandbox = new TempDirectory("RAR-007");
        var impostor = RarFixtures.WriteNtfsRecordNamedRar(sandbox.Combine("recovered.rar"));

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ListAsync(impostor));

        Assert.NotEqual(ArchiveErrorCode.PasswordRequired, failure.Error.Code);
        Assert.True(
            failure.Error.TechnicalDetail.Contains("NTFS", StringComparison.OrdinalIgnoreCase),
            failure.Error.TechnicalDetail);
    }

    /// <summary>
    /// RAR-008: the native backend refuses to guess which of its formats it was handed.
    /// </summary>
    /// <remarks>
    /// The defect this file exists for was a silent default. Making the omission throw is what stops
    /// it returning: a caller that forgets now fails at the call rather than three layers away, in a
    /// message about a password.
    /// </remarks>
    [Fact]
    public async Task The_native_backend_refuses_to_guess_a_format()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("RAR-008");
        var archive = RarFixtures.WritePlainRar(sandbox.Combine("plain.rar"));

        using var backend = new SevenZipBackend(new NullFormatDetector(), configuredLibraryPath: null);
        await using var stream = File.OpenRead(archive);

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await backend.OpenAsync(
                ArchiveSource.FromStream(stream, "plain.rar"),
                OpenOptions.Default,
                new NoPasswordProvider(),
                CancellationToken.None));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
    }

    private sealed class NullFormatDetector : IFormatDetector
    {
        public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FormatDetection.None);

        public FormatDetection DetectFromHeader(ReadOnlySpan<byte> header, string? fileName) => FormatDetection.None;
    }

    private sealed class NoPasswordProvider : IPasswordProvider
    {
        public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Password?>(null);
    }
}
