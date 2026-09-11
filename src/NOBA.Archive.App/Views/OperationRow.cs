using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.Presentation.ViewModels;

namespace NOBA.Archive.App.Views;

/// <summary>
/// One operation's card, built once and updated in place.
/// </summary>
/// <remarks>
/// <para>
/// Built once is the whole point. An extraction reports progress after every entry — thirty times a
/// second on a fast disk — and rebuilding the card each time would throw away and re-create a dozen
/// elements per tick, which is both wasteful and visibly janky. So the elements are held as fields and
/// only their text changes.
/// </para>
/// <para>
/// It is a class rather than a method returning a <c>Grid</c> for exactly that reason: something has to
/// remember which <c>TextBlock</c> is which between one report and the next.
/// </para>
/// </remarks>
internal sealed class OperationRow
{
    private readonly TextBlock _verb = new();
    private readonly TextBlock _target = new();
    private readonly TextBlock _stateGlyph = new();
    private readonly TextBlock _stateText = new();
    private readonly Border _stateChip = new();
    private readonly ProgressBar _bar = new();
    private readonly TextBlock _item = new();
    private readonly StackPanel _measures = new();
    private readonly StackPanel _result = new();
    private readonly TextBlock _error = new();
    private readonly TextBlock _destination = new();
    private readonly Button _toggle = new();
    private readonly Button _cancel = new();
    private readonly StackPanel _controls = new();

    public OperationRow(OperationViewModel operation, OperationsViewModel operations)
    {
        Operation = operation;

        Element = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };

        PaintSurface(Element, "NobaSurfaceRaised", "NobaBorderSubtle");

        var grid = new Grid
        {
            ColumnSpacing = DesignTokens.Spacing["space.3"],
            RowSpacing = DesignTokens.Spacing["space.2"],
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 6; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // Row 0: verb, target, and the state as glyph *and* word.
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DesignTokens.Spacing["space.2"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        Style(_verb, "NobaRowNameStyle");
        Style(_target, "NobaRowNameStyle");
        title.Children.Add(_verb);
        title.Children.Add(_target);

        _stateGlyph.FontFamily = IconFont();
        _stateGlyph.FontSize = 12;
        _stateGlyph.VerticalAlignment = VerticalAlignment.Center;

        _stateText.FontSize = 12;
        _stateText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _stateText.VerticalAlignment = VerticalAlignment.Center;

        var chipContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chipContent.Children.Add(_stateGlyph);
        chipContent.Children.Add(_stateText);

        _stateChip.CornerRadius = new CornerRadius(3);
        _stateChip.Padding = new Thickness(7, 2, 7, 2);
        _stateChip.VerticalAlignment = VerticalAlignment.Center;
        _stateChip.Child = chipContent;
        title.Children.Add(_stateChip);

        Grid.SetRow(title, 0);
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        // Row 0, column 1: the controls for this operation and no other.
        _controls.Orientation = Orientation.Horizontal;
        _controls.Spacing = DesignTokens.Spacing["space.2"];
        _controls.VerticalAlignment = VerticalAlignment.Center;

        Style(_toggle, "NobaSecondaryButtonStyle");
        Style(_cancel, "NobaSecondaryButtonStyle");

        _toggle.Click += (_, _) =>
        {
            if (Operation.CanResume)
            {
                operations.Resume(Operation.Id);
            }
            else
            {
                operations.Pause(Operation.Id);
            }
        };

        _cancel.Click += (_, _) => operations.Cancel(Operation.Id);

        _controls.Children.Add(_toggle);
        _controls.Children.Add(_cancel);
        Grid.SetRow(_controls, 0);
        Grid.SetColumn(_controls, 1);
        grid.Children.Add(_controls);

        _bar.Height = 4;
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        Grid.SetRow(_bar, 1);
        Grid.SetColumnSpan(_bar, 2);
        grid.Children.Add(_bar);

        // Row 2: the entry being written, then the measured facts.
        var live = new Grid { ColumnSpacing = DesignTokens.Spacing["space.3"] };
        live.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        live.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Style(_item, "NobaFindingPathStyle");
        Grid.SetColumn(_item, 0);
        live.Children.Add(_item);

        _measures.Orientation = Orientation.Horizontal;
        _measures.Spacing = 5;
        _measures.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_measures, 1);
        live.Children.Add(_measures);

        Grid.SetRow(live, 2);
        Grid.SetColumnSpan(live, 2);
        grid.Children.Add(live);

        // Row 3: what a finished operation produced.
        _result.Orientation = Orientation.Horizontal;
        _result.Spacing = 5;
        Grid.SetRow(_result, 3);
        Grid.SetColumnSpan(_result, 2);
        grid.Children.Add(_result);

        // Row 3, under the result: where the files went. A person who has just cancelled or completed
        // an extraction needs to be able to find them, and until this row existed nothing anywhere in
        // the product said which folder the picker had returned.
        Style(_destination, "NobaFindingPathStyle");
        _destination.TextTrimming = TextTrimming.CharacterEllipsis;
        // Fully qualified: this file imports both Microsoft.UI.Xaml and NOBA.Archive.Presentation.Bidi,
        // and each declares a FlowDirection. The one meant here is the framework's - it is being
        // assigned to a TextBlock property - and SummaryView spells it out the same way.
        _destination.FlowDirection = Microsoft.UI.Xaml.FlowDirection.LeftToRight;
        Grid.SetRow(_destination, 4);
        Grid.SetColumnSpan(_destination, 2);
        grid.Children.Add(_destination);

        // Row 5: why a failed one failed, in the engine's own words.
        Style(_error, "NobaFindingMessageStyle");
        _error.Visibility = Visibility.Collapsed;
        Grid.SetRow(_error, 5);
        Grid.SetColumnSpan(_error, 2);
        grid.Children.Add(_error);

        Element.Child = grid;
    }

