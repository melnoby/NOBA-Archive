using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Interaction;

/// <summary>
/// What the shell looks like at the moment a question has to be asked.
/// </summary>
/// <remarks>
/// Supplied as a function rather than captured once, because a dialog can be raised at any point in a
/// long operation and the user may have changed language or theme since it started. A prompt in the
/// wrong direction is not a cosmetic defect: an RTL window that suddenly shows an LTR modal reads as a
/// different application, which is exactly the moment a person should not be uncertain.
/// </remarks>
public sealed record UiContext(LocalizedStrings Text, bool IsRightToLeft, string FontFamily);

/// <summary>
/// Everything the password dialog renders, already localised, already direction-isolated.
/// </summary>
/// <remarks>
/// The dialog is handed finished strings on purpose. It decides nothing — not the wording, not the
/// direction, not whether this is a retry — so all of that stays in a project that can be tested where
/// WinUI cannot even be restored.
/// </remarks>
public sealed record PasswordPrompt(
    string Title,
    string Body,
    string ArchiveLine,
    string? EntryLine,
    string? RetryWarning,
    string FieldLabel,
    string RevealLabel,
    string SubmitLabel,
    string CancelLabel,
    bool IsRightToLeft,
    string FontFamily);

/// <summary>
/// The mechanism for asking a person for a password. Implemented by the UI, nowhere else.
/// </summary>
public interface IPasswordDialog
{
    /// <summary>
    /// Shows the prompt and returns the characters typed, or <c>null</c> if the person cancelled.
    /// </summary>
    /// <remarks>
    /// Characters, not a string: the caller zeroes the array the moment it has been copied into a
    /// <see cref="Core.Primitives.Password"/>. A <see cref="string"/> could not be zeroed at all.
    /// </remarks>
    ValueTask<char[]?> ShowAsync(PasswordPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Everything the conflict dialog renders, already localised and direction-isolated.</summary>
public sealed record ConflictPrompt(
    string Title,
    string Body,
    string PathLabel,
    string Path,
    string ExistingLabel,
    string ExistingSize,
    string IncomingLabel,
    string IncomingSize,
    string OverwriteLabel,
    string SkipLabel,
    string RenameLabel,
    string CancelLabel,
    string ContinueLabel,
    string ApplyToAllLabel,
    bool IsRightToLeft,
    string FontFamily);

/// <summary>The mechanism for asking a person what to do about an existing file.</summary>
public interface IConflictDialog
{
    /// <summary>
    /// Shows the prompt and returns the person's decision, or <c>null</c> if the dialog was dismissed
    /// without one being made.
    /// </summary>
    ValueTask<ConflictDecision?> ShowAsync(ConflictPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Everything the extract dialog renders, already localised and direction-isolated.</summary>
/// <remarks>
/// The conflict choices arrive as a list rather than as five labelled fields so the dialog cannot show
/// a policy the product did not offer, and cannot silently drop one that was added. Each carries the
/// <see cref="ConflictPolicy"/> it stands for, so the dialog never maps a label back to a meaning.
/// </remarks>
public sealed record ExtractPrompt(
    string Title,
    string Body,
    string ArchiveLabel,
    string Archive,
    string ScopeLabel,
    string Scope,
    string DestinationLabel,
    string? Destination,
    string NoDestinationLabel,
    string ChooseLabel,
    string ConflictLabel,
    IReadOnlyList<ConflictChoice> ConflictChoices,
    int SelectedConflictIndex,
    string ConflictHint,
    string ExtractLabel,
    string CancelLabel,
    bool IsRightToLeft,
    string FontFamily);

/// <summary>One conflict policy, with the words that describe it.</summary>
public sealed record ConflictChoice(ConflictPolicy Policy, string Label);

/// <summary>What the person chose: where it goes, and what happens on a collision.</summary>
public sealed record ExtractChoice(string Destination, ConflictPolicy ConflictPolicy);

/// <summary>The mechanism for asking a person where and how to extract.</summary>
public interface IExtractDialog
{
    /// <summary>
    /// Shows the prompt and returns the choice, or <c>null</c> if the person backed out.
    /// </summary>
    /// <remarks>
    /// Null is a cancellation, never a default: an extraction that begins because a dialog was
    /// dismissed is an extraction nobody asked for, into a folder nobody chose.
    /// </remarks>
    ValueTask<ExtractChoice?> ShowAsync(ExtractPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Everything a yes/no confirmation renders, already localised and direction-isolated.</summary>
/// <remarks>
/// One shape for every destructive confirmation this build asks for, rather than one dialog per verb.
/// Removing entries from an archive is the first caller, and it is not a special case: it rewrites a
/// real file on disk the moment the person agrees, which is exactly the kind of action this product
/// otherwise never takes without an explicit decision — the conflict dialog and the overwrite checkbox
/// both exist for the same reason.
/// </remarks>
public sealed record ConfirmPrompt(
    string Title,
    string Body,
    string ConfirmLabel,
    string CancelLabel,
    bool IsRightToLeft,
    string FontFamily);

/// <summary>The mechanism for asking a person to confirm a destructive action.</summary>
public interface IConfirmDialog
{
    /// <summary>Shows the prompt and returns whether the person confirmed.</summary>
    ValueTask<bool> ShowAsync(ConfirmPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Everything the rename dialog renders, already localised and direction-isolated.</summary>
public sealed record RenamePrompt(
    string Title,
    string Body,
    string FieldLabel,
    string CurrentName,
    string ConfirmLabel,
    string CancelLabel,
    bool IsRightToLeft,
    string FontFamily);

/// <summary>The mechanism for asking a person for a new name.</summary>
public interface IRenameDialog
{
    /// <summary>
    /// Shows the prompt and returns the new name, or <c>null</c> if the person backed out.
    /// </summary>
    /// <remarks>
    /// Returned exactly as typed, with no trimming or validation applied here: whether a name can be
    /// represented inside the archive is the engine's question, the same way a dropped path is never
    /// judged by the screen that received it.
    /// </remarks>
    ValueTask<string?> ShowAsync(RenamePrompt prompt, CancellationToken cancellationToken);
}
