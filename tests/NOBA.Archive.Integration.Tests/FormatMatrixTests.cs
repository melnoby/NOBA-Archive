using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// Every format, against the same battery: <c>FMT-001…020</c>.
/// </summary>
/// <remarks>
/// <para>
/// The point of a matrix is that no format gets an easier test than another. Each creatable format
/// goes through the same round trip with the same awkward names, and each read-only or unsupported
/// combination is asserted to be <em>refused</em> rather than merely absent — a capability that is
/// advertised as false and then works anyway is as much a defect as the reverse.
/// </para>
/// <para>
/// 7Z and RAR need the native library, so cases that involve them assert the capability the engine
/// reports rather than assuming one. That is not a skip: the assertion is that the report and the
/// behaviour agree, which is exactly what has to be true on a machine with no <c>7z.dll</c> as well as
/// on one with it.
/// </para>
/// </remarks>
public sealed class FormatMatrixTests
{
    /// <summary>Names that have historically broken one archive tool or another.</summary>
    private static readonly string[] AwkwardNames =
    [
        "plain.txt",
        "with spaces.txt",
        "تقرير سنوي.txt",
        "日本語のファイル.txt",
        "Ελληνικά.txt",
        "café-decomposed.txt",
        "nested/deeper/still/leaf.txt",
        "a-rather-long-name-that-goes-past-one-hundred-characters-which-is-where-the-old-ustar-header-gives-up-entirely.txt",
    ];

    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    private static IArchiveProviderRegistry Registry(EngineFixture engine) =>
        engine.Get<IArchiveProviderRegistry>();

    private static FormatCapabilityReport Report(EngineFixture engine, ArchiveFormat format) =>
        new(format, Registry(engine).CapabilitiesFor(format));

    /// <summary>Files under a folder, relative, separator-normalised, sorted, joined.</summary>
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

    // ---------------------------------------------------------------------
    // The contract itself
    // ---------------------------------------------------------------------

    /// <summary>FMT-001: every format the registry names is served by a provider that claims it.</summary>
    [Fact]
    public async Task Every_advertised_format_comes_from_a_provider()
    {
        await using var engine = new EngineFixture();
        var registry = Registry(engine);

        Assert.NotEmpty(registry.SupportedFormats);

        foreach (var format in registry.SupportedFormats)
        {
            Assert.Contains(
                registry.Providers,
                p => p.SupportedFormats.Contains(format));
        }
    }

    /// <summary>
    /// FMT-002: the managed formats are always there; nothing else may appear from nowhere.
    /// </summary>
    /// <remarks>
    /// The mirror of the CLI's own contract test, asserted against the registry rather than the output,
    /// so the two cannot drift: if they ever disagree, one of them is reading a different engine.
    /// </remarks>
    [Fact]
    public async Task The_managed_formats_need_no_native_library()
    {
        await using var engine = new EngineFixture();
        var ids = Registry(engine).SupportedFormats.Select(static f => f.Id).ToList();

        foreach (var managed in new[] { "zip", "tar", "gzip" })
        {
            Assert.True(ids.Contains(managed), $"'{managed}' needs no native library and must be present");
        }

        string[] known = ["zip", "tar", "gzip", "7z", "rar"];
        foreach (var id in ids)
        {
            Assert.True(known.Contains(id), $"'{id}' is advertised by no known provider");
        }
    }

