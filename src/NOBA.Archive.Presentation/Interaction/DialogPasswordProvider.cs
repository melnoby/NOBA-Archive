using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Interaction;

/// <summary>
/// The desktop host's <see cref="IPasswordProvider"/>: asks the person, through a dialog.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddNobaArchiveEngine</c> registers a password provider only for a headless host, and that is a
/// deliberate refusal rather than an omission — there is no generic way to ask a person a question, so
/// the engine will not invent one. Supplying this is the desktop host's half of the startup contract,
/// and <c>DHC-003</c> exists because the first time we did not supply it, the window simply never
/// appeared.
/// </para>
/// <para>
/// Nothing here decides anything about the archive. It turns a <see cref="PasswordRequest"/> into
/// localised, direction-isolated text, shows it, and turns what comes back into a
/// <see cref="Password"/>. Whether that password is correct, how many attempts are allowed, and what a
/// refusal means are all questions the engine already answers.
/// </para>
/// </remarks>
public sealed class DialogPasswordProvider : IPasswordProvider
{
    private readonly IPasswordDialog _dialog;
    private readonly Func<UiContext> _context;

    public DialogPasswordProvider(IPasswordDialog dialog, Func<UiContext> context)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(context);

        _dialog = dialog;
        _context = context;
    }

    public async ValueTask<Password?> GetPasswordAsync(
        PasswordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ui = _context();
        var text = ui.Text;

        var prompt = new PasswordPrompt(
            Title: text.PasswordTitle,
            Body: request.EntryPath is null ? text.PasswordBody : text.PasswordEntryBody,

            // Isolated as LTR: an archive called "تقرير ‎2024\report.7z" is still a path, and its
            // separators still run left to right. This is the string the person judges before typing a
            // secret into the window above it.
            ArchiveLine: BidiText.IsolateAsLtr(request.ArchiveDisplayName),
            EntryLine: request.EntryPath is null ? null : BidiText.IsolateAsLtr(request.EntryPath),
            RetryWarning: request.IsRetry ? text.PasswordRetry : null,
            FieldLabel: text.PasswordField,
            RevealLabel: text.PasswordReveal,
            SubmitLabel: text.PasswordSubmit,
            CancelLabel: text.PasswordCancel,
            IsRightToLeft: ui.IsRightToLeft,
            FontFamily: ui.FontFamily);

        var typed = await _dialog.ShowAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (typed is null)
        {
            return null;
        }

        try
        {
            // An empty box is not a password. Null is the contract's word for "none available", which
            // is the truth here; a zero-length key would be a different and worse answer.
            return typed.Length == 0 ? null : Password.FromChars(typed);
        }
        finally
        {
            Array.Clear(typed);
        }
    }
}
