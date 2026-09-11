namespace NOBA.Archive.Presentation.Design;

/// <summary>What a foreground/background pair is used for, and therefore what ratio it must meet.</summary>
public enum ContrastRole
{
    /// <summary>Body text, file names, metadata. WCAG AA: 4.5:1.</summary>
    BodyText = 0,

    /// <summary>Large or semibold text, and UI boundaries such as borders and focus rings. AA: 3:1.</summary>
    LargeTextOrBoundary,
}

public readonly record struct ContrastPair(string Foreground, string Background, ContrastRole Role)
{
    public double Required => Role == ContrastRole.BodyText ? 4.5 : 3.0;
}

/// <summary>
/// WCAG 2.x relative luminance and contrast ratio.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the palette's accessibility is a test result rather than an intention. §3 of
/// <c>docs/DESIGN_TOKENS.md</c> claims AA for every semantic pair; until something computed it, that
/// was a sentence in a document.
/// </para>
/// <para>
/// Translucent tokens are composited onto their surface first (see <see cref="TokenColor.Over"/>).
/// Measuring a 24%-alpha selection colour as if it were opaque answers a question nobody asked.
/// </para>
/// </remarks>
public static class Contrast
{
    public static double RelativeLuminance(TokenColor color)
    {
        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    public static double Ratio(TokenColor foreground, TokenColor background)
    {
        var a = RelativeLuminance(foreground);
        var b = RelativeLuminance(background);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Every pair the product actually renders, with the role that fixes its threshold.
    /// </summary>
    /// <remarks>
    /// Written out rather than generated from the cross product of all tokens: most combinations are
    /// never drawn, and a test that fails on a pair nobody uses teaches the reader to ignore it.
    /// </remarks>
    public static IReadOnlyList<ContrastPair> ShippedPairs { get; } =
    [
        new("text.primary", "surface.base", ContrastRole.BodyText),
        new("text.primary", "surface.raised", ContrastRole.BodyText),
        new("text.primary", "surface.sunken", ContrastRole.BodyText),
        new("text.primary", "surface.overlay", ContrastRole.BodyText),
        new("text.primary", "surface.selected", ContrastRole.BodyText),
        new("text.secondary", "surface.base", ContrastRole.BodyText),
        new("text.secondary", "surface.raised", ContrastRole.BodyText),
        new("text.secondary", "surface.sunken", ContrastRole.BodyText),
        new("text.onAccent", "accent.default", ContrastRole.BodyText),

        // Security state is read as text, so it is held to body contrast rather than to the boundary
        // threshold. A refusal the user has to squint at is a refusal that gets dismissed.
        new("state.blocked", "surface.raised", ContrastRole.BodyText),
        new("state.allowed", "surface.raised", ContrastRole.BodyText),
        new("state.warning", "surface.raised", ContrastRole.BodyText),
        new("state.encrypted", "surface.raised", ContrastRole.BodyText),

        new("border.strong", "surface.base", ContrastRole.LargeTextOrBoundary),
        new("border.strong", "surface.raised", ContrastRole.LargeTextOrBoundary),
        new("border.focus", "surface.base", ContrastRole.LargeTextOrBoundary),
        new("border.focus", "surface.raised", ContrastRole.LargeTextOrBoundary),
        new("border.focus", "surface.sunken", ContrastRole.LargeTextOrBoundary),
        new("accent.default", "surface.base", ContrastRole.LargeTextOrBoundary),
        new("accent.default", "surface.raised", ContrastRole.LargeTextOrBoundary),
    ];

    /// <summary>Measures one pair in one theme, compositing translucency onto the theme's base.</summary>
    public static double Measure(ContrastPair pair, ThemeMode theme)
    {
        var colors = DesignTokens.Colors(theme);
        var baseSurface = colors["surface.base"];

        var background = colors[pair.Background].Over(baseSurface);
        var foreground = colors[pair.Foreground].Over(background);

        return Ratio(foreground, background);
    }
}
