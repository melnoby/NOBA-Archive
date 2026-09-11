using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Presentation.ViewModels;

namespace NOBA.Archive.App.Views;

/// <summary>
/// Turns summary fragments into WinUI text.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that knows a <see cref="SummaryPart"/> becomes a <see cref="TextBlock"/>, and
/// it exists in the WinUI project rather than in Presentation because that is the layer that owns the
/// element tree. Presentation decides what the fragments are and which way each one runs; nothing in
/// this file makes that decision, and nothing in the XAML does either.
/// </para>
/// <para>
/// One <see cref="TextBlock"/> per fragment, not one text block of several <c>Run</c>s. The distinction
/// is the whole point of the change. Digit substitution is decided per <em>paragraph</em>: the shaper
/// looks back for the nearest strong character, and inline runs share their block's paragraph, so a run
/// holding only <c>9</c> that follows a run of Arabic would still be shaped as Arabic-Indic. A separate
/// text block is a separate paragraph, and a paragraph whose entire content is <c>9</c> has no Arabic
/// to be influenced by.
/// </para>
/// <para>
/// The host panel is not given a direction here. It inherits the window's, so the fragments are laid
/// out in the reading order of the UI language while each one is shaped by its own content — which is
/// the behaviour the table has had all along.
/// </para>
/// </remarks>
internal static class SummaryView
{
    private const string PartStyleKey = "NobaSummaryPartStyle";

    /// <summary>Replaces the panel's contents with one text block per fragment.</summary>
    public static void Render(Panel host, IReadOnlyList<SummaryPart> parts)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(parts);

        host.Children.Clear();

        foreach (var part in parts)
        {
            host.Children.Add(Block(part));
        }
    }

    private static TextBlock Block(SummaryPart part)
    {
        var block = new TextBlock { Text = part.Text };

        // The style, not a brush pulled from Application.Resources: that lookup ignores
        // ThemeDictionaries and hands back whichever theme came first, which is how the file names
        // once ended up invisible in the light theme.
        Apply(block, PartStyleKey);

        block.FlowDirection = part.Direction == SummaryDirection.RightToLeft
            ? Microsoft.UI.Xaml.FlowDirection.RightToLeft
            : Microsoft.UI.Xaml.FlowDirection.LeftToRight;

        return block;
    }

    private static void Apply(FrameworkElement target, string styleKey)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(styleKey, out var value)
            && value is Style style)
        {
            target.Style = style;
        }
    }
}
