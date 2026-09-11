using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.App.Views;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.Services;
using NOBA.Archive.Presentation.ViewModels;
using WinUiFlowDirection = Microsoft.UI.Xaml.FlowDirection;

namespace NOBA.Archive.App;

/// <summary>
/// The shell: navigation, language, theme, status.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is wiring. The decisions — which direction the UI runs in, which font stack, how
/// tall a row is, what the text says — are properties of <see cref="ShellViewModel"/>, which is tested
/// on a machine that cannot build this file at all.
/// </para>
/// <para>
/// Language is applied in one place, to the root element. Setting <c>FlowDirection</c> per page is how
/// a product ends up with a dialog that mirrors and a list that does not.
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private int _operationRenderQueued;
    private OperationViewModel? _watched;

    /// <summary>
    /// False until the view models exist.
    /// </summary>
    /// <remarks>
    /// XAML raises events while it is still building the tree, and every handler on this window runs
    /// against <see cref="Shell"/>, which does not exist until <c>InitializeComponent</c> has returned.
    /// One <c>IsChecked="True"</c> in markup was enough to crash the constructor; this flag makes that
    /// whole class of ordering bug impossible rather than fixing the one instance of it.
    /// </remarks>
    private bool _ready;

    public MainWindow(IArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        InitializeComponent();

        Shell = new ShellViewModel(workspace);
        Browse.Bind(Shell);
        Create.Bind(Shell);
        Operations.Bind(Shell);

        Shell.Operations.PropertyChanged += OnOperationsChanged;

        // The status bar reports the archive, so it follows the archive rather than the language.
        Shell.Browse.PropertyChanged += OnBrowseChanged;

        // The system title bar goes away entirely and we draw the caption buttons ourselves, because
        // the native frame does not mirror for an RTL UI: in Arabic the system buttons stayed on the
        // physical right, which is the wrong end of the window. Ours are XAML, at the logical end of
        // the title row, so they mirror with everything else. The border stays, so resizing and the
        // drop shadow are unchanged.
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        ApplyLanguage();
        ApplyTheme();

        // Checked last, and in code rather than in markup. Setting IsChecked="True" in XAML raises
        // Checked during InitializeComponent - which runs before Shell is assigned - so the handler
        // dereferenced a null view model and the window never appeared at all.
        _ready = true;
        NavBrowse.IsChecked = true;
    }

    public ShellViewModel Shell { get; }

    /// <summary>
    /// The theme the shell is currently painted in.
    /// </summary>
    /// <remarks>
    /// Exposed for the dialogs, which live in a popup outside this window's element tree and therefore
    /// inherit nothing from it. Without this a dark shell raises a light modal.
    /// </remarks>
    public ElementTheme CurrentTheme => Root.RequestedTheme;

    /// <summary>
    /// Keeps the status line current, from whichever thread the notification arrives on.
    /// </summary>
    /// <remarks>
    /// Same rule as the browse page: the view models raise notifications off the UI thread, and the
    /// component with the thread affinity is the one that pays for it.
    /// </remarks>
    private void OnBrowseChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BrowseViewModel.SummaryLine)
            or nameof(BrowseViewModel.HasArchive)
            or nameof(BrowseViewModel.SelectionSummary)))
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RenderStatus();
            return;
        }

        DispatcherQueue.TryEnqueue(RenderStatus);
    }

    /// <summary>
    /// The status bar says what is selected, and falls back to the archive only when nothing is.
    /// </summary>
    /// <remarks>
    /// It used to repeat the summary that already sits in the archive header two rows above. A status
    /// bar that duplicates another line is a row of pixels spent on nothing.
    /// </remarks>
    private void RenderStatus()
    {
        if (!Shell.Browse.HasArchive)
        {
            StatusParts.Children.Clear();
            return;
        }

        var text = Shell.Text;
        var selection = SummaryFormatter.ForSelection(Shell.Browse.Selection, text);

        SummaryView.Render(
            StatusParts,
            selection.Count > 0 ? selection : SummaryFormatter.ForArchive(Shell.Browse.Statistics, text));
    }

    /// <summary>
    /// Keeps the operation strip current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Progress arrives from the queue's own <c>Progress</c> sink, which is constructed on a pool
    /// thread and therefore has no synchronisation context to post back to. Every report reaches this
    /// window on whatever thread the extraction is running on, so it is marshalled here — the component
    /// with the thread affinity pays for it, as everywhere else in this shell.
    /// </para>
    /// <para>
    /// Coalesced, because a fast extraction reports far more often than a screen can usefully redraw,
    /// and each redraw would otherwise queue another one behind it.
    /// </para>
    /// </remarks>
    private void OnOperationsChanged(object? sender, PropertyChangedEventArgs e) => QueueOperationRender();

    private void OnActiveOperationChanged(object? sender, PropertyChangedEventArgs e) => QueueOperationRender();

    private void QueueOperationRender()
    {
        if (Interlocked.Exchange(ref _operationRenderQueued, 1) == 1)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _operationRenderQueued, 0);
            RenderOperation();
        });
    }

    private void RenderOperation()
    {
        var text = Shell.Text;
        var operation = Shell.Operations.Active;

        // Re-subscribe to whichever operation is current, so a second extraction does not keep
        // redrawing from the first one's notifications.
        if (!ReferenceEquals(_watched, operation))
        {
            if (_watched is not null)
            {
                _watched.PropertyChanged -= OnActiveOperationChanged;
            }

            _watched = operation;

            if (_watched is not null)
            {
                _watched.PropertyChanged += OnActiveOperationChanged;
            }
        }

        if (operation is null)
        {
            OperationStrip.Visibility = Visibility.Collapsed;
            OperationMeasures.Children.Clear();
            return;
        }

        OperationStrip.Visibility = Visibility.Visible;
        OperationVerb.Text = OperationFormatter.VerbOf(operation, text);
        OperationTarget.Text = OperationFormatter.TargetOf(operation);

        // Glyph and word together, so the state never depends on the reader seeing a hue.
        OperationStateGlyph.Text = OperationFormatter.GlyphFor(operation.State);
        OperationStateText.Text = OperationFormatter.StateLabel(operation.State, text);

        // Indeterminate is a fact the engine reported, not a loading style: a solid archive cannot say
        // how far along it is, and a bar that invents a number is worse than one that admits it.
        // IsRunning includes Cancelling: the work has not stopped yet, and a bar that freezes there
        // would say it had.
        OperationBar.IsIndeterminate = operation.IsIndeterminate && operation.IsRunning;
        OperationBar.Value = operation.Percent ?? 0;

        // A finished operation has no progress to show. Leaving the bar and the measures in place made
        // a refused archive read "Failed ... progress cannot be measured", which describes nothing that
        // happened: the extraction never began, so there was no progress to fail to measure.
        var finished = operation.IsFinished;
        OperationBar.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        OperationItem.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;

        OperationItem.Text = OperationFormatter.CurrentItemOf(operation);

        // What it produced once it is done; what it is doing while it runs; the reason if it failed.
        SummaryView.Render(
            OperationMeasures,
            operation.State == OperationState.Failed && operation.ErrorDetail is { Length: > 0 }
                ? []
                : finished
                    ? OperationFormatter.ResultOf(operation, text)
                    : OperationFormatter.MeasuresOf(operation, text));

        // Where the files are going, on the shell strip as well as the row: the strip is what a person
        // watching an extraction is actually looking at.
        var destination = OperationFormatter.DestinationOf(operation);
        OperationDestination.Text = destination.Length > 0 ? $"{text.OperationToLabel} {destination}" : string.Empty;
        OperationDestination.Visibility = destination.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        OperationReason.Text = operation.State == OperationState.Failed && operation.ErrorDetail is { Length: > 0 } detail
            ? $"{text.OperationFailedDetail}: {detail}"
            : string.Empty;
        OperationReason.Visibility = OperationReason.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        OperationPauseButton.Content = operation.CanResume ? text.OperationResume : text.OperationPause;
        OperationPauseButton.IsEnabled = operation.CanPause || operation.CanResume;
        OperationPauseButton.Visibility = operation.IsFinished ? Visibility.Collapsed : Visibility.Visible;

        OperationCancelButton.Content = text.OperationCancel;
        OperationCancelButton.IsEnabled = operation.CanCancel;
        OperationCancelButton.Visibility = operation.IsFinished ? Visibility.Collapsed : Visibility.Visible;

        OperationDismissButton.Visibility = operation.IsFinished ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One button for both, because an operation is never pausable and resumable at once.</summary>
    private void OnPauseOperation(object sender, RoutedEventArgs e)
    {
        if (Shell.Operations.Active is not { } operation)
        {
            return;
        }

        if (operation.CanResume)
        {
            Shell.Operations.Resume(operation.Id);
        }
        else
        {
            Shell.Operations.Pause(operation.Id);
        }
    }

    private void OnCancelOperation(object sender, RoutedEventArgs e)
    {
        if (Shell.Operations.Active is { } operation)
        {
            Shell.Operations.Cancel(operation.Id);
        }
    }

    private void OnDismissOperation(object sender, RoutedEventArgs e) => Shell.Operations.DismissActive();

    /// <summary>
    /// Switches section.
    /// </summary>
    /// <remarks>
    /// The sidebar used to be decorative: four items, one of which did anything. Three controls that
    /// promise a screen and deliver nothing are exactly the fake controls the execution rules forbid, so
    /// the two that exist now navigate and the two that do not are disabled and say why.
    /// </remarks>
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton item)
        {
            return;
        }

        Shell.Section = item.Name switch
        {
            nameof(NavOperations) => ShellSection.Operations,
            nameof(NavCreate) => ShellSection.Create,
            nameof(NavSettings) => ShellSection.Settings,
            _ => ShellSection.Browse,
        };

        RenderSection();
    }

    /// <summary>
    /// Shows exactly one screen.
    /// </summary>
    /// <remarks>
    /// Written as one visibility per section rather than as a pair of ternaries, because the pair only
    /// worked while there were two screens: the moment a third arrived, "not Operations" would have
    /// meant Browse and Create at once, stacked in the same cell. Settings has no page yet, so its nav
    /// item stays disabled and this method is never asked to show it.
    /// </remarks>
    /// <summary>
    /// Finishes a launch that came from the Explorer menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seed has already put the shell on the right section; what is left is the part that needs a
    /// window. Create needs nothing more — its inputs are in the view model and the person chooses
    /// from there. Opening an archive is real work against a real file, so it is awaited rather than
    /// assumed, and a failure surfaces through the ordinary Browse error path.
    /// </para>
    /// <para>
    /// Fire-and-forget on purpose: this runs before <c>Activate</c>, and awaiting a network archive
    /// here would hold the window closed for as long as the file took to open. The screen renders
    /// empty and fills in, which is what it does for any other open.
    /// </para>
    /// </remarks>
    public void RenderShellRequest(SeedOutcome outcome, ArchiveActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RenderSection();

        if (outcome is not (SeedOutcome.ArchiveOpened or SeedOutcome.ExtractRequested))
        {
            return;
        }

        if (request.PrimaryInput is not { Length: > 0 } archive)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await Shell.Browse.OpenAsync(archive).ConfigureAwait(true);
            RenderSection();
        });
    }

    private void RenderSection()
    {
        var section = Shell.Section;

        Browse.Visibility = section == ShellSection.Browse ? Visibility.Visible : Visibility.Collapsed;
        Create.Visibility = section == ShellSection.Create ? Visibility.Visible : Visibility.Collapsed;
        Operations.Visibility = section == ShellSection.Operations ? Visibility.Visible : Visibility.Collapsed;

        if (section == ShellSection.Operations)
        {
            Operations.Render();
        }
        else if (section == ShellSection.Create)
        {
            Create.Render();
        }
    }

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        Shell.ToggleLanguage();
        ApplyLanguage();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        Shell.ToggleTheme();
        ApplyTheme();
    }

    /// <summary>
    /// Applies everything the language decides, in one pass.
    /// </summary>
    /// <remarks>
    /// Direction, font stack and text are set together because they are one decision. A code path that
    /// could set the text without the direction is a code path that will eventually be taken.
    /// </remarks>
    private void ApplyLanguage()
    {
        var text = Shell.Text;

        Root.FlowDirection = Shell.IsRightToLeft ? WinUiFlowDirection.RightToLeft : WinUiFlowDirection.LeftToRight;

        // Grid has no FontFamily - it is declared on Control and TextBlock, not on Panel - so the
        // script's font stack is applied as an implicit TextBlock style on the root's resources, and
        // to the controls that render their own content text.
        ApplyFont(new Microsoft.UI.Xaml.Media.FontFamily(Shell.UiFontFamily));

        Title = text.AppName;
        TitleText.Text = text.AppName;

        NavBrowse.Content = text.NavBrowse;
        NavCreate.Content = text.NavCreate;
        NavOperations.Content = text.NavOperations;
        NavSettings.Content = text.NavSettings;

        // Create is a real screen as of this drop, so its item navigates like the other two. Settings
        // is not built yet, and the control says so rather than accepting a click that goes nowhere.
        NavSettings.IsEnabled = false;
        ToolTipService.SetToolTip(NavSettings, text.NotBuiltYet);

        LanguageLabel.Text = text.ToggleLanguage;
        ThemeLabel.Text = text.ToggleTheme;

        SidebarColumn.Width = new GridLength(Shell.SidebarWidth);
        ApplyTitleBarInsets();

        // Every screen and every strip, or the language pass leaves half the window in the old one.
        // The operations page was shipped blank because these three lines were written into a patch
        // that never matched this method - a silent no-op that no test could see, because none of it
        // can be compiled here.
        Browse.ApplyLanguage(Shell);
        Create.ApplyLanguage(Shell);
        Operations.ApplyLanguage(Shell);
        RenderStatus();
        RenderOperation();
        RenderSection();
        RenderCaptionGlyphs();
    }

    /// <summary>
    /// Keeps the title out from under the caption buttons, in either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window's caption buttons are drawn by Windows on the native frame, and the native frame does
    /// not mirror because a XAML element set its <c>FlowDirection</c>. So in Arabic the title ran to the
    /// physical right and straight underneath minimise, maximise and close.
    /// </para>
    /// <para>
    /// <c>AppWindow.TitleBar</c> reports how much room those buttons occupy, in physical pixels, so the
    /// content is inset by that much on the side they are on. Margins mirror with the flow direction,
    /// which is why the same <c>Thickness</c> is written the other way round for Arabic: the logical
    /// start is the physical right there. The buttons themselves stay where Windows puts them — moving
    /// them would need the native RTL frame, which WinUI 3 does not expose, and I would rather the title
    /// be readable than claim a mirror we do not have.
    /// </para>
    /// </remarks>
    private void ApplyTitleBarInsets()
    {
        // Logical margin: the start edge is the physical left in English and the physical right in
        // Arabic, which is exactly where the title belongs in each. The caption buttons occupy the
        // other end of the same row, so nothing has to be reserved for them by hand any more.
        const double edge = 14;
        TitleBarContent.Margin = new Thickness(edge, 0, edge, 0);
    }

    private OverlappedPresenter? Presenter => AppWindow?.Presenter as OverlappedPresenter;

    private void OnMinimise(object sender, RoutedEventArgs e) => Presenter?.Minimize();

    private void OnMaximise(object sender, RoutedEventArgs e)
    {
        if (Presenter is not { } presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        else
        {
            presenter.Maximize();
        }

        RenderCaptionGlyphs();
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>The maximise glyph names what the button will do, not what the window is.</summary>
    private void RenderCaptionGlyphs() =>
        MaximiseButton.Content = Presenter?.State == OverlappedPresenterState.Maximized ? "\uE923" : "\uE922";

    private void ApplyFont(Microsoft.UI.Xaml.Media.FontFamily family)
    {
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, family));
        Root.Resources[typeof(TextBlock)] = textStyle;

        foreach (var control in new Control[] { NavBrowse, NavCreate, NavOperations, NavSettings, LanguageButton, ThemeButton })
        {
            control.FontFamily = family;
        }

        Browse.FontFamily = family;
        Create.FontFamily = family;
    }

    private void ApplyTheme()
    {
        var dark = Shell.Theme == NOBA.Archive.Presentation.Design.ThemeMode.Dark;

        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        // The glyph names the theme you would switch to, which is what the button does.
        ThemeGlyph.Text = dark ? "" : "";
    }
}
