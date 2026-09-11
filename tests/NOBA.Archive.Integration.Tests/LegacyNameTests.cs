using System.Text;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Formats.Managed;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// INT-020: entry names in legacy code pages.
/// </summary>
/// <remarks>
/// <para>
/// ZIP never recorded the encoding of entry names properly. Bit 11 means "UTF-8" and arrived late;
/// without it the specification says CP437 while real tools wrote the machine's ANSI code page. An
/// archive of Arabic filenames from a 2009 Windows box carries CP1256 bytes and says nothing about it.
/// </para>
/// <para>
/// This matters more here than in most software: the decoded name becomes a filename on disk. Getting
/// it wrong writes <c>?????.txt</c> into the user's folder — and for this product's audience that is
/// the difference between a working archiver and a broken one.
/// </para>
/// </remarks>
public sealed class LegacyNameTests
{
    /// <summary>The name a 2009 Arabic Windows machine would have written: CP1256, no UTF-8 flag.</summary>
    private static byte[] ArabicCp1256 => [0xCA, 0xDE, 0xD1, 0xED, 0xD1, 0x2E, 0x74, 0x78, 0x74];

    private const string ArabicName = "تقرير.txt";

    /// <summary>INT-020a: told the code page, the name comes back exactly right.</summary>
    [Fact]
    public async Task An_explicit_code_page_decodes_a_legacy_arabic_name()
    {
        using var sandbox = new TempDirectory("INT-020a");
        var archive = ArchiveFactory.WriteLegacyNamedZip(
            sandbox.Combine("legacy.zip"), ArabicCp1256, "محتوى");

        await using var engine = new EngineFixture();
        var listing = await engine.Get<ListArchiveHandler>().HandleAsync(
            new ListRequest { ArchivePath = archive, LegacyNameCodePage = 1256 },
            CancellationToken.None);

        Assert.Equal(ArabicName, listing.Entries.Single().DisplayPath);
        Assert.NotNull(listing.NameEncoding);
        Assert.False(listing.NameEncoding!.IsGuess, "an explicitly requested code page is not a guess.");
    }

    /// <summary>
    /// INT-020b: and the extraction agrees with the listing. Showing one name and writing another
    /// would be worse than either mistake alone.
    /// </summary>
    [Fact]
    public async Task Extraction_uses_the_same_encoding_as_the_listing()
    {
        using var sandbox = new TempDirectory("INT-020b");
        var archive = ArchiveFactory.WriteLegacyNamedZip(
            sandbox.Combine("legacy.zip"), ArabicCp1256, "محتوى عربي");

        await using var engine = new EngineFixture();
        var destination = sandbox.Combine("dest");

        var outcome = await engine.Get<ExtractArchiveHandler>().HandleAsync(
            new ExtractRequest
            {
                ArchivePath = archive,
                LegacyNameCodePage = 1256,
                Options = new ExtractionOptions
                {
                    DestinationRoot = destination,
                    ConflictPolicy = ConflictPolicy.Fail,
                    PropagateMarkOfTheWeb = false,
                },
            },
            engine.Get<OperationIdFactory>().Next(OperationType.Extract),
            null,
            null,
            CancellationToken.None);

        Assert.Equal(1, outcome.EntriesWritten);
        Assert.True(
            File.Exists(Path.Combine(destination, ArabicName)),
            $"expected '{ArabicName}' on disk, found: {string.Join(", ", sandbox.Snapshot("dest"))}");
        Assert.Equal("محتوى عربي", await File.ReadAllTextAsync(Path.Combine(destination, ArabicName), Encoding.UTF8));
    }

    /// <summary>
    /// INT-020c: many tools write UTF-8 and forget the flag. Sniffing catches that without anyone
    /// having to know, because a legacy byte sequence practically never validates as UTF-8.
    /// </summary>
    [Fact]
    public async Task Undeclared_utf8_names_are_detected()
    {
        using var sandbox = new TempDirectory("INT-020c");
        var archive = ArchiveFactory.WriteLegacyNamedZip(
            sandbox.Combine("sneaky.zip"), Encoding.UTF8.GetBytes(ArabicName), "محتوى");

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Equal(ArabicName, listing.Entries.Single().DisplayPath);
        Assert.False(listing.NameEncoding!.IsGuess, "detected UTF-8 is evidence, not a guess.");
    }

