using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Interaction;
using WinUiFlowDirection = Microsoft.UI.Xaml.FlowDirection;

namespace NOBA.Archive.App.Dialogs;

/// <summary>
/// Asks what to do about a file that is already there.
/// </summary>
/// <remarks>
/// <para>
/// The three outcomes are radio buttons rather than dialog buttons because a <see cref="ContentDialog"/>
/// offers three buttons in total, and using all three for choices would leave cancelling with nowhere to
/// live but the dismiss gesture. Silence would then mean "cancel" — invisible, and unguessable. Here the
/// choices are in the content, Continue and Cancel are the buttons, and every outcome has a name on
/// screen.
/// </para>
/// <para>
/// Nothing is preselected and Continue starts disabled. A preselected radio button is this dialog making
/// the decision and presenting it as the person's.
/// </para>
/// </remarks>
public sealed partial class ConflictDialog : ContentDialog
{
    public ConflictDialog(ConflictPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InitializeComponent();

        FlowDirection = prompt.IsRightToLeft ? WinUiFlowDirection.RightToLeft : WinUiFlowDirection.LeftToRight;
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(prompt.FontFamily);

        Title = prompt.Title;
        PrimaryButtonText = prompt.ContinueLabel;
        CloseButtonText = prompt.CancelLabel;
        IsPrimaryButtonEnabled = false;

        BodyText.Text = prompt.Body;
        PathLabel.Text = prompt.PathLabel;
        PathText.Text = prompt.Path;

        ExistingLabel.Text = prompt.ExistingLabel;
        ExistingSize.Text = prompt.ExistingSize;
        IncomingLabel.Text = prompt.IncomingLabel;
        IncomingSize.Text = prompt.IncomingSize;

        OverwriteChoice.Content = prompt.OverwriteLabel;
        RenameChoice.Content = prompt.RenameLabel;
        SkipChoice.Content = prompt.SkipLabel;
        ApplyToAllBox.Content = prompt.ApplyToAllLabel;
    }

    /// <summary>
    /// What the person chose, or <c>null</c> if they made no choice.
    /// </summary>
    /// <remarks>
    /// Null when the dialog was dismissed or cancelled. The adapter turns that into
    /// <see cref="ConflictAction.Cancel"/>; it is deliberately not turned into Skip anywhere.
    /// </remarks>
    public ConflictDecision? Decision
    {
        get
        {
            var action = SelectedAction;
            return action is null ? null : new ConflictDecision(action.Value, ApplyToAllBox.IsChecked == true);
        }
    }

    private ConflictAction? SelectedAction
    {
        get
        {
            if (OverwriteChoice.IsChecked == true)
            {
                return ConflictAction.Overwrite;
            }

            if (RenameChoice.IsChecked == true)
            {
                return ConflictAction.Rename;
            }

            return SkipChoice.IsChecked == true ? ConflictAction.Skip : null;
        }
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) => IsPrimaryButtonEnabled = true;
}
