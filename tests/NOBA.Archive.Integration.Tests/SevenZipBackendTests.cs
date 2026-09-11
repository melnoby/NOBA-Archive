using System.Text;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
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
/// The native backend (<c>7z.dll</c>) against real 7-Zip archives.
/// </summary>
/// <remarks>
/// These run wherever the native library is present and skip honestly where it is not — a skip here is
/// an untested backend, never a working one.
/// </remarks>
public sealed class SevenZipBackendTests
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

    /// <summary>SZ-001: a plain 7z lists every entry, with non-ASCII names intact.</summary>
    [Fact]
    public async Task A_plain_archive_lists_its_entries()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-001");
        var archive = SevenZipFixtures.WritePlainSevenZip(sandbox.Combine("plain.7z"));

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Equal(ArchiveFormat.SevenZip, listing.Format);

        var names = listing.Entries.Where(static e => !e.IsDirectory).Select(static e => e.DisplayPath).ToList();
        Assert.True(names.Contains("تقرير.txt"), $"Arabic name missing. Got: {string.Join(", ", names)}");
        Assert.True(names.Contains("report 🎉.txt"), $"emoji name missing. Got: {string.Join(", ", names)}");
        Assert.True(names.Contains("docs/b.txt"), $"nested name missing. Got: {string.Join(", ", names)}");
    }

    /// <summary>SZ-002: extraction produces the exact bytes, including the Arabic-named entry.</summary>
    [Fact]
    public async Task Extraction_round_trips_content_exactly()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-002");
        var archive = SevenZipFixtures.WritePlainSevenZip(sandbox.Combine("plain.7z"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.False(outcome.HadBlockedEntries);
        Assert.Equal("محتوى عربي", await File.ReadAllTextAsync(Path.Combine(destination, "تقرير.txt"), Encoding.UTF8));
        Assert.Equal("hello from 7z", await File.ReadAllTextAsync(Path.Combine(destination, "a.txt"), Encoding.UTF8));
        Assert.Equal(
            "nested content",
            await File.ReadAllTextAsync(Path.Combine(destination, "docs", "b.txt"), Encoding.UTF8));
    }

    /// <summary>
    /// SZ-003: a solid archive extracts correctly. Solid blocks decode as a unit, so per-entry
    /// extraction is the case most likely to be wrong, not the least.
    /// </summary>
    [Fact]
    public async Task A_solid_archive_extracts_every_entry()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-003");
        var archive = SevenZipFixtures.WriteSolidSevenZip(sandbox.Combine("solid.7z"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Equal(4, outcome.EntriesWritten);
        Assert.Equal("محتوى عربي", await File.ReadAllTextAsync(Path.Combine(destination, "تقرير.txt"), Encoding.UTF8));
    }

    /// <summary>SZ-004: integrity testing passes on a good archive and writes nothing.</summary>
    [Fact]
    public async Task Testing_a_good_archive_passes_and_writes_nothing()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-004");
        var archive = SevenZipFixtures.WritePlainSevenZip(sandbox.Combine("plain.7z"));

        await using var engine = new EngineFixture();
        var before = sandbox.Snapshot(".");

        var result = await engine.Get<TestArchiveHandler>().HandleAsync(
            new TestRequest { ArchivePath = archive },
            engine.Get<OperationIdFactory>().Next(OperationType.Test),
            null,
            CancellationToken.None);

        Assert.Equal(ArchiveTestStatus.Valid, result.Status);
        Assert.Empty(result.Findings);
        Assert.Equal(before, sandbox.Snapshot("."));
    }

    /// <summary>
    /// SZ-005: the ADR-0005 invariant, measured across the interop boundary.
    /// </summary>
    /// <remarks>
    /// This is the one that matters most in the whole file. 7-Zip's <c>GetStream</c> callback has no
    /// path in its signature, so the native backend structurally cannot choose a destination — but
    /// "structurally cannot" is a claim about a C++ library nobody here compiled, and it deserves a
    /// measurement rather than a reading of the header. Extract a hostile archive whose every entry is
    /// refused, then assert the destination is still empty: 7-Zip decoded nothing to disk of its own
    /// accord, because it was never given anywhere to put it.
    /// </remarks>
    [Fact]
    public async Task The_native_backend_writes_nothing_when_every_entry_is_refused()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-005");
        var archive = SevenZipFixtures.WriteTraversalSevenZip(sandbox.Combine("hostile.7z"));
        var destination = sandbox.Combine("dest");
        Directory.CreateDirectory(destination);

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.True(outcome.HadBlockedEntries);
        Assert.Equal(0, outcome.EntriesWritten);
        Assert.Empty(Directory.GetFileSystemEntries(destination, "*", SearchOption.AllDirectories));
    }

    /// <summary>SZ-006: a relative traversal in a 7z is refused, and named as such.</summary>
    [Fact]
    public async Task A_traversal_entry_is_refused()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-006");
        var archive = SevenZipFixtures.WriteTraversalSevenZip(sandbox.Combine("hostile.7z"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Single(outcome.Blocked);
        var rejection = outcome.Blocked[0];
        Assert.Equal(PathRejectionReason.TraversalSegment, rejection.Reason);
        Assert.True(rejection.RawPath.Contains("..", StringComparison.Ordinal), rejection.RawPath);
    }

    /// <summary>SZ-007: an absolute path in a 7z is refused too — a different reason, same outcome.</summary>
    [Fact]
    public async Task An_absolute_path_entry_is_refused()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-007");
        var archive = SevenZipFixtures.WriteAbsolutePathSevenZip(sandbox.Combine("hostile.7z"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.Single(outcome.Blocked);
        Assert.Equal(PathRejectionReason.RootedPath, outcome.Blocked[0].Reason);
        Assert.Equal(0, outcome.EntriesWritten);
    }

    /// <summary>
    /// SZ-008: hostile entries are visible in the <em>listing</em>, before the user commits to
    /// anything. A refusal that only happens at extraction time is a refusal the UI cannot warn about.
    /// </summary>
    [Fact]
    public async Task Hostile_entries_are_flagged_in_the_listing()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-008");
        var archive = SevenZipFixtures.WriteTraversalSevenZip(sandbox.Combine("hostile.7z"));

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Single(listing.Entries);
        var entry = listing.Entries[0];
        Assert.NotNull(entry.Rejection);
        Assert.Null(entry.Path);
    }

    /// <summary>
    /// SZ-009: an archive with encrypted headers cannot even be listed without a password, and says so
    /// specifically rather than reporting corruption.
    /// </summary>
    [Fact]
    public async Task An_archive_with_encrypted_headers_asks_for_a_password()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-009");
        var archive = SevenZipFixtures.WriteEncryptedHeadersSevenZip(sandbox.Combine("enc.7z"));

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ListAsync(archive));

        Assert.Equal(ArchiveErrorCode.PasswordRequired, failure.Error.Code);
    }

    /// <summary>SZ-010: with the password, the same archive lists and extracts.</summary>
    [Fact]
    public async Task An_encrypted_archive_opens_with_the_password()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-010");
        var archive = SevenZipFixtures.WriteEncryptedHeadersSevenZip(sandbox.Combine("enc.7z"));
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture(SevenZipFixtures.EncryptedPassword);
        var listing = await engine.ListAsync(archive);
        Assert.NotEmpty(listing.Entries);

        var outcome = await engine.ExtractAsync(archive, destination);
        Assert.Equal(
            "محتوى عربي",
            await File.ReadAllTextAsync(Path.Combine(destination, "تقرير.txt"), Encoding.UTF8));
        Assert.False(outcome.HadBlockedEntries);
    }

    /// <summary>
    /// SZ-011: RAR advertises no Create capability, and never will.
    /// </summary>
    /// <remarks>
    /// A licence term is not a thing a future contributor can be relied upon to remember, so ADR-0004
    /// is pinned by a test rather than by a comment. The unRAR licence forbids using its source to
    /// build a RAR-compatible compressor; this fails the build if anyone adds the capability.
    /// </remarks>
    [Fact]
    public void Rar_never_advertises_creation()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var backend = new SevenZipBackend(new MagicByteFormatDetectorStub(), configuredLibraryPath: null);
        var capabilities = backend.GetCapabilities(ArchiveFormat.Rar);

        Assert.False(capabilities.HasAny(
            ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries |
            ArchiveCapabilities.RemoveEntries | ArchiveCapabilities.RenameEntries));
        Assert.True(capabilities.Has(ArchiveCapabilities.Extract));
    }

    /// <summary>
    /// SZ-012: with no native library, the backend degrades to an explanation rather than a mystery.
    /// </summary>
    [Fact]
    public async Task An_absent_library_reports_where_it_looked()
    {
        using var backend = new SevenZipBackend(
            new MagicByteFormatDetectorStub(),
            configuredLibraryPath: Path.Combine(Path.GetTempPath(), "definitely-not-here"));

        // Pointed at a path that does not exist, discovery may still find a real installation - that is
        // the search order working. Only the unavailable case has something to assert about.
        if (backend.IsAvailable)
        {
            return;
        }

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await backend.OpenAsync(
                ArchiveSource.FromStream(new MemoryStream()),
                OpenOptions.Default,
                new NoPasswordProvider(),
                CancellationToken.None));

        Assert.Equal(ArchiveErrorCode.UnsupportedFormat, failure.Error.Code);
        Assert.True(
            failure.Error.TechnicalDetail.Contains("Looked in", StringComparison.Ordinal),
            failure.Error.TechnicalDetail);
    }

    /// <summary>
    /// SZ-013: the batch path and the per-entry path produce identical results.
    /// </summary>
    /// <remarks>
    /// The R18 fix made the backend drive the extraction loop, which is a real change in who is in
    /// control. Speed is worth nothing if the fast path quietly differs, so this drives the same
    /// archive both ways and compares the file tree byte for byte — content included, not just names.
    /// </remarks>
    [Fact]
    public async Task Batch_and_per_entry_extraction_agree()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-013");
        var archive = SevenZipFixtures.WriteSolidSevenZip(sandbox.Combine("solid.7z"));

        await using var engine = new EngineFixture();

        var batchDestination = sandbox.Combine("batch");
        var batch = await engine.ExtractAsync(archive, batchDestination);

        var singleDestination = sandbox.Combine("single");
        var single = await engine.ExtractAsync(
            archive,
            singleDestination,
            b => b.With(o => o with { PreferSingleEntryExtraction = true }));

        Assert.Equal(single.EntriesWritten, batch.EntriesWritten);
        Assert.Equal(single.BytesWritten.Bytes, batch.BytesWritten.Bytes);
        Assert.Equal(single.Blocked.Count, batch.Blocked.Count);

        var batchFiles = Directory.GetFiles(batchDestination, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(batchDestination, f).Replace('\\', '/'))
            .OrderBy(static f => f, StringComparer.Ordinal).ToList();
        var singleFiles = Directory.GetFiles(singleDestination, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(singleDestination, f).Replace('\\', '/'))
            .OrderBy(static f => f, StringComparer.Ordinal).ToList();

        Assert.Equal(string.Join("|", singleFiles), string.Join("|", batchFiles));

        foreach (var relative in singleFiles)
        {
            var fromBatch = await File.ReadAllBytesAsync(Path.Combine(batchDestination, relative));
            var fromSingle = await File.ReadAllBytesAsync(Path.Combine(singleDestination, relative));
            Assert.Equal(fromSingle, fromBatch);
        }
    }

    /// <summary>
    /// SZ-014: refused entries are refused identically on the batch path.
    /// </summary>
    /// <remarks>
    /// This is the one that would matter if the fix were wrong. On the batch path the backend drives
    /// the loop and asks for a stream per entry; a refused entry is declined by handing back null. If
    /// that plumbing were broken, a hostile entry would be written rather than blocked — the exact
    /// failure ADR-0005 exists to prevent, reintroduced by a performance change.
    /// </remarks>
    [Fact]
    public async Task The_batch_path_still_refuses_hostile_entries()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("SZ-014");
        var archive = SevenZipFixtures.WriteTraversalSevenZip(sandbox.Combine("hostile.7z"));
        var destination = sandbox.Combine("dest");
        Directory.CreateDirectory(destination);

        await using var engine = new EngineFixture();
        var outcome = await engine.ExtractAsync(archive, destination);

        Assert.True(outcome.HadBlockedEntries);
        Assert.Equal(0, outcome.EntriesWritten);
        Assert.Empty(Directory.GetFileSystemEntries(destination, "*", SearchOption.AllDirectories));
    }

    private sealed class MagicByteFormatDetectorStub : IFormatDetector
    {
        public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FormatDetection.None);

        public FormatDetection DetectFromHeader(ReadOnlySpan<byte> header, string? fileName) => FormatDetection.None;
    }

    private sealed class NoPasswordProvider : IPasswordProvider
    {
        public ValueTask<Core.Primitives.Password?> GetPasswordAsync(
            PasswordRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Core.Primitives.Password?>(null);
    }
}
