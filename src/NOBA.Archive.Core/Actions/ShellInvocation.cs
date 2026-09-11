using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Core.Actions;

/// <summary>One position in a command line, and what fills it.</summary>
public enum ShellArgumentKind
{
    /// <summary>A fixed word — a verb, a switch.</summary>
    Literal = 0,

    /// <summary>The one selected archive.</summary>
    ArchivePath,

    /// <summary>Every selected path. Expands to as many arguments as were selected.</summary>
    SelectedPaths,

    /// <summary>The folder holding the selected archive.</summary>
    ContainingFolder,

    /// <summary>Where a quick compress writes its archive.</summary>
    SuggestedTarget,

    /// <summary>The chosen format's id.</summary>
    FormatId,
}

/// <summary>One argument, before a selection has filled it in.</summary>
public sealed record ShellArgument(ShellArgumentKind Kind, string? Text = null)
{
    public static ShellArgument Word(string text) => new(ShellArgumentKind.Literal, text);

    public static ShellArgument Slot(ShellArgumentKind kind) => new(kind);

    /// <summary>How this argument appears in the generated manifest. Stable — it is compared.</summary>
    public string Token => Kind == ShellArgumentKind.Literal ? $"literal:{Text}" : Kind.ToString();
}

/// <summary>
/// How an action behaves when more than one thing is selected.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the command shape rather than declared beside it, because the shape already says it: a
/// command with one <see cref="ShellArgumentKind.ArchivePath"/> slot has room for exactly one thing, and
/// a command with <see cref="ShellArgumentKind.SelectedPaths"/> takes them all.
/// </para>
/// <para>
/// This is D4. The classic registry's default is one process per selected file, so a three-file
/// Compress-to produced three separate single-file archives instead of one archive of three — and a
/// three-archive Test produced three windows, which OPEN-1 says must not be offered at all.
/// </para>
/// </remarks>
public enum ShellMultiSelect
{
    /// <summary>Offered only when exactly one thing is selected.</summary>
    SingleItemOnly = 0,

    /// <summary>Offered for any selection, and invoked once with all of it.</summary>
    AllItemsAtOnce,
}

/// <summary>
/// What an action runs, before it knows which files.
/// </summary>
/// <remarks>
/// The shape of a command, separated from its contents. The registration needs the shape — it is
/// written once, at install time, before any selection exists — and the launcher needs the shape
/// filled in. Keeping one definition for both is the whole point: a registration that carried its own
/// idea of the shape is exactly how <c>Compress to</c> lost its inputs and its format (D2), and how
/// <c>Extract here</c> lost its destination (D3).
/// </remarks>
public sealed record ShellInvocationTemplate(string Executable, IReadOnlyList<ShellArgument> Arguments)
{
    /// <summary>The manifest form: one stable token per argument.</summary>
    public IReadOnlyList<string> Tokens => [.. Arguments.Select(static a => a.Token)];
}

/// <summary>
/// Every action's command shape, in one table.
/// </summary>
/// <remarks>
/// <para>
/// <b>This table is the action contract.</b> <see cref="ShellLauncher.PlanFor"/> fills it in for a
/// concrete request; the registration manifest publishes it so an installer can write a command that
/// means the same thing; a future <c>IExplorerCommand</c> adapter will read the same table. None of
/// them may compose a command of their own.
/// </para>
/// <para>
/// Two of these shapes cannot be expressed by a classic Windows registry command string:
/// <see cref="ShellArgumentKind.ContainingFolder"/> and <see cref="ShellArgumentKind.SuggestedTarget"/>
/// are computed from the selection, and the registry can only substitute the selection itself. That
/// is a real limitation of the mechanism, not of this table, and it is why the classic registration
/// invokes a resolving verb rather than trying to spell the command out — see
/// <c>docs/SHELL_INTEGRATION.md</c>.
/// </para>
/// </remarks>
public static class ShellInvocations
{
    /// <summary>The command shape for an action, or null when the action has none.</summary>
    public static ShellInvocationTemplate? For(ArchiveAction action)
    {
        if (ShellRegistration.VerbFor(action) is not { } verb)
        {
            return null;
        }

        var executable = verb.Host == ShellHost.Application
            ? ShellRegistration.ApplicationExecutable
            : ShellRegistration.CommandLineExecutable;

        IReadOnlyList<ShellArgument> arguments = action switch
        {
            ArchiveAction.OpenInApp or ArchiveAction.TestArchive =>
                [ShellArgument.Word(verb.Verb), ShellArgument.Slot(ShellArgumentKind.ArchivePath)],

            ArchiveAction.ExtractHere =>
            [
                ShellArgument.Word(verb.Verb),
                ShellArgument.Slot(ShellArgumentKind.ArchivePath),
                ShellArgument.Word("-o"),
                ShellArgument.Slot(ShellArgumentKind.ContainingFolder),
            ],

            // No destination: the point of this verb is that the person picks one, in the existing
            // dialog, under the existing conflict policy.
            ArchiveAction.ExtractTo =>
                [ShellArgument.Word(verb.Verb), ShellArgument.Slot(ShellArgumentKind.ArchivePath)],

            ArchiveAction.CompressTo =>
            [
                ShellArgument.Word(verb.Verb),
                ShellArgument.Slot(ShellArgumentKind.SuggestedTarget),
                ShellArgument.Slot(ShellArgumentKind.SelectedPaths),
                ShellArgument.Word("--format"),
                ShellArgument.Slot(ShellArgumentKind.FormatId),
            ],

            ArchiveAction.CreateArchive =>
                [ShellArgument.Word(verb.Verb), ShellArgument.Slot(ShellArgumentKind.SelectedPaths)],

            _ => [],
        };

        return arguments.Count == 0 ? null : new ShellInvocationTemplate(executable, arguments);
    }

    /// <summary>
    /// True when a classic registry command string can express this shape unaided.
    /// </summary>
    /// <remarks>
    /// The registry can substitute the selection (<c>%1</c>, <c>%*</c>) and nothing else. An action
    /// needing a folder derived from the selection, or a name derived from it, needs something that
    /// can compute — which is what the resolving verb is for.
    /// </remarks>
    public static bool IsDirectlyRegistrable(ShellInvocationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        foreach (var argument in template.Arguments)
        {
            if (argument.Kind is ShellArgumentKind.ContainingFolder
                or ShellArgumentKind.SuggestedTarget)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether this shape takes the whole selection at once, or only ever one thing.
    /// </summary>
    /// <remarks>
    /// Read off the arguments, so it cannot disagree with them. A shape naming
    /// <see cref="ShellArgumentKind.SelectedPaths"/> is by construction the one that wants all of them.
    /// </remarks>
    public static ShellMultiSelect MultiSelectFor(ShellInvocationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        foreach (var argument in template.Arguments)
        {
            if (argument.Kind == ShellArgumentKind.SelectedPaths)
            {
                return ShellMultiSelect.AllItemsAtOnce;
            }
        }

        return ShellMultiSelect.SingleItemOnly;
    }
}