    public OperationViewModel Operation { get; }

    public Border Element { get; }

    /// <summary>Refreshes the card from the operation. No element is created here.</summary>
    public void Update(LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _verb.Text = OperationFormatter.VerbOf(Operation, text);
        _target.Text = OperationFormatter.TargetOf(Operation);

        var destination = OperationFormatter.DestinationOf(Operation);
        _destination.Text = destination.Length > 0 ? $"{text.OperationToLabel} {destination}" : string.Empty;
        _destination.Visibility = destination.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _stateGlyph.Text = OperationFormatter.GlyphFor(Operation.State);
        _stateText.Text = OperationFormatter.StateLabel(Operation.State, text);

        var tone = ToneKeyFor(Operation.State);
        PaintForeground(_stateGlyph, tone);
        PaintForeground(_stateText, tone);
        PaintBackground(_stateChip, Operation.State is OperationState.Failed or OperationState.Cancelled
            ? "NobaSurfaceSunken"
            : "NobaAccentSubtle");

        // Indeterminate is a measurement the engine could not make, not a loading animation.
        // IsRunning covers Cancelling too: the writer is still working while it unwinds, and freezing
        // the bar there would read as "stopped" before it had.
        _bar.IsIndeterminate = Operation.IsIndeterminate && Operation.IsRunning;
        _bar.Value = Operation.Percent ?? 0;
        _bar.Visibility = Operation.IsFinished ? Visibility.Collapsed : Visibility.Visible;

        _item.Text = OperationFormatter.CurrentItemOf(Operation);
        _item.Visibility = Operation.IsFinished ? Visibility.Collapsed : Visibility.Visible;

        SummaryView.Render(_measures, Operation.IsFinished ? [] : OperationFormatter.MeasuresOf(Operation, text));
        SummaryView.Render(_result, OperationFormatter.ResultOf(Operation, text));

        if (Operation.State == OperationState.Failed && Operation.ErrorDetail is { Length: > 0 } detail)
        {
            _error.Text = $"{text.OperationFailedDetail}: {detail}";
            _error.Visibility = Visibility.Visible;
        }
        else
        {
            _error.Visibility = Visibility.Collapsed;
        }

        _controls.Visibility = Operation.IsFinished ? Visibility.Collapsed : Visibility.Visible;
        _toggle.Content = Operation.CanResume ? text.OperationResume : text.OperationPause;
        _toggle.IsEnabled = Operation.CanPause || Operation.CanResume;
        _cancel.Content = text.OperationCancel;
        _cancel.IsEnabled = Operation.CanCancel;
    }

    private static string ToneKeyFor(OperationState state) => state switch
    {
        OperationState.Completed => "NobaStateAllowed",
        OperationState.Failed => "NobaStateBlocked",
        OperationState.Cancelled => "NobaTextSecondary",
        OperationState.Cancelling => "NobaStateWarning",
        OperationState.Paused => "NobaStateWarning",
        _ => "NobaAccentDefault",
    };

    private static FontFamily IconFont() =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("NobaIconFont", out var value)
            && value is FontFamily font
                ? font
                : new FontFamily("Segoe Fluent Icons");

    private static void PaintSurface(Border target, string backgroundKey, string borderKey)
    {
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        if (resources.TryGetValue(backgroundKey, out var background) && background is Brush fill)
        {
            target.Background = fill;
        }

        if (resources.TryGetValue(borderKey, out var border) && border is Brush stroke)
        {
            target.BorderBrush = stroke;
        }
    }

    private static void PaintBackground(Border target, string key)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            target.Background = brush;
        }
    }

    private static void PaintForeground(TextBlock target, string key)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            target.Foreground = brush;
        }
    }

    private static void Style(FrameworkElement target, string styleKey)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(styleKey, out var value)
            && value is Style style)
        {
            target.Style = style;
        }
    }
}
