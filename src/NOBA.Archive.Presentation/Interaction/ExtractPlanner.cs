using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Interaction;

/// <summary>
/// Builds the extract prompt, and names the product's conflict policy.
/// </summary>
/// <remarks>
/// <para>
/// The policy list lives here, once. A GUI dialog, <c>noba-ar --conflict</c> and a future
/// "NOBA → Extract to…" shell verb are three ways of asking the same question, and the failure mode
/// worth designing against is the one where they drift: the menu offers something the CLI cannot do,
/// or the dialog quietly defaults to Overwrite while the CLI refuses. Every surface reads
/// <see cref="Policies"/> and hands the chosen <see cref="ConflictPolicy"/> to the same seam.
/// </para>
/// <para>
/// This is presentation, not policy enforcement: what each value <em>does</em> is the extraction
/// writer's business and is tested there. What this decides is which ones a person is offered, in what
/// order, and which one is selected before they touch anything.
/// </para>
/// </remarks>
public static class ExtractPlanner
{
    /// <summary>
    /// What happens when the destination already holds the file, in the order it is offered.
    /// </summary>
    /// <remarks>
    /// <see cref="ConflictPolicy.Ask"/> is first and is the default, and that ordering is the product
    /// decision: the safe answer is the one a person gets by not choosing. Overwrite sits in the middle
    /// rather than at the top for the same reason — the destructive option should never be the one under
    /// the cursor when the dialog opens.
    /// </remarks>
    public static IReadOnlyList<ConflictPolicy> Policies { get; } =
    [
        ConflictPolicy.Ask,
        ConflictPolicy.Skip,
        ConflictPolicy.Rename,
        ConflictPolicy.Overwrite,
        ConflictPolicy.Fail,
    ];

    /// <summary>The policy an extraction uses when nobody has said otherwise.</summary>
    public static ConflictPolicy Default => ConflictPolicy.Ask;

    public static string NameOf(ConflictPolicy policy, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return policy switch
        {
            ConflictPolicy.Ask => text.ExtractConflictAsk,
            ConflictPolicy.Skip => text.ExtractConflictSkip,
            ConflictPolicy.Rename => text.ExtractConflictRename,
            ConflictPolicy.Overwrite => text.ExtractConflictOverwrite,
            ConflictPolicy.Fail => text.ExtractConflictFail,
            _ => text.ExtractConflictAsk,
        };
    }

    public static int IndexOf(ConflictPolicy policy)
    {
        for (var i = 0; i < Policies.Count; i++)
        {
            if (Policies[i] == policy)
            {
                return i;
            }
        }

        return IndexOf(Default);
    }

    /// <summary>
    /// Everything the dialog shows, finished.
    /// </summary>
    /// <remarks>
    /// The archive name and the destination path are direction-isolated: both are technical values that
    /// may sit inside an Arabic sentence, and an un-isolated Windows path reorders on screen into
    /// something that is not the path.
    /// </remarks>
    public static ExtractPrompt Build(
        LocalizedStrings text,
        string archiveDisplayName,
        bool selectionOnly,
        string? destination,
        ConflictPolicy policy,
        bool isRightToLeft,
        string fontFamily)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(archiveDisplayName);

        return new ExtractPrompt(
            Title: text.ExtractTitle,
            Body: text.ExtractBody,
            ArchiveLabel: text.ExtractArchiveLabel,
            Archive: BidiText.IsolateAsLtr(archiveDisplayName),
            ScopeLabel: text.ExtractScopeLabel,
            Scope: selectionOnly ? text.ExtractScopeSelection : text.ExtractScopeAll,
            DestinationLabel: text.ExtractDestinationLabel,
            Destination: destination is { Length: > 0 } ? BidiText.IsolateAsLtr(destination) : null,
            NoDestinationLabel: text.ExtractNoDestination,
            ChooseLabel: text.ExtractChooseDestination,
            ConflictLabel: text.ExtractConflictLabel,
            ConflictChoices: [.. Policies.Select(p => new ConflictChoice(p, NameOf(p, text)))],
            SelectedConflictIndex: IndexOf(policy),
            ConflictHint: text.ExtractConflictHint,
            ExtractLabel: text.ExtractStart,
            CancelLabel: text.ConflictCancel,
            IsRightToLeft: isRightToLeft,
            FontFamily: fontFamily);
    }
}
