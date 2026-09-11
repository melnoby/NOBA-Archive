using System.Globalization;
using System.Text;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Design;

/// <summary>
/// Generates the WinUI resource dictionary from <see cref="DesignTokens"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/DESIGN_TOKENS.md</c> §9.1 asked for this specifically: generate the dictionary rather than
/// hand-author it. A hand-authored one is a second copy of the palette, and the second copy is the one
/// that is wrong — nothing in a build compares a hex value in XAML to a hex value in a markdown table.
/// </para>
/// <para>
/// The output is checked in so the XAML project has a file to compile, and a test regenerates it and
/// compares. That way the check runs on Linux, where WinUI cannot even be restored, and a drifted
/// dictionary fails the build rather than shipping.
/// </para>
/// </remarks>
public static class ResourceDictionaryWriter
{
    /// <summary>Token name to XAML key: <c>surface.raised</c> becomes <c>NobaSurfaceRaised</c>.</summary>
    public static string KeyFor(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var builder = new StringBuilder("Noba");
        foreach (var part in token.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        }

        return builder.ToString();
    }

    public static string Generate()
    {
        var xaml = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        xaml.AppendLine("<!--");
        xaml.AppendLine("    GENERATED FILE - do not edit.");
        xaml.AppendLine();
        xaml.AppendLine("    Produced by NOBA.Archive.Presentation.Design.ResourceDictionaryWriter from DesignTokens.");
        xaml.AppendLine("    DES-004 regenerates this and compares, so an edit here fails the build rather than");
        xaml.AppendLine("    silently disagreeing with the design system.");
        xaml.AppendLine("-->");
        xaml.AppendLine("<ResourceDictionary");
        xaml.AppendLine("    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        xaml.AppendLine("    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
        xaml.AppendLine();
        xaml.AppendLine("    <ResourceDictionary.ThemeDictionaries>");

        foreach (var (theme, name) in new[] { (ThemeMode.Dark, "Dark"), (ThemeMode.Light, "Light") })
        {
            xaml.AppendLine(invariant, $"        <ResourceDictionary x:Key=\"{name}\">");
            foreach (var (token, color) in DesignTokens.Colors(theme).OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                var key = KeyFor(token);
                var argb = ToArgb(color);
                xaml.AppendLine(invariant, $"            <Color x:Key=\"{key}Color\">{argb}</Color>");
                xaml.AppendLine(
                    invariant,
                    $"            <SolidColorBrush x:Key=\"{key}\" Color=\"{{StaticResource {key}Color}}\" />");
            }

            xaml.AppendLine("        </ResourceDictionary>");
        }

        xaml.AppendLine("    </ResourceDictionary.ThemeDictionaries>");
        xaml.AppendLine();

        foreach (var (token, value) in DesignTokens.Spacing.OrderBy(static p => p.Value))
        {
            xaml.AppendLine(invariant, $"    <x:Double x:Key=\"{KeyFor(token)}\">{value}</x:Double>");
        }

        xaml.AppendLine();

        foreach (var (token, value) in DesignTokens.Radius.OrderBy(static p => p.Value))
        {
            xaml.AppendLine(invariant, $"    <CornerRadius x:Key=\"{KeyFor(token)}\">{value}</CornerRadius>");
        }

        xaml.AppendLine();

        // Milliseconds as x:Double, not <Duration>. Duration is a struct whose XAML object-element
        // form is inconsistently supported, and nothing consumes these yet - an unverifiable
        // dependency for no benefit is a bad trade in a file that cannot be compiled here.
        foreach (var (token, value) in DesignTokens.Motion.OrderBy(static p => p.Value))
        {
            xaml.AppendLine(invariant, $"    <x:Double x:Key=\"{KeyFor(token)}Ms\">{value}</x:Double>");
        }

        xaml.AppendLine();

        // Two line heights per step, because line height is a function of script. The shell selects
        // which family of keys a page uses when the language changes.
        foreach (var step in DesignTokens.TypeRamp)
        {
            var key = KeyFor(step.Name);
            xaml.AppendLine(invariant, $"    <x:Double x:Key=\"{key}Size\">{step.Size}</x:Double>");
            xaml.AppendLine(
                invariant,
                $"    <x:Double x:Key=\"{key}LineHeightLatin\">{step.LineHeightFor(UiScript.Latin)}</x:Double>");
            xaml.AppendLine(
                invariant,
                $"    <x:Double x:Key=\"{key}LineHeightArabic\">{step.LineHeightFor(UiScript.Arabic)}</x:Double>");
            xaml.AppendLine(
                invariant,
                $"    <x:String x:Key=\"{key}Weight\">{(step.SemiBold ? "SemiBold" : "Normal")}</x:String>");
        }

        xaml.AppendLine();
        xaml.AppendLine(invariant, $"    <x:String x:Key=\"NobaFontUiLatin\">{DesignTokens.UiFontFor(UiScript.Latin)}</x:String>");
        xaml.AppendLine(invariant, $"    <x:String x:Key=\"NobaFontUiArabic\">{DesignTokens.UiFontFor(UiScript.Arabic)}</x:String>");
        xaml.AppendLine(invariant, $"    <x:String x:Key=\"NobaFontMono\">{DesignTokens.MonoFont}</x:String>");
        xaml.AppendLine();
        xaml.AppendLine(invariant, $"    <x:Double x:Key=\"NobaToolbarHeight\">{DesignTokens.ToolbarHeight}</x:Double>");
        xaml.AppendLine(invariant, $"    <x:Double x:Key=\"NobaSidebarWidth\">{DesignTokens.SidebarWidth}</x:Double>");
        xaml.AppendLine(invariant, $"    <x:Double x:Key=\"NobaSidebarCollapsedWidth\">{DesignTokens.SidebarCollapsedWidth}</x:Double>");
        xaml.AppendLine(invariant, $"    <x:Double x:Key=\"NobaStatusBarHeight\">{DesignTokens.StatusBarHeight}</x:Double>");
        xaml.AppendLine(
            invariant,
            $"    <x:Double x:Key=\"NobaFileListRowHeightLatin\">{DesignTokens.FileListRowHeight(UiScript.Latin)}</x:Double>");
        xaml.AppendLine(
            invariant,
            $"    <x:Double x:Key=\"NobaFileListRowHeightArabic\">{DesignTokens.FileListRowHeight(UiScript.Arabic)}</x:Double>");
        xaml.AppendLine();
        xaml.AppendLine("</ResourceDictionary>");

        return xaml.ToString().ReplaceLineEndings("\n");
    }

    private static string ToArgb(TokenColor color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");
}
