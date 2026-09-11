namespace NOBA.Archive.Core.Actions;

/// <summary>
/// Reads back what <see cref="ShellLauncher"/> wrote.
/// </summary>
/// <remarks>
/// <para>
/// The application half of the shell bridge. The menu produced an argument list; the process that
/// starts receives it and has to recover the request. Both directions live next to each other on
/// purpose — a launcher and a parser maintained in different projects drift, and the symptom is a
/// menu entry that silently opens an empty window.
/// </para>
/// <para>
/// <b>Only the application's own verbs are recognised.</b> <c>--open</c>, <c>--extract</c> and
/// <c>--create</c> are switches; anything else is not a shell launch and is refused rather than
/// guessed at. A user who double-clicks the executable, or a shortcut with a stray argument, must get
/// the ordinary window and not a half-populated one.
/// </para>
/// <para>
/// Nothing here validates a path. The arguments came from this machine's own shell, but "came from
/// the shell" is not "is safe": every path still passes the path policy and ResourceGuard when the
/// operation runs. What this refuses is malformed <em>shape</em> — a verb with no paths, a switch it
/// does not know — because a request that cannot be honoured should not be built.
/// </para>
/// </remarks>
public static class ShellCommandLine
{
    /// <summary>
    /// The request an argument list describes, or null when it does not describe one.
    /// </summary>
    /// <param name="args">
    /// The process arguments <b>without</b> the executable path. Callers reading
    /// <c>Environment.GetCommandLineArgs()</c> must skip its first element.
    /// </param>
    public static ArchiveActionRequest? Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count < 2)
        {
            return null;
        }

        var verb = args[0];
        ShellVerb? matched = null;
        foreach (var candidate in ShellRegistration.Verbs)
        {
            if (candidate.Host == ShellHost.Application
                && string.Equals(candidate.Verb, verb, StringComparison.Ordinal))
            {
                matched = candidate;
                break;
            }
        }

        if (matched is null)
        {
            return null;
        }

        var inputs = new List<ShellItem>();
        for (var i = 1; i < args.Count; i++)
        {
            var value = args[i];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // A second switch after the verb is not something this contract produces. Refusing keeps
            // the parser from ever treating an option as a path.
            if (value.StartsWith('-'))
            {
                return null;
            }

            // A command line carries paths and nothing else, so the directory flag cannot be known
            // here and is recorded as false rather than probed - ARC-061 forbids the probe, and the
            // flag is unreadable on this path anyway: every application-hosted action's command shape
            // is paths-only, so nothing downstream can consult it. SHELL-078 asserts that, so this
            // stays a fact about the shapes rather than a promise in a comment.
            inputs.Add(ShellItem.For(value, isDirectory: false));
        }

        if (inputs.Count == 0)
        {
            return null;
        }

        // Open and Extract to act on one archive. Extra paths mean the caller was not this menu.
        if (matched.Action is ArchiveAction.OpenInApp or ArchiveAction.ExtractTo && inputs.Count != 1)
        {
            return null;
        }

        return new ArchiveActionRequest
        {
            Action = matched.Action,
            Inputs = inputs,
        };
    }
}
