using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Presentation.Interaction;
using WinUiFlowDirection = Microsoft.UI.Xaml.FlowDirection;

namespace NOBA.Archive.App.Dialogs;

/// <summary>
/// Asks for an archive password.
/// </summary>
/// <remarks>
/// <para>
/// Every string, and the direction they are laid out in, arrive finished in <see cref="PasswordPrompt"/>.
/// This file chooses nothing: it is the mechanism by which a person can answer, which is the only part
/// of the exchange that genuinely requires WinUI.
/// </para>
/// <para>
/// The password leaves as a <c>char[]</c> the caller zeroes. WinUI's <see cref="PasswordBox"/> exposes
/// its value only as a <see cref="string"/>, and a string cannot be zeroed — that copy stays in the heap
/// until a collection that may never come. It is a real limitation of the control, stated here rather
/// than papered over, and the reason the value is copied out and the box cleared immediately.
/// </para>
/// </remarks>
public sealed partial class PasswordDialog : ContentDialog
{
    public PasswordDialog(PasswordPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InitializeComponent();

        FlowDirection = prompt.IsRightToLeft ? WinUiFlowDirection.RightToLeft : WinUiFlowDirection.LeftToRight;
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(prompt.FontFamily);

        Title = prompt.Title;

        // DefaultButton is Primary in the markup, so Enter submits without a key handler of our own.
        PrimaryButtonText = prompt.SubmitLabel;
        CloseButtonText = prompt.CancelLabel;

        BodyText.Text = prompt.Body;
        ArchiveText.Text = prompt.ArchiveLine;

        if (prompt.EntryLine is { } entry)
        {
            EntryText.Text = entry;
            EntryText.Visibility = Visibility.Visible;
        }

        if (prompt.RetryWarning is { } retry)
        {
            RetryBar.Message = retry;
            RetryBar.IsOpen = true;
        }

        FieldLabel.Text = prompt.FieldLabel;
        RevealBox.Content = prompt.RevealLabel;

        Opened += (_, _) => PasswordField.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Takes the typed characters and clears the box behind them.
    /// </summary>
    /// <remarks>
    /// Called once, by the host, only when the person pressed the primary button. Calling it again
    /// returns an empty array, which is the honest answer once the box has been cleared.
    /// </remarks>
    public char[] TakePassword()
    {
        var typed = PasswordField.Password;
        PasswordField.Password = string.Empty;
        return typed.ToCharArray();
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e) =>
        PasswordField.PasswordRevealMode = RevealBox.IsChecked == true
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;
}
