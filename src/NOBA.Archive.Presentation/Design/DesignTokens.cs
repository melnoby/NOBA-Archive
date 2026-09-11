using System.Globalization;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Design;

public enum ThemeMode
{
    Dark = 0,
    Light,
}

/// <summary>One colour, kept as a value rather than a platform brush.</summary>
public readonly record struct TokenColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public static TokenColor Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var text = hex.TrimStart('#');

        return text.Length switch
        {
            6 => new TokenColor(Byte(text, 0), Byte(text, 2), Byte(text, 4)),
            8 => new TokenColor(Byte(text, 2), Byte(text, 4), Byte(text, 6), Byte(text, 0)),
            _ => throw new FormatException($"'{hex}' is not #RRGGBB or #AARRGGBB."),
        };

        static byte Byte(string s, int index) =>
            byte.Parse(s.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Flattens this colour onto an opaque one.
    /// </summary>
    /// <remarks>
    /// Several semantic tokens are defined as a percentage over their surface — a selected row is the
    /// brand blue at 24%, hover is white at 6%. Contrast is a property of what the eye receives, so it
    /// has to be measured against the composited result, not against the translucent value.
    /// </remarks>
    public TokenColor Over(TokenColor background)
    {
        var alpha = A / 255.0;
        return new TokenColor(
            (byte)Math.Round((R * alpha) + (background.R * (1 - alpha))),
            (byte)Math.Round((G * alpha) + (background.G * (1 - alpha))),
            (byte)Math.Round((B * alpha) + (background.B * (1 - alpha))));
    }

    public TokenColor WithAlpha(double fraction) =>
        new(R, G, B, (byte)Math.Clamp(Math.Round(fraction * 255), 0, 255));

    public string ToHex() =>
        A == 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{A:X2}{R:X2}{G:X2}{B:X2}");

    public override string ToString() => ToHex();
}

/// <summary>
/// The design system, in code, as the single definition both surfaces are generated from.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/DESIGN_TOKENS.md</c> §9 asked for exactly this: the WinUI resource dictionary generated from
/// the tokens rather than hand-authored, and a contrast test over every shipped pair. A specification
/// that is retyped into XAML is a specification that drifts on the first hurried change, and drifts
/// silently, because nothing compares the two.
/// </para>
/// <para>
/// Only semantic tokens are exposed. The primitives are private, which is the enforcement of "no
/// component names a raw colour" — a component physically cannot reach one from here.
/// </para>
/// </remarks>
public static class DesignTokens
{
    // ---- primitives (ADR-0011 layer 1): private, so nothing can reference one directly -------------
    private static readonly TokenColor Blue70 = TokenColor.Parse("#1B4FD8");
    private static readonly TokenColor Blue60 = TokenColor.Parse("#2563EB");
    private static readonly TokenColor Blue50 = TokenColor.Parse("#3B82F6");
    private static readonly TokenColor Blue40 = TokenColor.Parse("#60A5FA");
    private static readonly TokenColor Blue20 = TokenColor.Parse("#BFDBFE");
    private static readonly TokenColor Neutral98 = TokenColor.Parse("#F8FAFC");
    private static readonly TokenColor Neutral94 = TokenColor.Parse("#EEF2F7");
    private static readonly TokenColor Neutral86 = TokenColor.Parse("#D8DEE8");
    private static readonly TokenColor Neutral64 = TokenColor.Parse("#94A3B8");

