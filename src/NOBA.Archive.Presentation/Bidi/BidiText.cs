using System.Globalization;

namespace NOBA.Archive.Presentation.Bidi;

/// <summary>
/// Directional isolation for strings the user must be able to read exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a security control, not typography.</b> An LTR path dropped into an RTL paragraph is
/// rearranged by the bidirectional algorithm: <c>docs\..\..\evil.txt</c> can render with its segments
/// in a different order from the one the path actually has. The dialog asking "extract this entry?"
/// then shows a path that is not the path being judged, which is the failure mode a traversal refusal
/// exists to prevent — now dressed as a rendering detail.
/// </para>
/// <para>
/// The fix is the Unicode isolate pair: FIRST STRONG ISOLATE opens a run whose direction is decided by
/// its own first strong character and cannot leak into or out of the surrounding paragraph, and POP
/// DIRECTIONAL ISOLATE closes it. Everything the user is asked to trust — entry paths, archive names,
/// destination folders — goes through here.
/// </para>
/// </remarks>
public static class BidiText
{
    /// <summary>U+2068 FIRST STRONG ISOLATE.</summary>
    public const char FirstStrongIsolate = '⁨';

    /// <summary>U+2069 POP DIRECTIONAL ISOLATE.</summary>
    public const char PopDirectionalIsolate = '⁩';

    /// <summary>U+2066 LEFT-TO-RIGHT ISOLATE.</summary>
    public const char LeftToRightIsolate = '⁦';

    /// <summary>
    /// Wraps a value so the surrounding paragraph cannot reorder it, and it cannot reorder the
    /// paragraph.
    /// </summary>
    public static string Isolate(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $"{FirstStrongIsolate}{value}{PopDirectionalIsolate}";

    /// <summary>
    /// Isolates a value that must read left to right whatever it contains.
    /// </summary>
    /// <remarks>
    /// For a path or a hash, "decide from the first strong character" is not good enough: a Windows
    /// path beginning with an Arabic folder name is still a path, and its separators still run left to
    /// right. Pinning the direction is the difference between a readable path and a plausible one.
    /// </remarks>
    public static string IsolateAsLtr(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $"{LeftToRightIsolate}{value}{PopDirectionalIsolate}";

    /// <summary>True when the string carries at least one strong right-to-left character.</summary>
    public static bool ContainsRightToLeft(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            // Arabic, Hebrew, Syriac, Thaana and the Arabic supplements. Enough to answer "does this
            // string need isolating", which is the only question asked here.
            var v = rune.Value;
            if (v is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats a byte count for display: always an LTR run, always the UI culture's digits.
    /// </summary>
    /// <remarks>
    /// Numbers are LTR runs regardless of UI language, and digit shape is a separate preference from
    /// direction. Formatting with the culture and isolating as LTR keeps those two decisions apart
    /// instead of tangling them.
    /// </remarks>
    public static string FormatCount(long value, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return IsolateAsLtr(value.ToString("N0", culture));
    }
}
