using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.Interaction;

/// <summary>Why one dropped item did not become an input.</summary>
public enum DropRejection
{
    /// <summary>It was accepted.</summary>
    None = 0,

    /// <summary>The drop carried something that is not a file or folder on this machine.</summary>
    NotAFilesystemItem,

    /// <summary>The path resolved, but nothing is there now.</summary>
    Unreachable,

    /// <summary>The same path is already in the list.</summary>
    AlreadyListed,

    /// <summary>It is the archive being created. Compressing a file into itself is not a thing.</summary>
    SelfTarget,
}

/// <summary>One item the drop could not use, and why.</summary>
/// <param name="Path">
/// What was dropped, exactly as Windows gave it. Empty when the item had no filesystem path at all.
/// </param>
public sealed record DropRejectionDetail(string Path, DropRejection Reason);

/// <summary>
/// What a drop actually did.
/// </summary>
/// <remarks>
/// Returned rather than swallowed because a drop that silently does nothing is indistinguishable from
/// a drop the application failed to notice. The user let go of six files and the list did not move;
/// they are owed a sentence saying which of the two happened.
/// </remarks>
public sealed record DropOutcome(int Added, IReadOnlyList<DropRejectionDetail> Rejected)
{
    public static DropOutcome Nothing { get; } = new(0, []);

    public bool AddedAnything => Added > 0;

    public bool HasRejections => Rejected.Count > 0;

    /// <summary>
    /// The one sentence to put on the screen, or null when the drop was unremarkable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One reason, not a list: a mixed drop that rejected four items for four different causes would
    /// produce a paragraph nobody reads. The most serious cause wins, because that is the one the user
    /// can act on — a self-referential target is a mistake to correct, a duplicate is not.
    /// </para>
    /// <para>
    /// Duplicates are deliberately silent when something else was added. Dragging a folder twice is
    /// how people check that the first drag worked, and scolding them for it is noise.
    /// </para>
    /// </remarks>
    public string? ReasonFor(LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Rejected.Count == 0)
        {
            return null;
        }

        if (Worst() is not { } reason)
        {
            return null;
        }

        return reason switch
        {
            DropRejection.SelfTarget => text.DropSelfTarget,
            DropRejection.Unreachable => text.DropUnreachable,
            DropRejection.NotAFilesystemItem => text.DropNotAFile,
            DropRejection.AlreadyListed => text.DropAlreadyListed,
            _ => null,
        };
    }

    private DropRejection? Worst()
    {
        var ranked = new[]
        {
            DropRejection.SelfTarget,
            DropRejection.Unreachable,
            DropRejection.NotAFilesystemItem,
        };

        foreach (var candidate in ranked)
        {
            foreach (var item in Rejected)
            {
                if (item.Reason == candidate)
                {
                    return candidate;
                }
            }
        }

        // Only duplicates are left. Worth saying when the drop achieved nothing at all, and not
        // otherwise.
        if (Added > 0)
        {
            return null;
        }

        foreach (var item in Rejected)
        {
            if (item.Reason == DropRejection.AlreadyListed)
            {
                return DropRejection.AlreadyListed;
            }
        }

        return null;
    }
}

/// <summary>
/// Decides whether two paths name the same thing, for the purpose of not listing it twice.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shallow. It folds case, unifies separators and drops a trailing one, which covers what
/// Windows and a file manager actually hand over: <c>C:\Docs\File.txt</c> against
/// <c>c:\docs\file.txt</c>, and <c>C:\Projects\A</c> against <c>C:\Projects\A\</c>.
/// </para>
/// <para>
/// It does not resolve <c>..</c>, junctions, substituted drives or UNC aliases, and it must not start
/// doing so here. Those need the filesystem, and this type is the one thing in the drop path that has
/// to give the same answer on the machine that runs the tests as on the machine that runs the product
/// — <see cref="System.IO.Path.GetFullPath(string)"/> resolves a Windows path against a Linux working
/// directory and produces a confident, wrong answer. The engine canonicalises for real, later, where
/// the disk is.
/// </para>
/// </remarks>
public static class PathKey
{
    /// <summary>A comparison key. Never shown to anyone, and never stored as the input path.</summary>
    public static string For(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var unified = path.Trim().Replace('/', '\\');

        // A root is its own separator: "C:\" must not become "C:".
        var trimmed = unified.TrimEnd('\\');
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            trimmed = unified;
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>True when both name the same file or folder as far as this comparison can tell.</summary>
    public static bool Same(string? left, string? right) =>
        For(left) is { Length: > 0 } a && For(right) is { Length: > 0 } b && string.Equals(a, b, StringComparison.Ordinal);
}