    // Added when the contrast test was first run: the Phase 1 palette failed AA on five pairs, and
    // three of them needed a value that did not exist. neutral.54 is a border that clears 3:1 on a
    // light surface without reading as text; neutral.50 is secondary text that clears 4.5:1 on the
    // sunken surface, which neutral.44 did not (4.23:1).
    private static readonly TokenColor Neutral54 = TokenColor.Parse("#7A8AA3");
    // Darkened from #5B6B80 when the warm light surfaces landed: against the new sunken warm grey it
    // measured 4.45:1, five hundredths short of AA. The palette moved, so the pair had to be re-measured
    // rather than assumed to still hold.
    private static readonly TokenColor Neutral50 = TokenColor.Parse("#566378");
    private static readonly TokenColor Neutral44 = TokenColor.Parse("#64748B");
    private static readonly TokenColor Neutral28 = TokenColor.Parse("#334155");
    private static readonly TokenColor Neutral18 = TokenColor.Parse("#1E293B");
    private static readonly TokenColor Neutral12 = TokenColor.Parse("#141C2B");
    private static readonly TokenColor Neutral08 = TokenColor.Parse("#0B1220");
    private static readonly TokenColor Red60 = TokenColor.Parse("#DC2626");
    private static readonly TokenColor Red40 = TokenColor.Parse("#F87171");
    private static readonly TokenColor Green60 = TokenColor.Parse("#16A34A");

    // The light-theme security colours had to darken: 3.30:1 and 3.19:1 on white. State is read as
    // text - "blocked", "allowed" - so it is held to body contrast, and a refusal the user squints at
    // is a refusal that gets dismissed.
    private static readonly TokenColor Green70 = TokenColor.Parse("#15803D");
    private static readonly TokenColor Green40 = TokenColor.Parse("#4ADE80");
    private static readonly TokenColor Amber60 = TokenColor.Parse("#D97706");
    private static readonly TokenColor Amber70 = TokenColor.Parse("#B45309");
    private static readonly TokenColor Amber40 = TokenColor.Parse("#FBBF24");
    // Warm neutrals for the light theme. The cool blue-greys above are correct on a dark surface, where
    // they read as depth; on a light one they read as a screenshot of an unfinished tool. These carry a
    // little red and yellow so a full-window expanse of them looks like paper rather than like glass.
    private static readonly TokenColor Warm99 = TokenColor.Parse("#FCFBF9");
    private static readonly TokenColor Warm97 = TokenColor.Parse("#F6F4F0");
    private static readonly TokenColor Warm93 = TokenColor.Parse("#EBE8E1");
    private static readonly TokenColor Warm86 = TokenColor.Parse("#DAD5CB");

    // A muted navy rather than the saturated blue. At 66% of the window's chrome the brighter value was
    // doing the job of an alert, not of an accent.
    private static readonly TokenColor Navy70 = TokenColor.Parse("#1E3A6E");
    private static readonly TokenColor Navy60 = TokenColor.Parse("#2B4C8C");
    private static readonly TokenColor Navy20 = TokenColor.Parse("#D7E0F1");

    private static readonly TokenColor White = TokenColor.Parse("#FFFFFF");
    private static readonly TokenColor Black = TokenColor.Parse("#000000");

    /// <summary>Semantic colour tokens for a theme. The component vocabulary (ADR-0011 layer 2).</summary>
    public static IReadOnlyDictionary<string, TokenColor> Colors(ThemeMode theme) =>
        theme == ThemeMode.Dark ? DarkColors : LightColors;

    private static readonly Dictionary<string, TokenColor> DarkColors = new(StringComparer.Ordinal)
    {
        ["surface.base"] = Neutral08,
        ["surface.raised"] = Neutral12,
        ["surface.sunken"] = TokenColor.Parse("#070C16"),
        ["surface.overlay"] = Neutral18,
        ["surface.selected"] = Blue70.WithAlpha(0.24),
        ["surface.hover"] = White.WithAlpha(0.06),
        ["border.subtle"] = White.WithAlpha(0.08),
        // 34%, not the 16% the mock used: at 16% an input's own boundary sat at 1.59:1 against the
        // window, which is invisible to a large number of people and fails SC 1.4.11 outright.
        ["border.strong"] = White.WithAlpha(0.34),
        ["border.focus"] = Blue40,
        ["text.primary"] = Neutral98,
        ["text.secondary"] = Neutral64,
        ["text.disabled"] = Neutral44,
        ["text.onAccent"] = White,
        ["accent.default"] = Blue60,
        ["accent.hover"] = Blue50,
        ["accent.subtle"] = Blue70.WithAlpha(0.20),
        ["state.blocked"] = Red40,
        ["state.allowed"] = Green40,
        ["state.warning"] = Amber40,
        ["state.encrypted"] = Blue40,
    };

