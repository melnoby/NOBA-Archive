using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NOBA.Archive.App.Views;

/// <summary>
/// The create screen: choose what to compress, where it goes, and how.
/// </summary>
/// <remarks>
/// <para>
/// No archive logic, and no knowledge of any format. Which formats can be written, whether solid
/// compression means anything, whether encryption is available — every one of those comes from
/// <see cref="CreateViewModel"/>, which asks the provider registry. This file picks files, sets
/// strings on controls, and calls one method.
/// </para>
/// <para>
/// The view model raises notifications from whatever thread the operation is on, so every redraw is
/// marshalled here — the component with the thread affinity pays for it, as everywhere else.
/// </para>
/// </remarks>
public sealed partial class CreatePage : UserControl
{
    /// <summary>What the drop zone is currently saying.</summary>
    private enum DropVisual
    {
        /// <summary>Nothing is being dragged.</summary>
        Idle,

        /// <summary>Something droppable is over the panel.</summary>
        Ready,

        /// <summary>Something is over the panel that cannot become an input.</summary>
        Refusing,
    }

    private ShellViewModel? _shell;
    private int _renderQueued;
    private DropVisual _dropVisual = DropVisual.Idle;

    public CreatePage() => InitializeComponent();

    private CreateViewModel? Model => _shell?.Create;

