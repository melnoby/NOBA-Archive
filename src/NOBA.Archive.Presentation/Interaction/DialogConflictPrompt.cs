using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Interaction;

/// <summary>
/// The desktop host's <see cref="IConflictPrompt"/>: asks the person, through a dialog.
/// </summary>
/// <remarks>
/// <para>
/// The prompt only reports what the person chose. Latching an "apply to all" answer across the rest of
/// the operation, generating a non-colliding name for <see cref="ConflictAction.Rename"/>, and deciding
/// when to ask at all remain in <c>IConflictResolutionService</c>, which is where they were already
/// tested.
/// </para>
/// <para>
/// A dismissed dialog cancels the operation. It must never mean <see cref="ConflictAction.Skip"/>:
/// skipping is the one outcome a person cannot notice afterwards — the extraction reports success and
/// the file is quietly not there. Cancelling is loud, and loud is correct when we do not know the
/// answer.
/// </para>
/// </remarks>
public sealed class DialogConflictPrompt : IConflictPrompt
{
    private readonly IConflictDialog _dialog;
    private readonly Func<UiContext> _context;

    public DialogConflictPrompt(IConflictDialog dialog, Func<UiContext> context)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(context);

        _dialog = dialog;
        _context = context;
    }

    public async ValueTask<ConflictDecision> ResolveAsync(
        ConflictRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ui = _context();
        var text = ui.Text;
        var culture = text.Language.Culture;

        var prompt = new ConflictPrompt(
            Title: text.ConflictTitle,
            Body: text.ConflictBody,
            PathLabel: text.ConflictPath,
            Path: BidiText.IsolateAsLtr(request.DestinationPath),
            ExistingLabel: text.ConflictExisting,
            ExistingSize: BidiText.IsolateAsLtr(request.ExistingSize.ToDisplayString(culture)),
            IncomingLabel: text.ConflictIncoming,
            IncomingSize: BidiText.IsolateAsLtr(request.IncomingSize.ToDisplayString(culture)),
            OverwriteLabel: text.ConflictOverwrite,
            SkipLabel: text.ConflictSkip,
            RenameLabel: text.ConflictRename,
            CancelLabel: text.ConflictCancel,
            ContinueLabel: text.ConflictContinue,
            ApplyToAllLabel: text.ConflictApplyToAll,
            IsRightToLeft: ui.IsRightToLeft,
            FontFamily: ui.FontFamily);

        var decision = await _dialog.ShowAsync(prompt, cancellationToken).ConfigureAwait(false);

        return decision ?? ConflictDecision.Once(ConflictAction.Cancel);
    }
}
