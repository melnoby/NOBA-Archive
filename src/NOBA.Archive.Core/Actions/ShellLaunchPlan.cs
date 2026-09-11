namespace NOBA.Archive.Core.Actions;

/// <summary>
/// How to start the work: one executable, and an argument list.
/// </summary>
/// <param name="Executable">A file name to be resolved against the install directory. Never a command line.</param>
/// <param name="Arguments">
/// Already separated. Each element is passed as one argument, so a path containing a space, a quote or
/// an ampersand is one argument containing those characters — not two arguments and an injection.
/// </param>
public sealed record ShellLaunchPlan(string Executable, IReadOnlyList<string> Arguments);

/// <summary>
/// Turns a request into a launch, structurally.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no string to escape, because there is no string.</b> The shell integration is an
/// attack surface whose inputs are filenames a hostile party may have chosen, and the classic failure
/// is a menu handler that builds <c>cmd /c "noba-ar extract " + path</c>. A file named
/// <c>holiday" &amp; del /q *.*.zip</c> is a legal Windows filename and, through a command string, is
/// a command. So this produces a list, the caller hands the list to the process API as a list, and
/// <c>cmd.exe</c> and PowerShell never appear at all.
/// </para>
/// <para>
/// <c>SHELL-024</c> holds the other half: whichever executable is started, the work reaches the same
/// <c>IArchiveWorkspace</c> and the same <c>OperationQueue</c>. This chooses a front door; it does not
/// choose an engine, and there is only one to choose.
/// </para>
/// </remarks>
public static class ShellLauncher
{
    /// <summary>
    /// The plan for a request, or null when the request cannot be carried out as described.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null rather than a best guess. A compress request with no format, or an extract with no
    /// archive, is a caller mistake — and inventing a default here would put a policy decision in the
    /// one place that is meant to have none.
    /// </para>
    /// <para>
    /// <b>The shape comes from <see cref="ShellInvocations"/>, not from here.</b> This method fills
    /// slots; it does not decide which slots exist. That is what stops a second command builder from
    /// ever growing — the registration reads the same table and cannot disagree with it.
    /// </para>
    /// </remarks>
    public static ShellLaunchPlan? PlanFor(ArchiveActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Inputs.Count == 0 || ShellInvocations.For(request.Action) is not { } template)
        {
            return null;
        }

        foreach (var input in request.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return null;
            }
        }

        var arguments = new List<string>(template.Arguments.Count);

        foreach (var argument in template.Arguments)
        {
            switch (argument.Kind)
            {
                case ShellArgumentKind.Literal:
                    if (argument.Text is not { Length: > 0 } word)
                    {
                        return null;
                    }

                    arguments.Add(word);
                    break;

                case ShellArgumentKind.ArchivePath:
                    arguments.Add(request.Inputs[0].Path);
                    break;

                case ShellArgumentKind.SelectedPaths:
                    arguments.AddRange(request.InputPaths);
                    break;

                case ShellArgumentKind.ContainingFolder:
                    if (request.Destination is not { Length: > 0 } folder)
                    {
                        return null;
                    }

                    arguments.Add(folder);
                    break;

                case ShellArgumentKind.SuggestedTarget:
                    if (request.Format is not { } target || request.Destination is not { Length: > 0 } into)
                    {
                        return null;
                    }

                    arguments.Add(SuggestedArchivePath(request, into, target.PrimaryExtension));
                    break;

                case ShellArgumentKind.FormatId:
                    if (request.Format is not { } format)
                    {
                        return null;
                    }

                    arguments.Add(format.Id);
                    break;

                default:
                    return null;
            }
        }

        return new ShellLaunchPlan(template.Executable, arguments);
    }

    /// <summary>
    /// What a quick "Compress to…" names its archive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One item takes its own name; several take the name of the folder they are in, which is what
    /// every file manager does and what a person expects to find afterwards.
    /// </para>
    /// <para>
    /// <b>It does not avoid a collision.</b> If that name is taken, the engine refuses the publication
    /// and the existing overwrite contract decides what happens next — the same contract the Create
    /// screen and <c>noba-ar create</c> use. Inventing "name (2).7z" here would be a second, quieter
    /// overwrite policy living in the menu, and the one place a silent overwrite must never be
    /// possible is the one the user reached in a single click.
    /// </para>
    /// </remarks>
    public static string SuggestedArchivePath(
        ArchiveActionRequest request,
        string folder,
        string extension)
    {
        var only = request.Inputs.Count == 1 ? request.Inputs[0] : null;
        var stem = only is not null ? LeafName(only.Path) : LeafName(folder);

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "archive";
        }

        // A single file keeps its base name: report.pdf becomes report.7z, not report.pdf.7z.
        //
        // A single FOLDER keeps all of its name, because "Project.Alpha" is not "Project" with an
        // extension - and this used to strip it anyway, so "Project.Alpha" and "Project.Beta"
        // proposed the same "Project.zip" and the second was refused as already existing (SHELL-075).
        // The distinction is read from the item, which Explorer supplied; nothing is probed and
        // nothing is inferred from the name.
        if (only is { IsDirectory: false })
        {
            var dot = stem.LastIndexOf('.');
            if (dot > 0)
            {
                stem = stem[..dot];
            }
        }

        if (folder.EndsWith('\\') || folder.EndsWith('/'))
        {
            return folder + stem + extension;
        }

        // Join with the separator the folder is already written in, the way
        // <see cref="ShellMenuBuilder.ContainingFolder"/> already tolerates both. A Windows path keeps
        // its backslash, which is every path this reaches in production; a folder written with forward
        // slashes does not acquire a stray backslash in the middle of it.
        var separator = folder.Contains('\\', StringComparison.Ordinal) || !folder.Contains('/', StringComparison.Ordinal)
            ? '\\'
            : '/';

        return folder + separator + stem + extension;
    }

    private static string LeafName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
    }
}