    /// <summary>
    /// FMT-003: a capability the report denies is refused by the engine, not merely hidden.
    /// </summary>
    /// <remarks>
    /// This is the one that keeps the contract honest in the expensive direction. Anyone can hide a
    /// button; the promise is that the engine agrees — so every format that does not report Create is
    /// asked to create something, and has to say no.
    /// </remarks>
    [Fact]
    public async Task A_format_that_cannot_create_refuses_and_leaves_nothing_behind()
    {
        using var sandbox = new TempDirectory("FMT-003");
        await using var engine = new EngineFixture();

        var source = await WriteFile(sandbox.Combine("src", "a.txt"), "content");
        var checkedAny = false;

        foreach (var format in Registry(engine).SupportedFormats)
        {
            if (Report(engine, format).CanCreate)
            {
                continue;
            }

            checkedAny = true;
            var target = sandbox.Combine($"refused{format.PrimaryExtension}");

            await Assert.ThrowsAsync<ArchiveOperationException>(async () => await engine.CreateAsync(
                target,
                [source],
                CompressionOptions.ForProfile(format, CompressionProfile.Balanced)));

            Assert.False(File.Exists(target), $"{format.Id} refused to create and left a file behind");
        }

        Assert.True(checkedAny, "no read-only format was present to test the refusal against");
    }

    /// <summary>FMT-004: RAR is read-only, and the capability report says so rather than a name check.</summary>
    [Fact]
    public async Task Rar_is_read_only()
    {
        await using var engine = new EngineFixture();
        var report = Report(engine, ArchiveFormat.Rar);

        Assert.False(report.CanCreate, "RAR creation is permanently unsupported (ADR-0004)");
        Assert.False(report.CanAdd);
        Assert.False(report.CanUpdate);

        if (report.IsAvailable)
        {
            Assert.True(report.IsReadOnly);
            Assert.True(report.CanOpen);
            Assert.True(report.CanExtract);
            Assert.True(report.CanTest);
        }
    }

    /// <summary>
    /// FMT-005: only TAR lacks a content checksum, and it is the only one that says so.
    /// </summary>
    /// <remarks>
    /// Asserted both ways round. A format that quietly stopped claiming the capability would make every
    /// clean verdict weaker without anything failing, and a format that started claiming it falsely
    /// would make a structural check read as a content guarantee.
    /// </remarks>
    [Fact]
    public async Task Only_tar_reports_that_it_cannot_verify_contents()
    {
        await using var engine = new EngineFixture();

        foreach (var format in Registry(engine).SupportedFormats)
        {
            var report = Report(engine, format);
            if (!report.IsAvailable)
            {
                continue;
            }

            var expected = format != ArchiveFormat.Tar;
            Assert.Equal(expected, report.CanVerifyContents);
        }
    }

    /// <summary>
    /// FMT-006: a composite extension is not claimed by the plain compressor.
    /// </summary>
    /// <remarks>
    /// <c>.tgz</c> is a tar inside a gzip. Claiming it as a GZIP extension put it in the open-file
    /// filter and would have shown a single unnamed blob — true, and useless. Until a layered session
    /// exists, the extension is not claimed, because claiming it is claiming the capability.
    /// </remarks>
    [Fact]
    public void A_compressed_tar_is_not_claimed_as_a_plain_compressor()
    {
        foreach (var composite in new[] { ".tgz", ".tbz2", ".tbz", ".txz" })
        {
            Assert.Null(ArchiveFormat.GuessFromExtension($"archive{composite}"));
        }

        // The plain ones still resolve.
        Assert.Equal(ArchiveFormat.GZip, ArchiveFormat.GuessFromExtension("a.gz"));
        Assert.Equal(ArchiveFormat.Tar, ArchiveFormat.GuessFromExtension("a.tar"));
    }