    public void Bind(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        Model!.PropertyChanged += OnModelChanged;
        Model!.Options.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Render();
        });
    }

    public void ApplyLanguage(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var text = shell.Text;

        PageTitle.Text = text.CreateTitle;
        AddFilesButton.Content = text.CreateAddFiles;
        AddFolderButton.Content = text.CreateAddFolder;
        RemoveButton.Content = text.CreateRemove;
        ClearButton.Content = text.CreateClear;
        EmptyInputs.Text = text.CreateNoInputs;
        DestinationButton.Content = text.CreateChooseDestination;
        NameBox.PlaceholderText = text.CreateName;
        DestinationBox.PlaceholderText = text.CreateDestination;
        SolidBox.Content = text.CreateSolid;
        OverwriteBox.Content = text.CreateOverwrite;
        CreateButton.Content = text.CreateStart;

        RenderFormats(text);
        RenderLevels();
        Render();
    }

    /// <summary>
    /// Refreshes everything the model decides.
    /// </summary>
    /// <remarks>
    /// The button's availability is the model's <c>CanCreate</c>, and the line under it is the reason
    /// it is unavailable. "Disabled with no explanation" teaches a user that the product is arbitrary;
    /// naming the missing piece tells them what to do next.
    /// </remarks>
    public void Render()
    {
        if (_shell is null || Model is null)
        {
            return;
        }

        var text = _shell.Text;

        // Rebuilding the list throws the selection away, and Remove is bound to it. Whichever path was
        // selected is put back by value, so adding a file does not silently disarm the Remove button
        // the user was reaching for.
        var selected = InputList.SelectedItem as string;

        InputList.Items.Clear();
        foreach (var input in Model.Inputs)
        {
            InputList.Items.Add(input);
        }

        if (selected is not null)
        {
            InputList.SelectedItem = InputList.Items.FirstOrDefault(
                item => string.Equals(item as string, selected, StringComparison.Ordinal));
        }

        RenderDropZone(text);
        RemoveButton.IsEnabled = InputList.SelectedItem is not null;
        ClearButton.IsEnabled = Model.HasInputs;

        DestinationBox.Text = Model.DestinationFolder ?? string.Empty;
        OverwriteBox.IsChecked = Model.Overwrite;

        SolidBox.IsEnabled = Model.Options.Solid.IsAvailable;
        SolidBox.IsChecked = Model.Options.IsSolid;
        ToolTipService.SetToolTip(
            SolidBox,
            Model.Options.Solid.IsAvailable ? null : text.UnsupportedByFormat);

        TargetLine.Text = Model.TargetPath is { } target ? $"{text.CreateTargetLabel}: {target}" : string.Empty;
        CreateButton.IsEnabled = Model.CanCreate;
        StatusLine.Text = StatusFor(Model, text);
    }

    /// <summary>
    /// The drop panel: what it says, and how it looks saying it.
    /// </summary>
    /// <remarks>
    /// The hint is over the list whenever a drag is in progress, and otherwise only when there is
    /// nothing to look at. Border and fill move with the state, but they are never the whole message —
    /// the glyph and the sentence change too, so the panel still reads for someone who cannot separate
    /// the two blues.
    /// </remarks>
    private void RenderDropZone(LocalizedStrings text)
    {
        if (Model is null)
        {
            return;
        }

        var dragging = _dropVisual != DropVisual.Idle;
        DropHint.Visibility = dragging || !Model.HasInputs ? Visibility.Visible : Visibility.Collapsed;

        // Transparent while idle over an empty list, so the hint sits on the panel rather than in a
        // box of its own; opaque during a drag, where it has a populated list to cover.
        DropHint.Background = dragging ? ThemeBrush("NobaSurfaceRaised") : null;
        DropZone.BorderBrush = ThemeBrush(_dropVisual switch
        {
            DropVisual.Ready => "NobaBorderFocus",
            DropVisual.Refusing => "NobaBorderStrong",
            _ => "NobaBorderSubtle",
        });
        DropZone.BorderThickness = new Thickness(dragging ? 2 : 1);

        (DropGlyph.Text, DropText.Text) = _dropVisual switch
        {
            // Segoe MDL2: a tray being filled, then a refusal, then a folder at rest.
            DropVisual.Ready => ("\uE896", text.DropActive),
            DropVisual.Refusing => ("\uE711", text.DropInvalid),
            _ => ("\uE8B7", text.DropIdle),
        };

        DropGlyph.Foreground = ThemeBrush(_dropVisual == DropVisual.Ready ? "NobaAccentDefault" : "NobaTextSecondary");
        EmptyInputs.Text = Model.HasInputs ? string.Empty : text.CreateNoInputs;
    }

    private static Brush? ThemeBrush(string key) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    /// <summary>One sentence for the state the screen is in, blocker or outcome.</summary>
    private static string StatusFor(CreateViewModel model, LocalizedStrings text)
    {
        // What just happened outranks what is missing: a user who dropped six items and got four is
        // owed the reason before they are told they still need a destination folder.
        if (model.LastDrop.ReasonFor(text) is { Length: > 0 } dropped)
        {
            return dropped;
        }

        if (model.TargetExists)
        {
            return text.CreateExists;
        }

        var outcome = model.Status switch
        {
            CreateStatus.Completed => text.CreateDone,
            CreateStatus.Cancelled => text.CreateCancelled,
            CreateStatus.Failed => model.FailureDetail is { Length: > 0 } detail
                ? $"{text.CreateFailed}: {detail}"
                : text.CreateFailed,
            _ => null,
        };

        if (outcome is not null)
        {
            return outcome;
        }

        return model.Blocker switch
        {
            CreateBlocker.NoInputs => text.CreateNeedsInputs,
            CreateBlocker.NoDestination => text.CreateNeedsDestination,
            CreateBlocker.NoName => text.CreateNeedsName,
            CreateBlocker.NoCreatableFormat => text.CreateNoFormat,
            CreateBlocker.TooManyInputsForFormat => text.CreateSingleMemberFormat,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The format list, from the engine.
    /// </summary>
    /// <remarks>
    /// Only formats this build can write appear, and the list is asked for rather than written down:
    /// if the native library is missing, 7Z is simply not there. Nothing in this file knows the name
    /// of a format — the display string is the format's own id.
    /// </remarks>
    private void RenderFormats(LocalizedStrings text)
    {
        if (Model is null)
        {
            return;
        }

        FormatBox.Header = text.CreateFormat;
        FormatBox.Items.Clear();

        foreach (var format in Model.Options.CreatableFormats)
        {
            FormatBox.Items.Add(format.Id.ToUpperInvariant());
        }

        if (Model.Options.Format is { } current)
        {
            FormatBox.SelectedIndex = IndexOfFormat(current);
        }
    }

    private int IndexOfFormat(ArchiveFormat format)
    {
        for (var i = 0; i < Model!.Options.CreatableFormats.Count; i++)
        {
            if (Model.Options.CreatableFormats[i] == format)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The compression profiles, named by the presentation layer.
    /// </summary>
    /// <remarks>
    /// Profiles rather than numeric levels: "Fast" and "Smallest size" are choices a person can make,
    /// and 0/1/5/9 is a number they would have to already understand. The numeric level stays in the
    /// model for the day an expert control needs it.
    /// </remarks>
    private void RenderLevels()
    {
        if (Model is null || _shell is null)
        {
            return;
        }

        LevelBox.Header = _shell.Text.CreateLevel;
        LevelBox.Items.Clear();

        foreach (var profile in CreateOptionsViewModel.Profiles)
        {
            LevelBox.Items.Add(CreateOptionsViewModel.NameOf(profile, _shell.Text));
        }

        LevelBox.SelectedIndex = CreateOptionsViewModel.IndexOfProfile(Model.Options.Profile);
    }

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            ((App)Microsoft.UI.Xaml.Application.Current).MainWindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            Model.AddInputs(files.Select(static f => f.Path));
        }
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            ((App)Microsoft.UI.Xaml.Application.Current).MainWindowHandle);

        if (await picker.PickSingleFolderAsync() is StorageFolder folder)
        {
            Model.AddInputs([folder.Path]);
        }
    }

    /// <summary>
    /// Whether something being dragged could become an input.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. This runs continuously while the pointer moves over the panel, and the
    /// only honest question that can be answered without touching the disk is whether the payload
    /// carries filesystem items at all. Everything else — does it still exist, is it already listed,
    /// is it the archive itself — needs the items themselves and is decided on drop, by the view model.
    /// </remarks>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var acceptable = Model is not null && e.DataView.Contains(StandardDataFormats.StorageItems);

        e.AcceptedOperation = acceptable ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = acceptable;
        e.Handled = true;

        SetDropVisual(acceptable ? DropVisual.Ready : DropVisual.Refusing);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetDropVisual(DropVisual.Idle);

    /// <summary>
    /// Turns the payload into paths and hands them to the view model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deferral is not optional. <c>DataView</c> is torn down when this method returns, and this
    /// method returns at the first <c>await</c> — without it the collection is read after Windows has
    /// reclaimed it, which fails as an empty drop on a fast machine and as an exception on a slow one.
    /// </para>
    /// <para>
    /// Nothing is copied, opened or enumerated. <see cref="IStorageItem.Path"/> is a string Windows
    /// already has; an item that has no filesystem path yields an empty one and the view model refuses
    /// it by name. A folder ends the journey here as a single path — the engine walks it later, on its
    /// own thread, where a slow network share cannot hold the window still.
    /// </para>
    /// </remarks>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        SetDropVisual(DropVisual.Idle);

        if (Model is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items is null)
            {
                return;
            }

            // Materialised before it is handed over: the view model must not be given a sequence whose
            // enumeration reaches back into a data package that is about to be released.
            var paths = new List<string?>(items.Count);
            foreach (var item in items)
            {
                paths.Add(item?.Path);
            }

            Model.AcceptDrop(paths);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // Windows could not produce the items — a shell extension that failed, a share that went
            // away mid-drag. The list is untouched and the screen says so; there is nothing here worth
            // showing a user an HRESULT over.
            Model.AcceptDrop([null]);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetDropVisual(DropVisual visual)
    {
        if (_dropVisual == visual)
        {
            return;
        }

        _dropVisual = visual;

        if (_shell is not null)
        {
            RenderDropZone(_shell.Text);
        }
    }

    private void OnInputSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = InputList.SelectedItem is not null;

    private void OnRemoveInput(object sender, RoutedEventArgs e)
    {
        if (InputList.SelectedItem is string path)
        {
            Model?.RemoveInput(path);
        }
    }

    private void OnClearInputs(object sender, RoutedEventArgs e) => Model?.ClearInputs();

    private async void OnChooseDestination(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            ((App)Microsoft.UI.Xaml.Application.Current).MainWindowHandle);

        if (await picker.PickSingleFolderAsync() is StorageFolder folder)
        {
            Model.DestinationFolder = folder.Path;
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.ArchiveName = NameBox.Text;
        }
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is null || FormatBox.SelectedIndex < 0)
        {
            return;
        }

        Model.Options.Format = Model.Options.CreatableFormats[FormatBox.SelectedIndex];
    }

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is not null && LevelBox.SelectedIndex >= 0)
        {
            Model.Options.Profile = CreateOptionsViewModel.Profiles[LevelBox.SelectedIndex];
        }
    }

    private void OnSolidChanged(object sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            Model.Options.IsSolid = SolidBox.IsChecked == true;
        }
    }

    private void OnOverwriteChanged(object sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            Model.Overwrite = OverwriteBox.IsChecked == true;
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            await Model.CreateAsync().ConfigureAwait(true);
        }
    }
}