    private static readonly Dictionary<string, TokenColor> LightColors = new(StringComparer.Ordinal)
    {
        ["surface.base"] = Warm97,
        ["surface.raised"] = Warm99,
        ["surface.sunken"] = Warm93,
        ["surface.overlay"] = White,
        ["surface.selected"] = Navy20,
        ["surface.hover"] = Black.WithAlpha(0.04),
        ["border.subtle"] = Warm86,
        ["border.strong"] = Neutral54,
        ["border.focus"] = Navy60,
        ["text.primary"] = Neutral18,
        ["text.secondary"] = Neutral50,
        ["text.disabled"] = Neutral64,
        ["text.onAccent"] = White,
        ["accent.default"] = Navy60,
        ["accent.hover"] = Navy70,
        ["accent.subtle"] = Navy20,
        ["state.blocked"] = Red60,
        ["state.allowed"] = Green70,
        ["state.warning"] = Amber70,
        ["state.encrypted"] = Navy70,
    };

    /// <summary>The 4px spacing scale. These values exist and no others do.</summary>
    public static IReadOnlyDictionary<string, double> Spacing { get; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["space.0"] = 0,
        ["space.1"] = 4,
        ["space.2"] = 8,
        ["space.3"] = 12,
        ["space.4"] = 16,
        ["space.5"] = 24,
        ["space.6"] = 32,
        ["space.7"] = 48,
    };

    public static IReadOnlyDictionary<string, double> Radius { get; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["radius.sm"] = 4,
        ["radius.md"] = 8,
        ["radius.lg"] = 12,
        ["radius.full"] = 9999,
    };

    public static IReadOnlyDictionary<string, double> Motion { get; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["motion.instant"] = 0,
        ["motion.fast"] = 120,
        ["motion.default"] = 200,
        ["motion.slow"] = 320,
    };

    /// <summary>
    /// One step of the type ramp. Line height is a function of script, not a constant.
    /// </summary>
    public readonly record struct TypeStep(string Name, double Size, double LatinLineHeight, double ArabicLineHeight, bool SemiBold)
    {
        public double LineHeightFor(UiScript script) =>
            script == UiScript.Arabic ? ArabicLineHeight : LatinLineHeight;
    }

    public static IReadOnlyList<TypeStep> TypeRamp { get; } =
    [
        new("type.caption", 11, 16, 20, SemiBold: false),
        new("type.body", 13, 18, 22, SemiBold: false),
        new("type.bodyStrong", 13, 18, 22, SemiBold: true),
        new("type.subtitle", 16, 22, 26, SemiBold: true),
        new("type.title", 20, 28, 34, SemiBold: true),
        new("type.display", 28, 36, 44, SemiBold: true),
    ];

    /// <summary>Font stacks. Two, because almost no family serves both scripts well.</summary>
    public static string UiFontFor(UiScript script) =>
        script == UiScript.Arabic
            ? "Segoe UI, Dubai, Noto Sans Arabic"
            : "Segoe UI Variable, Segoe UI";

    /// <summary>Paths, hashes and CRC32 are monospace in both languages, and always LTR.</summary>
    public const string MonoFont = "Cascadia Mono, Consolas";

    /// <summary>
    /// Component tokens. Deliberately few, and the row height depends on the script.
    /// </summary>
    public static double FileListRowHeight(UiScript script) => script == UiScript.Arabic ? 32 : 28;

    public const double ToolbarHeight = 40;
    public const double SidebarWidth = 208;
    public const double SidebarCollapsedWidth = 48;
    public const double StatusBarHeight = 28;
}