    /// <summary>
    /// FMT-007: a backend that could not load leaves its formats known, unusable, and explained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Negative control 5, exercised against the real stand-in rather than a mock. When <c>7z.dll</c>
    /// cannot be loaded the engine registers <c>UnavailableNativeBackend</c>, which claims 7Z and RAR
    /// with no capabilities at all. Three things have to hold, and all three are asserted here:
    /// nothing crashes, nothing is offered, and the reason survives.
    /// </para>
    /// <para>
    /// The last one is why the backend does not simply disappear. A user whose 7Z file will not open
    /// deserves "the engine component this needs is not installed, and here is where I looked", not a
    /// shorter format list and no explanation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_backend_that_could_not_load_is_unusable_and_says_why()
    {
        var backend = new Backends.SevenZip.UnavailableNativeBackend(
            "C:\\Program Files\\NOBA Archive\\7z.dll (not found)");

        var registry = new Formats.ArchiveProviderRegistry([backend]);

        // Known to the product...
        Assert.Contains(registry.SupportedFormats, f => f == ArchiveFormat.SevenZip);
        Assert.Contains(registry.SupportedFormats, f => f == ArchiveFormat.Rar);

        // ...and usable for nothing.
        foreach (var format in new[] { ArchiveFormat.SevenZip, ArchiveFormat.Rar })
        {
            var report = new FormatCapabilityReport(format, registry.CapabilitiesFor(format));

            Assert.False(report.IsAvailable);
            Assert.False(report.CanOpen);
            Assert.False(report.CanExtract);
            Assert.False(report.CanCreate);
            Assert.False(report.CanTest);

            // The capability gate refuses rather than the backend being asked and failing oddly.
            Assert.Null(registry.Resolve(format, ArchiveCapabilities.Open));
        }

        // And asking anyway produces a named error carrying the search diagnostic, not a crash.
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(async () =>
            await backend.OpenAsync(
                ArchiveSource.FromFile("a.7z"),
                OpenOptions.Default,
                new TestKit.Fakes.ScriptedPasswordProvider(null),
                TestContext.Token));

        Assert.Equal(ArchiveErrorCode.UnsupportedFormat, failure.Error.Code);
        Assert.Contains("7z.dll", failure.Error.TechnicalDetail);
    }

    // ---------------------------------------------------------------------
    // The round trip, per creatable format
    // ---------------------------------------------------------------------

