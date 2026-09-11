using System.Globalization;

namespace NOBA.Archive.Presentation.Bidi;

public enum UiScript
{
    Latin = 0,
    Arabic,
}

public enum FlowDirection
{
    LeftToRight = 0,
    RightToLeft,
}

/// <summary>
/// The UI language, and everything that follows from it.
/// </summary>
/// <remarks>
/// Arabic is not a localisation pass bolted on later: script drives the font stack, the line height,
/// the row height and the flow direction, and all four are decided here rather than in each screen. A
/// screen that decides its own direction is a screen that will disagree with the next one.
/// </remarks>
public sealed record UiLanguage(string Tag, string NativeName, UiScript Script, FlowDirection Flow)
{
    public static UiLanguage English { get; } = new("en", "English", UiScript.Latin, FlowDirection.LeftToRight);

    public static UiLanguage Arabic { get; } = new("ar", "العربية", UiScript.Arabic, FlowDirection.RightToLeft);

    public static IReadOnlyList<UiLanguage> Supported { get; } = [English, Arabic];

    public CultureInfo Culture => CultureInfo.GetCultureInfo(Tag);

    public bool IsRightToLeft => Flow == FlowDirection.RightToLeft;

    /// <summary>Picks the closest supported language for a system culture, defaulting to English.</summary>
    public static UiLanguage ForCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        return culture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? Arabic
            : English;
    }
}
