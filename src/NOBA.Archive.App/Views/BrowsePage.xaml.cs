using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NOBA.Archive.App.Views;

/// <summary>
/// The browse screen.
/// </summary>
/// <remarks>
/// <para>
/// The whole of this file is presentation: pick a file, hand the path to the view model, render what
/// comes back. There is no path validation here, no conflict handling, no decision about whether an
/// entry may be extracted — all of that is below the seam, and this screen would be wrong to duplicate
/// any of it. What each cell says is decided by <see cref="RowColumnFormatter"/>, which is tested where
/// this file cannot even be compiled.
/// </para>
/// <para>
/// Rows are built in code rather than from a DataTemplate because a refused entry has to be rendered
/// differently in a way that is not colour alone. The header row is built by the same helper as the
/// data rows, so the two cannot drift out of alignment — a table whose header does not line up with its
/// body is worse than no header.
/// </para>
/// </remarks>
public sealed partial class BrowsePage : UserControl
{
    /// <summary>Column widths. One definition, used by the header and by every row.</summary>
    private static readonly double[] ColumnWidths = [0, 96, 116, 116, 148, 72];

    /// <summary>
    /// Where each column's text sits, as one definition shared by the header and the rows.
    /// </summary>
    /// <remarks>
    /// <see cref="TextAlignment"/> in WinUI is logical, not physical: the same <c>Right</c> resolves to
    /// opposite screen edges depending on the flow direction of the element it is on. So alignment
    /// alone is not enough - the flow direction has to match too, which is what
    /// <see cref="IsNotationColumn"/> settles. Held here rather than at each call site because a header
    /// that disagrees with its own column is the one defect a table cannot survive.
    /// </remarks>
    private static readonly TextAlignment[] ColumnAlignment =
    [
        TextAlignment.Left,   // name - follows the window, like the names themselves
        TextAlignment.Left,   // type
        TextAlignment.Right,  // size
        TextAlignment.Right,  // packed size
        TextAlignment.Left,   // modified
        TextAlignment.Left,   // attributes
    ];

    /// <summary>
    /// True for the columns that hold notation rather than language.
    /// </summary>
    /// <remarks>
    /// Column zero is the file name: it reads in the window's direction, because a name can be in
    /// either script. Everything after it is digits, dates and DOS attribute letters, which run left to
    /// right whatever the window does — so the heading has to run that way as well, or the two land on
    /// opposite sides of the same column.
    /// </remarks>
    private static bool IsNotationColumn(int column) => column > 0;

    /// <summary>Marks the row that walks up a folder, which is navigation rather than an entry.</summary>
    private static readonly object ParentSentinel = new();

    private ShellViewModel? _shell;
    private int _listRenderQueued;

    public BrowsePage() => InitializeComponent();

    private BrowseViewModel? Model => _shell?.Browse;

