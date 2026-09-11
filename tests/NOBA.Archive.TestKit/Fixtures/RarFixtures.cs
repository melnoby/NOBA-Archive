namespace NOBA.Archive.TestKit.Fixtures;

/// <summary>
/// RAR archives, checked in as base64.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist at all.</b> RAR shipped in Phase 3 with a capability declaration, a licence test
/// and no archive: 388 tests passed without one RAR ever being opened. The first real <c>.rar</c> a
/// user pointed at the CLI came back "encrypted, password required" — the engine never told the native
/// backend which of its three handlers to use, the backend guessed 7Z, and 7-Zip's "not my format"
/// answer was being read as "the header is encrypted". Every test that could have caught it happened to
/// use the format the wrong guess named.
/// </para>
/// <para>
/// <b>Why they are hand-built rather than produced by a tool.</b> There is no RAR writer to produce
/// them with, and there never will be one here: the unRAR licence forbids building a RAR-compatible
/// compressor (ADR-0004). These are assembled byte by byte from the RAR4 block layout — marker, main
/// header, one block per entry, terminator — with the stored method (0x30) so no compression code is
/// involved on either side. Each one is verified to list and extract identically under 7-Zip.
/// </para>
/// <para>
/// Names carry the RAR4 Unicode flag (0x0200) with UTF-8 in the name field, so the Arabic entry decodes
/// the same on a machine whose ANSI code page is 1252 as on one running 1256. A fixture whose meaning
/// depends on the tester's locale proves nothing about the reader.
/// </para>
/// </remarks>
public static class RarFixtures
{
    /// <summary>Entry names in <see cref="WritePlainRar"/>, in archive order.</summary>
    public static IReadOnlyList<string> PlainEntryNames { get; } =
        ["docs", "docs/تقرير.txt", "a.txt"];

    /// <summary>Contents of the Arabic-named entry in <see cref="WritePlainRar"/>.</summary>
    public const string ArabicEntryContent = "محتوى عربي";

    private const string PlainRarBase64 =
        "UmFyIRoHAM+QcwAADQAAAAAAAADwynTggiQAAAAAAAAAAAACAAAAAKNa+ToUMAQAEAAAAGRvY3NKDHQAgjMAEwAAABMAAAAC" +
        "GsO7M6Na+ToUMBMAIAAAAGRvY3Nc2KrZgtix2YrYsS50eHTZhdit2KrZiNmJINi52LHYqNmKML90AIIlAA4AAAAOAAAAAgZG" +
        "sWijWvk6FDAFACAAAABhLnR4dGhlbGxvIGZyb20gcmFyxD17AEAHAA==";

    /// <summary>A well-formed RAR4: one directory, one Arabic-named file, one ASCII file.</summary>
    public static string WritePlainRar(string path) => WriteFixture(path, PlainRarBase64);

    private const string OneBadEntryRarBase64 =
        "UmFyIRoHAM+QcwAADQAAAAAAAAA6+3QAgigAFAAAABQAAAACT8FUPaNa+ToUMAgAIAAAAGdvb2QudHh0dGhpcyBlbnRyeSBp" +
        "cyBpbnRhY3RaxXQAgioAKQAAACkAAAAC776t3qNa+ToUMAoAIAAAAGJyb2tlbi50eHR0aGVzZSBieXRlcyBkbyBub3QgbWF0" +
        "Y2ggdGhlIHJlY29yZGVkIENSQ8Q9ewBABwA=";

    /// <summary>
    /// Two files, the second carrying a CRC that does not match its bytes.
    /// </summary>
    /// <remarks>
    /// Two entries on purpose: the batch extraction path only engages above one file, and that is the
    /// path that used to report a per-entry backend failure by silently abandoning the file and
    /// returning success.
    /// </remarks>
    public static string WriteOneBadEntryRar(string path) => WriteFixture(path, OneBadEntryRarBase64);

    private const string TraversalRarBase64 =
        "UmFyIRoHAM+QcwAADQAAAAAAAAA37HQAgjEAFwAAABcAAAACUVIvmKNa+ToUMBEAIAAAAC4uXC4uXGVzY2FwZWQudHh0c2hv" +
        "dWxkIG5ldmVyIGJlIHdyaXR0ZW4WInQAgiwAEAAAABAAAAACFUGHjaNa+ToUMAwAIAAAAGlubm9jZW50LnR4dG9yZGluYXJ5" +
        "IGNvbnRlbnTEPXsAQAcA";

    /// <summary>An entry named <c>..\..\escaped.txt</c>, beside an ordinary one.</summary>
    public static string WriteTraversalRar(string path) => WriteFixture(path, TraversalRarBase64);

    /// <summary>
    /// 1 KiB of raw NTFS master-file-table data — the header a recovery tool leaves behind when it
    /// restores an entry's metadata instead of its contents.
    /// </summary>
    /// <remarks>
    /// Found in a real folder of RAR archives, named <c>.rar</c>, opening in nothing. Its value as a
    /// fixture is that it is emphatically not an archive while claiming to be one, and the first
    /// answer NOBA gave was to ask for a password.
    /// </remarks>
    public static string WriteNtfsRecordNamedRar(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var record = new byte[1024];
        // "FILE", update-sequence offset 0x0030, update-sequence count 3.
        ReadOnlySpan<byte> header = [0x46, 0x49, 0x4C, 0x45, 0x30, 0x00, 0x03, 0x00];
        header.CopyTo(record);
        record[0x14] = 0x38;  // offset to first attribute
        record[0x16] = 0x01;  // flags: record in use
        record[0x18] = 0xA0;  // used size, 0x1A0
        record[0x19] = 0x01;
        record[0x1D] = 0x04;  // allocated size, 0x400

        File.WriteAllBytes(path, record);
        return path;
    }

    private static string WriteFixture(string path, string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }
}
