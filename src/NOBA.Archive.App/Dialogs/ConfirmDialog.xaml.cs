using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Presentation.Interaction;
using WinUiFlowDirection = Microsoft.UI.Xaml.FlowDirection;

namespace NOBA.Archive.App.Dialogs;

/// <summary>
/// Asks a person to confirm something that cannot be undone from the screen.
/// </summary>
/// <remarks>
/// One dialog for every destructive confirmation this build asks for, rather than one per verb. Every
/// string and the direction they lay out in arrive finished in <see cref="ConfirmPrompt"/>; this file
/// chooses nothing, which is the same discipline <see cref="PasswordDialog"/> and
/// <see cref="ConflictDialog"/> already hold to.
/// </remarks>
public sealed partial class ConfirmDialog : ContentDialog
{
    public ConfirmDialog(ConfirmPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InitializeComponent();

        FlowDirection = prompt.IsRightToLeft ? WinUiFlowDirection.RightToLeft : WinUiFlowDirection.LeftToRight;
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(prompt.FontFamily);

        Title = prompt.Title;
        PrimaryButtonText = prompt.ConfirmLabel;
        CloseButtonText = prompt.CancelLabel;

        BodyText.Text = prompt.Body;
    }

    /// <summary>True only when the person pressed the primary button.</summary>
    public bool Confirmed(ContentDialogResult result) => result == ContentDialogResult.Primary;
}