    /// <summary>INT-020d: an ordinary ASCII archive must not be reported as a guess.</summary>
    [Fact]
    public async Task An_ascii_archive_is_not_reported_as_a_guess()
    {
        using var sandbox = new TempDirectory("INT-020d");
        var archive = ArchiveFactory.WriteLegacyNamedZip(
            sandbox.Combine("plain.zip"), Encoding.ASCII.GetBytes("readme.txt"), "hello");

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Equal("readme.txt", listing.Entries.Single().DisplayPath);
        Assert.False(listing.NameEncoding!.IsGuess);
    }

    /// <summary>INT-020e: an archive that declares UTF-8 is believed, and says so.</summary>
    [Fact]
    public async Task A_declared_utf8_archive_is_believed()
    {
        using var sandbox = new TempDirectory("INT-020e");
        var archive = ArchiveFactory.WriteLegacyNamedZip(
            sandbox.Combine("modern.zip"), Encoding.UTF8.GetBytes(ArabicName), "محتوى", declareUtf8: true);

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.Equal(ArabicName, listing.Entries.Single().DisplayPath);
        Assert.Equal(65001, listing.NameEncoding!.CodePage);
        Assert.False(listing.NameEncoding.IsGuess);
    }

    /// <summary>
    /// INT-020f: undecodable legacy bytes are still reported — as a guess, with a code page the user
    /// can override. Silently producing replacement characters would be the worst outcome.
    /// </summary>
    [Fact]
    public async Task An_unguessable_name_is_reported_as_a_guess()
    {
        using var sandbox = new TempDirectory("INT-020f");
        var archive = ArchiveFactory.WriteLegacyNamedZip(
            sandbox.Combine("legacy.zip"), ArabicCp1256, "محتوى");

        await using var engine = new EngineFixture();
        var listing = await engine.ListAsync(archive);

        Assert.NotNull(listing.NameEncoding);
        Assert.True(listing.NameEncoding!.IsGuess, "a fallback to the machine's code page is a guess.");
        Assert.DoesNotContain("�", listing.Entries.Single().DisplayPath);
    }

    /// <summary>
    /// The tables themselves, checked against known byte/character pairs rather than against the
    /// generator that produced them.
    /// </summary>
    [Theory]
    [InlineData(1256, 0xCA, 'ت')] // Arabic teh
    [InlineData(1256, 0xDE, 'ق')] // Arabic qaf
    [InlineData(1252, 0xE9, 'é')] // e-acute
    [InlineData(1251, 0xC0, 'А')] // Cyrillic A
    [InlineData(437, 0xE1, 'ß')]  // sharp s
    [InlineData(1255, 0xE0, 'א')] // Hebrew alef
    public void Built_in_code_pages_decode_known_bytes(int codePage, int rawByte, char expected)
    {
        Assert.True(LegacyCodePages.TryGetEncoding(codePage, out var encoding));
        Assert.Equal(expected.ToString(), encoding.GetString([(byte)rawByte]));
    }

    /// <summary>Every built-in table must agree with ASCII below 0x80, which the decoder relies on.</summary>
    [Fact]
    public void Built_in_code_pages_are_ascii_compatible()
    {
        var ascii = new byte[0x80];
        for (var i = 0; i < 0x80; i++)
        {
            ascii[i] = (byte)i;
        }

        var expected = Encoding.ASCII.GetString(ascii);

        foreach (var codePage in LegacyCodePages.BuiltIn)
        {
            Assert.True(LegacyCodePages.TryGetEncoding(codePage, out var encoding));
            Assert.Equal(expected, encoding.GetString(ascii));
        }
    }
}
