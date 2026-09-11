using NOBA.Archive.Core.Actions;
using NOBA.Archive.Presentation.ViewModels;

namespace NOBA.Archive.Presentation.Interaction;

/// <summary>What the window did with a request that arrived from outside it.</summary>
public enum SeedOutcome
{
    /// <summary>Nothing was asked, or the request could not be honoured.</summary>
    Ignored = 0,

    /// <summary>The Create screen is open with the selection loaded, waiting for the person.</summary>
    CreateSeeded,

    /// <summary>The Browse screen is showing the archive.</summary>
    ArchiveOpened,

    /// <summary>The Browse screen is showing the archive, with the extract dialog to follow.</summary>
    ExtractRequested,
}

/// <summary>
/// Applies a request that came from Explorer to the window that just started.
/// </summary>
/// <remarks>
/// <para>
/// The last few inches of "right-click → NOBA Archive → Create archive…". Everything before this is
/// <c>Core.Actions</c>, which knows nothing about screens; everything after is the ordinary Create
/// workflow, which knows nothing about where its inputs came from. This is the joint, and it is
/// deliberately thin — it sets a section and calls <see cref="CreateViewModel.AddInputs"/>, the same
/// method the picker and the drop zone call.
/// </para>
/// <para>
/// <b>It starts nothing.</b> A shell action populates the screen and stops. Compressing from the menu
/// with defaults is a different verb that never opens a window at all; this one exists precisely
/// because the person wanted to choose something first, and pressing Create on their behalf would
/// take that back.
/// </para>
/// </remarks>
public static class ShellRequestSeed
{
    /// <summary>
    /// Loads the request into the shell and returns what the window should now be showing.
    /// </summary>
    /// <remarks>
    /// Duplicate and ordering rules come free: <see cref="CreateViewModel.AddInputs"/> already folds
    /// case and separators, so a selection containing both <c>C:\Docs</c> and <c>C:\Docs\</c> lands as
    /// one input here for the same reason it does when dropped.
    /// </remarks>
    public static SeedOutcome Apply(ShellViewModel shell, ArchiveActionRequest? request)
    {
        ArgumentNullException.ThrowIfNull(shell);

        if (request is null || request.Inputs.Count == 0)
        {
            return SeedOutcome.Ignored;
        }

        switch (request.Action)
        {
            case ArchiveAction.CreateArchive:
                shell.Create.AddInputs(request.InputPaths);
                if (!shell.Create.HasInputs)
                {
                    return SeedOutcome.Ignored;
                }

                shell.Section = ShellSection.Create;
                return SeedOutcome.CreateSeeded;

            case ArchiveAction.OpenInApp:
                if (request.PrimaryInput is not { Length: > 0 })
                {
                    return SeedOutcome.Ignored;
                }

                shell.Section = ShellSection.Browse;
                return SeedOutcome.ArchiveOpened;

            case ArchiveAction.ExtractTo:
                if (request.PrimaryInput is not { Length: > 0 })
                {
                    return SeedOutcome.Ignored;
                }

                // Browse first, then the existing extract dialog: the person is about to be asked for
                // a destination and a conflict policy, and answering that about an archive they have
                // not seen the contents of is answering blind.
                shell.Section = ShellSection.Browse;
                return SeedOutcome.ExtractRequested;

            // Test, Extract here and Compress to never reach a window. They are work with nothing to
            // ask, and they run through the CLI so that a right-click does not steal focus.
            default:
                return SeedOutcome.Ignored;
        }
    }
}
