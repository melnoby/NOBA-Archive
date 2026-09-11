using System.Globalization;
using System.Text;

namespace NOBA.Archive.Formats.Managed;

/// <summary>How the encoding for an archive's entry names was arrived at.</summary>
public enum ZipNameEncodingSource
{
    /// <summary>Every entry set bit 11, so the archive says UTF-8 itself.</summary>
    DeclaredUtf8 = 0,

    /// <summary>No entry needed decoding — the names are pure ASCII, and every code page agrees.</summary>
    Ascii,

    /// <summary>Bit 11 was absent, but the bytes are valid UTF-8. Common: many tools write UTF-8 and forget the flag.</summary>
    SniffedUtf8,

    /// <summary>The caller named a code page explicitly ("Open as…").</summary>
    CallerSpecified,

    /// <summary>Guessed from the operating system's ANSI code page, which is what other archivers do.</summary>
    SystemDefault,
}

/// <summary>The chosen encoding plus the reasoning, so a UI can say what it did and offer to change it.</summary>
public sealed record ZipNameEncodingChoice(Encoding Encoding, ZipNameEncodingSource Source, int CodePage)
{
    /// <summary>
    /// True when the choice was inferred rather than stated by the archive. A UI should offer
    /// "reinterpret names as…" in that case, because a guess is all anyone can do here.
    /// </summary>
    public bool IsGuess => Source is ZipNameEncodingSource.SystemDefault;
}

/// <summary>
/// Decides which encoding a ZIP's entry names are in.
/// </summary>
/// <remarks>
/// <para>
/// The ZIP format never recorded this properly. Bit 11 of the general-purpose flag means "UTF-8"; it
/// was added late, and without it the specification says CP437 while real tools wrote whatever ANSI
/// code page the machine was set to. So an archive of Arabic filenames made on an Arabic Windows box
/// carries CP1256 bytes and says nothing about it.
/// </para>
/// <para>
/// Getting this wrong is not cosmetic in an archive manager. The decoded name becomes a filename on
/// disk, so a mis-decode writes <c>?????.txt</c> into the user's folder — and for a product whose
/// audience writes Arabic, that is the difference between working and not.
/// </para>
/// <para>
/// The order below is "believe the archive, then believe the bytes, then guess like everyone else" —
/// and the guess is reported as a guess rather than presented as fact.
/// </para>
/// </remarks>
public static class ZipNameEncoding
{
    private const int Utf8CodePage = 65001;

    /// <summary>CP437 is what the specification names, and the least-bad answer when nothing else is known.</summary>
    public const int SpecificationCodePage = 437;

    public static ZipNameEncodingChoice Choose(IReadOnlyList<ZipRawEntry> entries, int? requestedCodePage)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // An explicit request wins outright: this is the "Open as…" escape hatch, and second-guessing
        // a user who just told us the answer would make that command useless.
        if (requestedCodePage is { } requested)
        {
            return LegacyCodePages.TryGetEncoding(requested, out var chosen)
                ? new ZipNameEncodingChoice(chosen, ZipNameEncodingSource.CallerSpecified, requested)
                : new ZipNameEncodingChoice(utf8, ZipNameEncodingSource.CallerSpecified, Utf8CodePage);
        }

        var undeclared = entries.Where(static e => !e.DeclaresUtf8).ToList();
        if (undeclared.Count == 0)
        {
            return new ZipNameEncodingChoice(utf8, ZipNameEncodingSource.DeclaredUtf8, Utf8CodePage);
        }

        // Pure ASCII needs no decision at all, and this is the overwhelmingly common case. Saying
        // "system default" here would make every ordinary archive look like a guess.
        if (undeclared.All(static e => Array.TrueForAll(e.RawName, static b => b < 0x80)))
        {
            return new ZipNameEncodingChoice(utf8, ZipNameEncodingSource.Ascii, Utf8CodePage);
        }

        // Strict UTF-8: a legacy byte sequence almost never validates as UTF-8, so a clean parse is
        // strong evidence rather than a coin flip.
        if (undeclared.All(static e => IsValidUtf8(e.RawName)))
        {
            return new ZipNameEncodingChoice(utf8, ZipNameEncodingSource.SniffedUtf8, Utf8CodePage);
        }

        var systemCodePage = SystemAnsiCodePage();
        return LegacyCodePages.TryGetEncoding(systemCodePage, out var fallback)
            ? new ZipNameEncodingChoice(fallback, ZipNameEncodingSource.SystemDefault, systemCodePage)
            : new ZipNameEncodingChoice(Encoding.Latin1, ZipNameEncodingSource.SystemDefault, 28591);
    }

    /// <summary>
    /// The machine's ANSI code page, which is the same guess 7-Zip and WinRAR make. On a system that
    /// reports UTF-8 (modern Windows with the UTF-8 beta option, and every Linux) that is no help for
    /// bytes we already know are not UTF-8, so the specification's CP437 is used instead.
    /// </summary>
    private static int SystemAnsiCodePage()
    {
        var ansi = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return ansi is 0 or Utf8CodePage ? SpecificationCodePage : ansi;
    }

    internal static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            // Throwing decoder, not the replacement-character one: the whole point is to find out
            // whether these bytes are UTF-8, and a silent replacement would answer "yes" every time.
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