    public void Bind(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;

        // Two signals, because the model has two. Content changes are collection changes on Rows -
        // Refresh() raises no property for them at all - while everything a property notification says
        // is either about the archive as a whole or about what the buttons may do.
        Model!.PropertyChanged += OnModelChanged;
        Model.Rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>
    /// The bridge between the view model's notifications and this control's elements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Threads.</b> <see cref="INotifyPropertyChanged"/> promises nothing about which thread raises
    /// it, and the view models are deliberately platform-neutral: they await with
    /// <c>ConfigureAwait(false)</c>, so every notification after the first await in <c>OpenAsync</c> or
    /// <c>ExtractAsync</c> arrives on a thread-pool thread. A WinUI element may only be touched from the
    /// thread that created it. The affinity is WinUI's requirement, so the marshalling is WinUI's cost.
    /// </para>
    /// <para>
    /// <b>Why this is a switch and not a single render.</b> Rebuilding the list on every notification
    /// was not merely wasteful, it was a cycle: rebuilding clears <c>FileList.Items</c>, the list view
    /// answers with <c>SelectionChanged</c>, that calls <c>Select</c>, and <c>Select</c> raises
    /// <c>CanExtractSelection</c> <em>unconditionally</em>. The window stopped responding with ten
    /// entries in the archive, because the loop never depended on how many there were.
    /// </para>
    /// </remarks>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowseViewModel.HasArchive):
            case nameof(BrowseViewModel.ShowEncodingWarning):
            case nameof(BrowseViewModel.ArchiveDisplayName):
            case nameof(BrowseViewModel.SummaryLine):
                QueueListRender();
                break;

            // Only what the toolbar may do. Never the list - see the cycle above.
            case nameof(BrowseViewModel.CanExtractAll):
            case nameof(BrowseViewModel.CanExtractSelection):
            case nameof(BrowseViewModel.CanTest):
            case nameof(BrowseViewModel.CanAddEntries):
            case nameof(BrowseViewModel.CanRemoveSelected):
            case nameof(BrowseViewModel.CanRenameSelected):
            case nameof(BrowseViewModel.IsBusy):
                OnUiThread(RenderCommands);
                break;

            // The verdict panel, and nothing else. Deliberately not routed through QueueListRender:
            // rebuilding the list clears the selection, which raises CanExtractSelection, which is the
            // loop that froze this window once already.
            case nameof(BrowseViewModel.TestResult):
            case nameof(BrowseViewModel.IsTesting):
                OnUiThread(RenderTest);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The list's real content signal.
    /// </summary>
    /// <remarks>
    /// <c>Refresh()</c> rebuilds <c>Rows</c> and raises no property change for it, so a page that
    /// listened only to <see cref="INotifyPropertyChanged"/> would redraw folder navigation by accident
    /// or not at all — <c>Enter</c> and <c>GoUp</c> raise nothing whatsoever.
    /// </remarks>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueListRender();

    /// <summary>Collapses a burst of change notifications into one rebuild.</summary>
    private void QueueListRender()
    {
        if (Interlocked.Exchange(ref _listRenderQueued, 1) == 1)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _listRenderQueued, 0);
            RenderList();
        });
    }

    private void OnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        DispatcherQueue.TryEnqueue(() => action());
    }

    public void ApplyLanguage(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var text = shell.Text;

        OpenButton.Content = text.OpenArchive;
        ExtractAllButton.Content = text.ExtractAll;
        ExtractSelectedButton.Content = text.ExtractSelected;
        TestButton.Content = text.TestArchive;
        EmptyOpenButton.Content = text.OpenArchive;

        // Same words the Create screen uses for the same verbs - see LocalizedStrings' own remark on
        // why "Add files", "Add folder" and "Remove" are not duplicated under a second key.
        AddFilesButton.Content = text.CreateAddFiles;
        AddFolderButton.Content = text.CreateAddFolder;
        RemoveButton.Content = text.CreateRemove;
        RenameButton.Content = text.BrowseRename;

        SearchBox.PlaceholderText = text.SearchPlaceholder;
        ToolTipService.SetToolTip(ClearSearchButton, text.ClearSearch);
        ToolTipService.SetToolTip(HomeButton, text.GoHomeLabel);
        ToolTipService.SetToolTip(UpButton, text.GoUpLabel);

        EmptyTitle.Text = text.EmptyTitle;
        EmptyBody.Text = text.EmptyBody;
        EmptyDropHint.Text = text.DropHint;
        EncodingWarning.Message = text.GuessedEncodingWarning;

        Render();
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();

        // The list comes from the engine: this build offers exactly what it can open, and a format
        // added to the registry appears here without anyone editing this file.
        foreach (var extension in Model?.OpenableExtensions ?? [])
        {
            picker.FileTypeFilter.Add(extension);
        }

        if (picker.FileTypeFilter.Count == 0)
        {
            // A picker with no filter throws. Nothing openable is a real state - no native library and
            // no managed reader - and it must not take the window down with it.
            picker.FileTypeFilter.Add("*");
        }

        // Unpackaged apps must give the picker an owning window; without this it throws at runtime.
        var window = ((App)Microsoft.UI.Xaml.Application.Current).MainWindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);

        var file = await picker.PickSingleFileAsync();
        if (file is not null && Model is not null)
        {
            await Model.OpenAsync(file.Path);
        }
    }

    private async void OnExtractAll(object sender, RoutedEventArgs e) => await ExtractAsync(selectionOnly: false);

    private async void OnExtractSelected(object sender, RoutedEventArgs e) => await ExtractAsync(selectionOnly: true);

    /// <summary>
    /// Asks where and how, then starts the extraction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dialog is the whole change here. What this used to do was open a folder picker and begin
    /// writing the moment it closed — the person never saw the destination echoed back, was never asked
    /// what should happen to files already there, and had no step at which to change their mind.
    /// </para>
    /// <para>
    /// The conflict policy the dialog returns is passed down the same seam the CLI's <c>--conflict</c>
    /// uses. Nothing about it is decided in this file, and a future shell verb can call the same
    /// method with a policy of its own.
    /// </para>
    /// </remarks>
    private async Task ExtractAsync(bool selectionOnly)
    {
        if (_shell is null || Model is null || !Model.HasArchive)
        {
            return;
        }

        var prompt = ExtractPlanner.Build(
            _shell.Text,
            Model.ArchiveDisplayName ?? string.Empty,
            selectionOnly,
            destination: null,
            policy: ExtractPlanner.Default,
            isRightToLeft: _shell.IsRightToLeft,
            fontFamily: _shell.UiFontFamily);

        var choice = await ((App)Microsoft.UI.Xaml.Application.Current)
            .ExtractDialog
            .ShowAsync(prompt, CancellationToken.None);

        if (choice is not null)
        {
            await Model.ExtractAsync(choice.Destination, selectionOnly, choice.ConflictPolicy);
        }
    }

    /// <summary>
    /// Verifies the open archive and shows what came back.
    /// </summary>
    /// <remarks>
    /// No archive logic here and no interpretation of the result: the engine reads every entry and
    /// checks the checksums, the view model keeps the verdict, and
    /// <see cref="TestReportFormatter"/> decides how it reads. This method starts it and lets the
    /// notification draw the panel, so a verdict that arrives any other way is drawn the same.
    /// </remarks>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        await Model.TestAsync().ConfigureAwait(true);
    }

    private void OnDismissTest(object sender, RoutedEventArgs e) => Model?.ClearTest();

    /// <summary>Picks files and adds them to the open archive. No archive-format filter: anything goes in.</summary>
    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");

        var window = ((App)Microsoft.UI.Xaml.Application.Current).MainWindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await Model.AddEntriesAsync([.. files.Select(static f => f.Path)]);
        }
    }

    /// <summary>
    /// Picks one folder and adds it to the open archive.
    /// </summary>
    /// <remarks>Walked recursively by the handler, exactly as a folder added to a new archive is.</remarks>
    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var picker = new FolderPicker();

        var window = ((App)Microsoft.UI.Xaml.Application.Current).MainWindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await Model.AddEntriesAsync([folder.Path]);
        }
    }

    /// <summary>
    /// Confirms, then removes the selection from the open archive.
    /// </summary>
    /// <remarks>
    /// The confirmation is not a formality: unlike removing a staged input before a new archive is
    /// written, this rewrites a file already on disk the moment the person agrees to it. The dialog
    /// says so, and <c>ConfirmDialog</c>'s own default button is Close rather than Primary for exactly
    /// this reason.
    /// </remarks>
    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        if (_shell is null || Model is null || !Model.CanRemoveSelected)
        {
            return;
        }

        var text = _shell.Text;
        var count = BidiText.FormatCount(Model.Selection.Count, CultureInfo.InvariantCulture);

        var prompt = new ConfirmPrompt(
            string.Format(_shell.Language.Culture, text.ManageRemoveTitle, count),
            text.ManageRemoveBody,
            text.ManageRemoveConfirm,
            text.ManageRemoveCancel,
            _shell.IsRightToLeft,
            _shell.UiFontFamily);

        var confirmed = await ((App)Microsoft.UI.Xaml.Application.Current)
            .ConfirmDialog
            .ShowAsync(prompt, CancellationToken.None);

        if (confirmed)
        {
            await Model.RemoveSelectedAsync();
        }
    }

    /// <summary>Asks for a new name, then renames the one selected entry.</summary>
    private async void OnRenameSelected(object sender, RoutedEventArgs e)
    {
        if (_shell is null || Model is null || !Model.CanRenameSelected)
        {
            return;
        }

        var node = Model.Selection[0];
        var text = _shell.Text;

        var prompt = new RenamePrompt(
            text.ManageRenameTitle,
            text.ManageRenameBody,
            text.ManageRenameField,
            node.Name,
            text.ManageRenameConfirm,
            text.ManageRenameCancel,
            _shell.IsRightToLeft,
            _shell.UiFontFamily);

        var newName = await ((App)Microsoft.UI.Xaml.Application.Current)
            .RenameDialog
            .ShowAsync(prompt, CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(newName))
        {
            await Model.RenameAsync(node, newName);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.Search = SearchBox.Text;
        }

        ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchShortcut.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Text = string.Empty;

    /// <summary>One level up, which is the movement a person actually makes.</summary>
    private void OnGoUp(object sender, RoutedEventArgs e) => Model?.GoUp();

    /// <summary>
    /// Walks back to the crumb that was clicked.
    /// </summary>
    /// <remarks>
    /// Expressed as repeated <c>GoUp</c> rather than a jump, because the view model owns navigation and
    /// a second way to move around the tree is a second idea of where you are. The archive is not
    /// reopened and nothing is re-listed: this only moves the current folder.
    /// </remarks>
    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (Model is null || sender is not FrameworkElement { Tag: int depth })
        {
            return;
        }

        for (var guard = 0; Model.Breadcrumbs.Count > depth + 1 && Model.CanGoUp && guard < 128; guard++)
        {
            Model.GoUp();
        }
    }

    /// <summary>
    /// Walks back to the archive's root.
    /// </summary>
    /// <remarks>
    /// Repeated <c>GoUp</c> rather than a new operation: the view model already owns navigation, and
    /// inventing a second way to move around the tree in the UI is how two ideas of "where am I" start.
    /// The guard is for a breadcrumb list that somehow never shortens; a spin here would freeze the
    /// window, and this screen has frozen once already.
    /// </remarks>
    private void OnGoHome(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        for (var guard = 0; Model.CanGoUp && guard < 128; guard++)
        {
            Model.GoUp();
        }
    }

    /// <summary>
    /// The rows are elements, so the node travels on <see cref="FrameworkElement.Tag"/>.
    /// </summary>
    private static ArchiveNode? NodeOf(object? item) => (item as FrameworkElement)?.Tag as ArchiveNode;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Model?.Select(FileList.SelectedItems.Select(NodeOf).Where(static n => n is not null).Select(static n => n!));

    private void OnRowActivated(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileList.SelectedItem is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, ParentSentinel))
        {
            Model?.GoUp();
            return;
        }

        if (NodeOf(FileList.SelectedItem) is { } node)
        {
            Model?.Enter(node);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e) => e.AcceptedOperation = DataPackageOperation.Copy;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Model is null)
        {
            return;
        }

        // Indexed, not FirstOrDefault: the result is an IReadOnlyList, and CA1826 is right that walking
        // an enumerator to reach element zero of an indexable collection is work for nothing.
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0 && items[0] is StorageFile file)
        {
            await Model.OpenAsync(file.Path);
        }
    }

    /// <summary>Everything, for the cases that really do change everything: binding and language.</summary>
    private void Render()
    {
        RenderList();
        RenderCommands();

        // The verdict is text in the UI language, so a language switch has to redraw it too. Leaving
        // it out would strand an Arabic panel above an English window until the next test.
        RenderTest();
    }

    /// <summary>
    /// The expensive half: the archive header, the breadcrumb, the columns and the rows.
    /// </summary>
    /// <remarks>
    /// The only method that touches <c>FileList.Items</c>, and therefore the only one that can provoke
    /// <c>SelectionChanged</c>. Nothing reachable from a selection notification may call it.
    /// </remarks>
    private void RenderList()
    {
        if (_shell is null || Model is null)
        {
            return;
        }

        var text = _shell.Text;
        var culture = _shell.Language.Culture;
        var hasArchive = Model.HasArchive;

        EmptyState.Visibility = hasArchive ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = hasArchive ? Visibility.Visible : Visibility.Collapsed;
        ArchiveHeader.Visibility = hasArchive ? Visibility.Visible : Visibility.Collapsed;
        ListChrome.Visibility = hasArchive ? Visibility.Visible : Visibility.Collapsed;
        EncodingWarning.IsOpen = Model.ShowEncodingWarning;

        ArchiveName.Text = Model.ArchiveDisplayName ?? string.Empty;
        FormatBadge.Text = Model.Format is { } format ? BidiText.IsolateAsLtr(format.DisplayName) : string.Empty;
        SummaryView.Render(ArchiveSummary, SummaryFormatter.ForArchive(Model.Statistics, text));

        ColumnHeaderHost.Child = BuildHeader(text);

        FileList.Items.Clear();

        if (Model.CanGoUp)
        {
            FileList.Items.Add(BuildRow(RowColumnFormatter.ParentLink(text), ParentSentinel, _shell));
        }

        foreach (var row in Model.Rows)
        {
            FileList.Items.Add(BuildRow(RowColumnFormatter.For(row, text, culture), row.Node, _shell));
        }

        RenderBreadcrumbs();
        UpButton.IsEnabled = Model.CanGoUp;
    }

    /// <summary>
    /// The trail, as buttons. A breadcrumb you cannot click is a label.
    /// </summary>
    private void RenderBreadcrumbs()
    {
        Breadcrumbs.Items.Clear();
        if (Model is null)
        {
            return;
        }

        for (var depth = 0; depth < Model.Breadcrumbs.Count; depth++)
        {
            if (depth > 0)
            {
                var separator = new TextBlock { Text = "\uE76C" };
                Apply(separator, "NobaRowGlyphStyle");
                separator.Width = 12;
                separator.FontSize = 10;
                Breadcrumbs.Items.Add(separator);
            }

            var crumb = new Button
            {
                Content = Model.Breadcrumbs[depth].DisplayName,
                Tag = depth,
            };
            Apply(crumb, "NobaCrumbButtonStyle");
            crumb.Click += OnCrumbClick;
            Breadcrumbs.Items.Add(crumb);
        }
    }

    /// <summary>
    /// The cheap half: what the toolbar may do.
    /// </summary>
    /// <remarks>
    /// Neither assignment can call back into the model. That is the whole reason this is separate: it is
    /// what a selection change is allowed to run.
    /// </remarks>
    private void RenderCommands()
    {
        if (Model is null)
        {
            return;
        }

        ExtractAllButton.IsEnabled = Model.CanExtractAll;
        ExtractSelectedButton.IsEnabled = Model.CanExtractSelection;

        // Asked of the provider registry through the view model, never hard-coded. The button was
        // disabled in markup, which is the same pixels for the wrong reason: it said "not built" when
        // the truth is "depends on the format", and it would have kept saying it after this shipped.
        TestButton.IsEnabled = Model.CanTest;

        AddFilesButton.IsEnabled = Model.CanAddEntries;
        AddFolderButton.IsEnabled = Model.CanAddEntries;
        RemoveButton.IsEnabled = Model.CanRemoveSelected;
        RenameButton.IsEnabled = Model.CanRenameSelected;
    }

    /// <summary>
    /// Draws the verdict.
    /// </summary>
    /// <remarks>
    /// Built in code rather than from a DataTemplate for the same reason the file rows are: a finding
    /// is three pieces of text with three different directions — a path that must run left to right, an
    /// engine sentence that is machine output, and a chip in the UI language — and a template would
    /// have to hand all three to one text run.
    /// </remarks>
    private void RenderTest()
    {
        if (_shell is null || Model is null)
        {
            return;
        }

        var text = _shell.Text;

        if (Model.IsTesting)
        {
            TestPanel.Visibility = Visibility.Visible;
            TestHeadline.Text = text.TestRunning;
            TestToneGlyph.Text = HourglassGlyph;
            ApplyTone(TestTone.Info);
            TestDetail.Children.Clear();
            TestFindings.Items.Clear();
            TestDismissButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (Model.TestResult is not { } result)
        {
            TestPanel.Visibility = Visibility.Collapsed;
            TestDetail.Children.Clear();
            TestFindings.Items.Clear();
            return;
        }

        // The format's capability report goes in too, so the panel can say what "intact" covered. The
        // page does not know which format it is looking at and does not need to.
        var report = TestReportFormatter.For(result, text, Model?.FormatReport);

        TestPanel.Visibility = Visibility.Visible;
        TestDismissButton.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(TestDismissButton, text.TestDismiss);

        TestHeadline.Text = report.Headline;
        TestToneGlyph.Text = GlyphForTone(report.Tone);
        ApplyTone(report.Tone);

        SummaryView.Render(TestDetail, report.Detail);

        TestFindings.Items.Clear();
        foreach (var finding in report.Findings)
        {
            TestFindings.Items.Add(BuildFinding(finding));
        }
    }

    private void ApplyTone(TestTone tone)
    {
        Apply(TestToneGlyph, tone switch
        {
            TestTone.Good => "NobaToneGoodStyle",
            TestTone.Warning => "NobaToneWarningStyle",
            TestTone.Bad => "NobaToneBadStyle",
            _ => "NobaToneInfoStyle",
        });

        Apply(TestToneMark, tone switch
        {
            TestTone.Good => "NobaToneGoodDiscStyle",
            TestTone.Warning => "NobaToneWarningDiscStyle",
            TestTone.Bad => "NobaToneBadDiscStyle",
            _ => "NobaToneInfoDiscStyle",
        });
    }

    /// <summary>
    /// A glyph per tone, so the verdict is never carried by colour alone.
    /// </summary>
    /// <remarks>
    /// Segoe Fluent Icons code points, written as escapes rather than pasted characters: a pasted
    /// glyph is invisible in a diff, survives a copy badly, and an ordinary Unicode check mark is not
    /// the same character as the icon font's - it renders as a box in the font this panel uses.
    /// </remarks>
    private static string GlyphForTone(TestTone tone) => tone switch
    {
        TestTone.Good => "\uE73E",     // CheckMark
        TestTone.Warning => "\uE7BA",  // Warning
        TestTone.Bad => "\uEA39",      // ErrorBadge
        _ => "\uE946",                 // Info
    };

    /// <summary>Stopwatch, shown while the engine is still reading the archive.</summary>
    private const string HourglassGlyph = "\uE916";

    /// <summary>
    /// One findings row: a chip in the UI language, a path, and the engine's own sentence.
    /// </summary>
    /// <remarks>
    /// Static and returning the concrete type, like <c>BuildRow</c> beside it: it reads nothing from
    /// this control, and the caller adds it straight to an items collection.
    /// </remarks>
    private static Grid BuildFinding(TestFindingRow finding)
    {
        var grid = new Grid
        {
            Padding = new Thickness(14, 9, 14, 9),
            ColumnSpacing = DesignTokens.Spacing["space.3"],
            BorderThickness = new Thickness(0, 1, 0, 0),
        };

        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("NobaBorderSubtle", out var rule)
            && rule is Brush hairline)
        {
            grid.BorderBrush = hairline;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Word first, then the path, then the machine's own sentence.
        var chip = Chip(finding.ChipLabel, finding.IsError ? "NobaRowBlockedChipStyle" : "NobaRowEncryptedChipStyle");
        Grid.SetColumn(chip, 0);
        grid.Children.Add(chip);

        var path = new TextBlock { Text = finding.EntryPath, MaxWidth = 320 };
        Apply(path, "NobaFindingPathStyle");
        Grid.SetColumn(path, 1);
        grid.Children.Add(path);

        var message = new TextBlock { Text = finding.Message };
        Apply(message, "NobaFindingMessageStyle");
        Grid.SetColumn(message, 2);
        grid.Children.Add(message);

        ToolTipService.SetToolTip(grid, finding.EntryPath);
        return grid;
    }

    /// <summary>The six columns, defined once so the header and the rows cannot disagree.</summary>
    private static Grid NewRowGrid()
    {
        var grid = new Grid { ColumnSpacing = DesignTokens.Spacing["space.3"] };

        // Column zero is the name and takes the slack; the rest are fixed, because a column of digits
        // that changes width as you scroll cannot be compared down the page.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 1; i < ColumnWidths.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidths[i]) });
        }

        return grid;
    }

    private static Grid BuildHeader(LocalizedStrings text)
    {
        var grid = NewRowGrid();
        var labels = new[]
        {
            text.ColumnName, text.ColumnType, text.ColumnSize,
            text.ColumnPacked, text.ColumnModified, text.ColumnAttributes,
        };

        for (var column = 0; column < labels.Length; column++)
        {
            var cell = new TextBlock { Text = labels[column] };

            // Indented to clear the row icon, so the header sits over the names rather than over
            // the glyph column.
            if (column == 0)
            {
                cell.Margin = new Thickness(28, 0, 0, 0);
            }

            Apply(cell, "NobaColumnHeaderStyle");
            AlignToColumn(cell, column);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private static Grid BuildRow(RowColumns columns, object tag, ShellViewModel shell)
    {
        var grid = NewRowGrid();
        grid.MinHeight = shell.FileListRowHeight;
        grid.Padding = new Thickness(8, 3, 8, 3);
        grid.Tag = tag;

        // A hairline under every row. Hover and selection tell you where the pointer is; the rule tells
        // you where a row ends, which is what lets the eye track across six columns without drifting a
        // line. Subtle enough that ten rows do not read as a grid of boxes.
        grid.BorderThickness = new Thickness(0, 0, 0, 1);
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("NobaBorderSubtle", out var rule)
            && rule is Brush hairline)
        {
            grid.BorderBrush = hairline;
        }

        // Name cell: glyph, then the name, then the state chip if there is one.
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = DesignTokens.Spacing["space.2"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        var glyph = new TextBlock { Text = GlyphFor(columns) };
        Apply(glyph, columns.IsRejected ? "NobaRowBlockedGlyphStyle" : columns.IsFolder ? "NobaRowFolderGlyphStyle" : "NobaRowGlyphStyle");
        name.Children.Add(glyph);

        var label = new TextBlock { Text = columns.Name };
        Apply(label, "NobaRowNameStyle");
        name.Children.Add(label);

        if (columns.StateLabel is { } state)
        {
            // Icon and text, never colour alone: a security refusal rendered as a red row is a refusal
            // that a large number of people cannot see.
            name.Children.Add(Chip(
                columns.IsRejected ? $" {state}" : $" {state}",
                columns.IsRejected ? "NobaStateBlocked" : "NobaStateEncrypted"));
        }

        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        AddCell(grid, 1, columns.TypeLabel);
        AddCell(grid, 2, columns.Size);
        AddCell(grid, 3, columns.PackedSize);
        AddCell(grid, 4, columns.Modified);
        AddCell(grid, 5, columns.Attributes);

        if (columns.StateDetail is { } detail)
        {
            ToolTipService.SetToolTip(grid, detail);
        }

        return grid;
    }

    /// <summary>Applies the one shared alignment and flow for a column, to header and row alike.</summary>
    private static void AlignToColumn(TextBlock cell, int column)
    {
        if (IsNotationColumn(column))
        {
            cell.FlowDirection = Microsoft.UI.Xaml.FlowDirection.LeftToRight;
        }

        cell.TextAlignment = ColumnAlignment[column];
    }

    private static void AddCell(Grid grid, int column, string value)
    {
        var cell = new TextBlock { Text = value };
        Apply(cell, "NobaRowCellStyle");
        AlignToColumn(cell, column);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static string GlyphFor(RowColumns columns) =>
        columns.IsParentLink ? "" : columns.IsFolder ? "" : "";

    private static TextBlock Chip(string content, string styleKey)
    {
        var chip = new TextBlock { Text = content };
        Apply(chip, styleKey);
        return chip;
    }

    /// <summary>
    /// Applies a named style, and leaves the element inheriting if the key is missing.
    /// </summary>
    /// <remarks>
    /// A <em>style</em>, never a brush. Pulling a brush out of <c>Application.Resources</c> in code does
    /// not consult <c>ResourceDictionary.ThemeDictionaries</c>, so it returns whichever theme's value
    /// happens to be first — which is how the light theme shipped with its file names painted in the
    /// dark theme's near-white, on a near-white surface. A <c>ThemeResource</c> inside a style is
    /// resolved per element, against the theme that element is actually in.
    /// </remarks>
    private static void Apply(FrameworkElement target, string styleKey)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(styleKey, out var value)
            && value is Style style)
        {
            target.Style = style;
        }
    }
}
