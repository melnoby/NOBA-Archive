using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Presentation.Interaction;
using WinUiFlowDirection = Microsoft.UI.Xaml.FlowDirection;

namespace NOBA.Archive.App.Dialogs;

/// <summary>
/// Asks for a new name for one entry.
/// </summary>
/// <remarks>
/// Every string arrives finished in <see cref="RenamePrompt"/>, including the current name the field
/// is seeded with — the person is retyping one path segment, not starting from a blank box, and the
/// text is pre-selected so a short name can be replaced in one keystroke rather than cleared first.
/// </remarks>
public sealed partial class RenameDialog : ContentDialog
{
    public RenameDialog(RenamePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InitializeComponent();

        FlowDirection = prompt.IsRightToLeft ? WinUiFlowDirection.RightToLeft : WinUiFlowDirection.LeftToRight;
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(prompt.FontFamily);

        Title = prompt.Title;
        PrimaryButtonText = prompt.ConfirmLabel;
        CloseButtonText = prompt.CancelLabel;

        BodyText.Text = prompt.Body;
        FieldLabel.Text = prompt.FieldLabel;
        NameField.Text = prompt.CurrentName;

        IsPrimaryButtonEnabled = NameField.Text.Length > 0;

        Opened += (_, _) =>
        {
            NameField.Focus(FocusState.Programmatic);
            NameField.SelectAll();
        };
    }

    /// <summary>The typed name, or <c>null</c> if the person did not confirm.</summary>
    public string? NewName { get; private set; }

    /// <summary>Takes the typed name. Called once, by the host, only on the primary button.</summary>
    public string Confirm()
    {
        NewName = NameField.Text;
        return NewName;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) =>
        IsPrimaryButtonEnabled = NameField.Text.Length > 0;
}