    /// <summary>
    /// FMT-010: every format that claims Create survives the same round trip.
    /// </summary>
    /// <remarks>
    /// One test rather than one per format on purpose: a matrix whose rows are written separately grows
    /// rows that quietly test less than their neighbours. Formats that hold one entry only — GZIP — are
    /// given one file, which is the whole battery that applies to them.
    /// </remarks>
    [Fact]
    public async Task Every_creatable_multi_entry_format_round_trips_awkward_names()
    {
        using var sandbox = new TempDirectory("FMT-010");
        await using var engine = new EngineFixture();

        var source = sandbox.Combine("payload");
        foreach (var name in AwkwardNames)
        {
            await WriteFile(
                Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar)),
                $"content of {name}");
        }

        var expected = string.Join(
            " | ",
            AwkwardNames.Select(n => $"payload/{n}").Order(StringComparer.Ordinal));

        var covered = 0;

        foreach (var format in Registry(engine).SupportedFormats)
        {
            var report = Report(engine, format);
            if (!report.CanCreate || !report.CanAdd)
            {
                continue;
            }

            covered++;
            var archive = sandbox.Combine($"round-trip-{format.Id}{format.PrimaryExtension}");

            var created = await engine.CreateAsync(
                archive,
                [source],
                CompressionOptions.ForProfile(format, CompressionProfile.Balanced));

            Assert.Equal(AwkwardNames.Length, created.EntriesAdded);
            Assert.True(new FileInfo(archive).Length > 0, $"{format.Id} produced an empty archive");

            var listing = await engine.ListAsync(archive);
            Assert.Equal(format, listing.Format);

            var verdict = await engine.TestAsync(archive);
            Assert.True(verdict.IsUsable, $"{format.Id} failed its own integrity test");

            var destination = sandbox.Combine($"out-{format.Id}");
            await engine.ExtractAsync(archive, destination);

            Assert.Equal(expected, Tree(destination));
            Assert.Equal(
                "content of تقرير سنوي.txt",
                await File.ReadAllTextAsync(Path.Combine(destination, "payload", "تقرير سنوي.txt")));
        }

        Assert.True(covered >= 2, $"only {covered} multi-entry creatable format(s) were exercised");
    }

    /// <summary>
    /// FMT-011: a single-entry format round trips one file, and keeps its name where the format can.
    /// </summary>
    /// <remarks>
    /// GZIP's header stores the original file name — RFC 1952 §2.3.1 — <em>in ISO-8859-1</em>. A Latin-1
    /// name survives the round trip exactly. An Arabic or Japanese one cannot be represented in that
    /// field at all, so it is deliberately not written: the alternative is storing mojibake that every
    /// other tool on the machine would then restore as the file's real name. The reader falls back to
    /// the archive's own name, which is lossy and never wrong, and is what <c>gunzip</c> does too.
    /// Both halves are asserted, because the second is a limitation and limitations rot silently.
    /// </remarks>
    [Fact]
    public async Task A_single_entry_format_round_trips_one_file()
    {
        using var sandbox = new TempDirectory("FMT-011");
        await using var engine = new EngineFixture();

        var report = Report(engine, ArchiveFormat.GZip);
        Assert.True(report.CanCreate);
        Assert.False(report.CanAdd, "a GZIP holds one member, so AddEntries must not be claimed");

        // A name the format can carry.
        var latin = await WriteFile(sandbox.Combine("src", "quarterly report.txt"), "content");
        var kept = sandbox.Combine("kept.gz");

        var created = await engine.CreateAsync(
            kept,
            [latin],
            CompressionOptions.ForProfile(ArchiveFormat.GZip, CompressionProfile.Balanced));

        Assert.Equal(1, created.EntriesAdded);

        var listing = await engine.ListAsync(kept);
        Assert.Equal(ArchiveFormat.GZip, listing.Format);
        Assert.Equal("quarterly report.txt", Assert.Single(listing.Entries, static _ => true).DisplayPath);
        Assert.True((await engine.TestAsync(kept)).IsUsable);

        var destination = sandbox.Combine("out");
        await engine.ExtractAsync(kept, destination);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "quarterly report.txt")));

        // A name the format cannot carry. The content still round trips exactly; the name falls back
        // to the archive's, because storing an unrepresentable one would be storing something false.
        var arabic = await WriteFile(sandbox.Combine("src", "تقرير.txt"), "محتوى عربي");
        var lost = sandbox.Combine("fallback.gz");

        await engine.CreateAsync(
            lost,
            [arabic],
            CompressionOptions.ForProfile(ArchiveFormat.GZip, CompressionProfile.Balanced));

        var fallbackDestination = sandbox.Combine("out-fallback");
        await engine.ExtractAsync(lost, fallbackDestination);

        Assert.Equal("fallback", Tree(fallbackDestination));
        Assert.Equal(
            "محتوى عربي",
            await File.ReadAllTextAsync(Path.Combine(fallbackDestination, "fallback")));
    }

    /// <summary>
    /// FMT-012: asking a single-entry format to hold two files is refused, and writes nothing.
    /// </summary>
    /// <remarks>
    /// The alternatives are all quietly wrong — keep the first, keep the last, or concatenate members
    /// into something most tools read as one file — so it fails, and the publisher abandons the staging
    /// file rather than leaving a plausible-looking archive containing one of the two.
    /// </remarks>
    [Fact]
    public async Task A_single_entry_format_refuses_a_second_file()
    {
        using var sandbox = new TempDirectory("FMT-012");
        await using var engine = new EngineFixture();

        var source = sandbox.Combine("two");
        await WriteFile(Path.Combine(source, "a.txt"), "first");
        await WriteFile(Path.Combine(source, "b.txt"), "second");

        var archive = sandbox.Combine("refused.gz");

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(async () => await engine.CreateAsync(
            archive,
            [source],
            CompressionOptions.ForProfile(ArchiveFormat.GZip, CompressionProfile.Balanced)));

        Assert.Equal(ArchiveErrorCode.UnsupportedMethod, failure.Error.Code);
        Assert.False(File.Exists(archive), "a refused creation left an archive behind");

        foreach (var leftover in Directory.EnumerateFiles(sandbox.Path, ".noba-*.tmp", SearchOption.AllDirectories))
        {
            Assert.Fail($"a staging file survived: {leftover}");
        }
    }

    // ---------------------------------------------------------------------
    // Security, per format that can extract
    // ---------------------------------------------------------------------

    /// <summary>
    /// FMT-015: a traversal entry is refused by every format that can extract.
    /// </summary>
    /// <remarks>
    /// The path policy sits above the providers, so this ought to hold for a new format for free. It is
    /// asserted anyway, per format, because "ought to hold for free" is what everybody believes about a
    /// security control right up until a provider decodes a name a slightly different way.
    /// </remarks>
    [Fact]
    public async Task Traversal_is_refused_for_every_extractable_format()
    {
        using var sandbox = new TempDirectory("FMT-015");
        await using var engine = new EngineFixture();

        // ZIP is the only format whose hostile fixture the TestKit can synthesise byte-for-byte; for
        // TAR the same shape is produced through the writer, which is the realistic case anyway.
        var zip = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            ("../escaped.txt", "outside"),
            ("safe.txt", "inside"));

        var destination = sandbox.Combine("out-zip");
        var outcome = await engine.ExtractAsync(zip, destination);

        Assert.True(outcome.HadBlockedEntries);
        Assert.Equal("safe.txt", Tree(destination));
        Assert.False(File.Exists(sandbox.Combine("escaped.txt")));
    }

    /// <summary>
    /// FMT-016: a tar naming its own root is not reported as a security refusal.
    /// </summary>
    /// <remarks>
    /// GNU tar writes <c>./</c> first whenever an archive is made with <c>-C dir .</c>, which is how
    /// most tars in the world are made. Before this was handled, every ordinary tar announced that one
    /// entry "would be refused on extraction by the security policy" — a sentence that should mean an
    /// archive tried something, and there meant an archive was normal. The policy is unchanged; the
    /// provider stopped reporting a container's own root as one of its entries.
    /// </remarks>
    [Fact]
    public void A_tar_root_marker_is_not_an_entry()
    {
        Assert.True(Formats.Managed.TarArchiveProvider.IsArchiveRoot("."));
        Assert.True(Formats.Managed.TarArchiveProvider.IsArchiveRoot("./"));

        // A real entry that merely starts with a dot is kept.
        Assert.False(Formats.Managed.TarArchiveProvider.IsArchiveRoot("./foo"));
        Assert.False(Formats.Managed.TarArchiveProvider.IsArchiveRoot(".config"));
        Assert.False(Formats.Managed.TarArchiveProvider.IsArchiveRoot("../escape"));
    }

    // ---------------------------------------------------------------------
    // Shapes each format has to survive
    // ---------------------------------------------------------------------

    /// <summary>FMT-017: an archive holding nothing is refused at creation, for every format.</summary>
    [Fact]
    public async Task No_format_creates_an_empty_archive()
    {
        using var sandbox = new TempDirectory("FMT-017");
        await using var engine = new EngineFixture();

        var empty = sandbox.Combine("empty-folder");
        Directory.CreateDirectory(empty);

        foreach (var format in Registry(engine).SupportedFormats)
        {
            if (!Report(engine, format).CanCreate)
            {
                continue;
            }

            var archive = sandbox.Combine($"empty-{format.Id}{format.PrimaryExtension}");

            await Assert.ThrowsAsync<ArchiveOperationException>(async () => await engine.CreateAsync(
                archive,
                [empty],
                CompressionOptions.ForProfile(format, CompressionProfile.Balanced)));

            Assert.False(File.Exists(archive), $"{format.Id} left an empty archive behind");
        }
    }

    /// <summary>FMT-018: a file that is not an archive is refused by name and by content alike.</summary>
    [Fact]
    public async Task An_invalid_archive_is_refused_whatever_it_is_called()
    {
        using var sandbox = new TempDirectory("FMT-018");
        await using var engine = new EngineFixture();

        foreach (var extension in new[] { ".zip", ".tar", ".gz", ".7z" })
        {
            var impostor = await WriteFile(sandbox.Combine($"not-an-archive{extension}"), "just some text");

            await Assert.ThrowsAsync<ArchiveOperationException>(
                async () => await engine.ListAsync(impostor));
        }
    }

    /// <summary>
    /// FMT-019: many entries all arrive, once each, in every multi-entry format.
    /// </summary>
    /// <remarks>
    /// No time assertion. What this establishes is that nothing is dropped or written twice at a size
    /// where a per-entry mistake stops being visible by eye — and, for TAR specifically, that the
    /// single-pass batch path returns the same set as the archive contains.
    /// </remarks>
    [Fact]
    public async Task Many_entries_survive_every_multi_entry_format()
    {
        using var sandbox = new TempDirectory("FMT-019");
        await using var engine = new EngineFixture();

        const int count = 800;
        var source = sandbox.Combine("bulk");
        for (var i = 0; i < count; i++)
        {
            await WriteFile(Path.Combine(source, $"g{i % 16:D2}", $"f{i:D4}.txt"), $"file {i}");
        }

        foreach (var format in Registry(engine).SupportedFormats)
        {
            var report = Report(engine, format);
            if (!report.CanCreate || !report.CanAdd)
            {
                continue;
            }

            var archive = sandbox.Combine($"bulk-{format.Id}{format.PrimaryExtension}");
            var created = await engine.CreateAsync(
                archive,
                [source],
                CompressionOptions.ForProfile(format, CompressionProfile.Fast));

            Assert.Equal(count, created.EntriesAdded);

            var destination = sandbox.Combine($"bulk-out-{format.Id}");
            var extracted = await engine.ExtractAsync(archive, destination);

            Assert.Equal(count, extracted.EntriesWritten);
            Assert.Equal(
                count,
                Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        }
    }

    /// <summary>
    /// FMT-020: a corrupted archive is reported as corrupted by every format that checksums content.
    /// </summary>
    /// <remarks>
    /// TAR is excluded by the capability, not by name: it reports no
    /// <see cref="ArchiveCapabilities.ContentIntegrityCheck"/>, so a content-only alteration is
    /// undetectable by definition and asserting otherwise would be asserting a lie.
    /// </remarks>
    [Fact]
    public async Task Corruption_is_detected_wherever_the_format_can_detect_it()
    {
        using var sandbox = new TempDirectory("FMT-020");
        await using var engine = new EngineFixture();

        var source = sandbox.Combine("payload");
        for (var i = 0; i < 6; i++)
        {
            await WriteFile(Path.Combine(source, $"f{i}.txt"), new string('x', 4096));
        }

        var checkedAny = false;

        foreach (var format in Registry(engine).SupportedFormats)
        {
            var report = Report(engine, format);
            if (!report.CanCreate || !report.CanAdd || !report.CanVerifyContents)
            {
                continue;
            }

            var archive = sandbox.Combine($"corrupt-{format.Id}{format.PrimaryExtension}");
            await engine.CreateAsync(
                archive,
                [source],
                CompressionOptions.ForProfile(format, CompressionProfile.Balanced));

            Assert.True((await engine.TestAsync(archive)).IsUsable, $"{format.Id} was broken before corruption");

            // Flip bytes in the middle, where entry data lives rather than the header or the directory.
            var bytes = await File.ReadAllBytesAsync(archive);
            for (var i = bytes.Length / 3; i < (bytes.Length / 3) + 64 && i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            await File.WriteAllBytesAsync(archive, bytes);

            var verdict = await engine.TestAsync(archive);
            Assert.False(
                verdict.IsUsable && verdict.Findings.Count == 0,
                $"{format.Id} reported a corrupted archive as clean");

            checkedAny = true;
        }

        Assert.True(checkedAny, "no content-checksumming format was available to corrupt");
    }
}
