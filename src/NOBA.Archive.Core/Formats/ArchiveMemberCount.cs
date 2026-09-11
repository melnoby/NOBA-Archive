namespace NOBA.Archive.Core.Formats;

/// <summary>
/// How many members a selection will put into an archive.
/// </summary>
/// <remarks>
/// <para>
/// Two values, not a number, because the only distinction any format draws is "exactly one" against
/// "more than one" — and because a number invites the caller to supply the wrong one. That is D7:
/// <c>PermitsInputCount(selection.Count)</c> counted mouse clicks, so a single selected folder
/// answered "one" and GZIP was offered for something the engine then refused.
/// </para>
/// <para>
/// <b>A folder is <see cref="Many"/> without anyone looking inside it.</b> That is not a guess, it is
/// the honest answer to a question that cannot be settled without the disk: a folder may hold one
/// file or ten thousand, and menu construction may not find out (ARC-061). Treating the unknown as
/// Many is the safe direction — the worst case is that a single-member format is withheld from a
/// folder that happened to contain exactly one file, and the user reaches it through Create archive…
/// The opposite error offers an action that fails.
/// </para>
/// </remarks>
public enum ArchiveMemberCount
{
    /// <summary>One file, and nothing that could expand.</summary>
    ExactlyOne = 0,

    /// <summary>More than one, or something whose contents are not known here.</summary>
    Many,
}
