using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Interaction;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUiFlowDirection = Microsoft.UI.Xaml.FlowDirection;

namespace NOBA.Archive.App.Dialogs;

/// <summary>
/// Asks where an archive should be extracted, and what happens to files already there.
/// </summary>
/// <remarks>
/// <para>
/// This is the one moment before anything is written, so it is where both decisions belong. Previously
/// the folder picker appeared alone and extraction began the instant it closed: the destination was
/// never shown back, the conflict policy was never asked, and a person who picked the wrong folder found
/// out from the operation strip while it was already writing.
/// </para>
/// <para>
/// It decides nothing. The wording, the direction, the list of policies and which one starts selected
/// are all <see cref="ExtractPlanner"/>'s, in a project that can be tested where WinUI cannot be built.
/// This file shows strings, opens a picker, and reports the choice.
/// </para>
/// </remarks>
public sealed partial class ExtractDialog : ContentDialog
{
    private readonly ExtractPrompt _prompt;
    private readonly nint _ownerWindow;
    private string? _destination;

    public ExtractDialog(ExtractPrompt prompt, nint ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InitializeComponent();

        _prompt = prompt;
        _ownerWindow = ownerWindow;

        FlowDirection = prompt.IsRightToLeft ? WinUiFlowDirection.RightToLeft : WinUiFlowDirection.LeftToRight;
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(prompt.FontFamily);

        Title = prompt.Title;
        PrimaryButtonText = prompt.ExtractLabel;
        CloseButtonText = prompt.CancelLabel;

        BodyText.Text = prompt.Body;
        ArchiveLabel.Text = prompt.ArchiveLabel;
        ArchiveText.Text = prompt.Archive;
        ScopeLabel.Text = prompt.ScopeLabel;
        ScopeText.Text = prompt.Scope;

        DestinationLabel.Text = prompt.DestinationLabel;
        ChooseButton.Content = prompt.ChooseLabel;

        ConflictLabel.Text = prompt.ConflictLabel;
        ConflictHint.Text = prompt.ConflictHint;

        foreach (var choice in prompt.ConflictChoices)
        {
            ConflictBox.Items.Add(choice.Label);
        }

        ConflictBox.SelectedIndex = prompt.SelectedConflictIndex;

        _destination = prompt.Destination;
        RenderDestination();
    }

    /// <summary>
    /// Where the files go and what happens on a collision, or <c>null</c> if no folder was chosen.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess. There is no sensible fallback destination — extracting somewhere the
    /// person did not name is the failure this dialog exists to prevent.
    /// </remarks>
    public ExtractChoice? Choice =>
        _destination is { Length: > 0 } destination
            ? new ExtractChoice(destination, SelectedPolicy)
            : null;

    private ConflictPolicy SelectedPolicy =>
        ConflictBox.SelectedIndex >= 0 && ConflictBox.SelectedIndex < _prompt.ConflictChoices.Count
            ? _prompt.ConflictChoices[ConflictBox.SelectedIndex].Policy
            : ExtractPlanner.Default;

    private void RenderDestination()
    {
        DestinationText.Text = _destination is { Length: > 0 } destination
            ? destination
            : _prompt.NoDestinationLabel;

        // Nothing can be extracted to nowhere, and the button says so rather than accepting a click
        // that would have to be refused a moment later.
        IsPrimaryButtonEnabled = _destination is { Length: > 0 };
    }

    private async void OnChooseDestination(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);

        if (await picker.PickSingleFolderAsync() is StorageFolder folder)
        {
            _destination = folder.Path;
            RenderDestination();
        }
    }
}
