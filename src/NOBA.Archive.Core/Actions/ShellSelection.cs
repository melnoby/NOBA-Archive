using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Core.Actions;

/// <summary>
/// One thing Explorer had selected.
/// </summary>
/// <param name="Path">The filesystem path, exactly as the shell gave it.</param>
/// <param name="IsDirectory">
/// Supplied by the caller rather than tested here. Explorer already knows; asking the disk again would
/// be one stat call per selected item on the way to drawing a menu.
/// </param>
/// <param name="Format">
/// What the name suggests this is, or null. A <em>hint</em>, from the extension alone — see
/// <see cref="ArchiveFormat.GuessFromExtension"/>. Menu construction may not open the file to find out
/// (§20), so a mislabelled archive gets the menu its name earned and the truth arrives when the action
/// runs and the content is sniffed.
/// </param>
public sealed record ShellItem(string Path, bool IsDirectory, ArchiveFormat? Format)
{
    /// <summary>
    /// Reads an item from a path and one fact the caller already has.
    /// </summary>
    /// <remarks>
    /// A directory is never treated as an archive however it is named. <c>Backups.zip\</c> as a folder
    /// is a folder, and offering to extract it would be an offer the engine cannot honour.
    /// </remarks>
    public static ShellItem For(string path, bool isDirectory) =>
        new(path, isDirectory, isDirectory ? null : ArchiveFormat.GuessFromExtension(path));

    /// <summary>True when the name says this is an archive of a format the product knows.</summary>
    public bool LooksLikeArchive => !IsDirectory && Format is not null;
}

/// <summary>
/// What the shell had selected when the menu was asked for.
/// </summary>
/// <remarks>
/// Everything on this type is answered from the selection itself — counts, names, the containing
/// folder. Nothing here reads a file, opens an archive or walks a directory, and nothing may start:
/// this runs while the user is holding a right-click open, and a menu that stats a network share is a
/// menu that hangs Explorer.
/// </remarks>
public sealed class ShellSelection
{
    public ShellSelection(IReadOnlyList<ShellItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    public IReadOnlyList<ShellItem> Items { get; }

    public int Count => Items.Count;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The one item, when exactly one was selected.</summary>
    public ShellItem? OnlyItem => Items.Count == 1 ? Items[0] : null;

    /// <summary>The single selected archive, or null — including when several archives are selected.</summary>
    /// <remarks>
    /// Open, Test, Extract here and Extract to are all single-archive actions in this drop. Offering
    /// them for a multi-archive selection would promise a batch the engine has no request shape for.
    /// </remarks>
    public ShellItem? SingleArchive => OnlyItem is { LooksLikeArchive: true } item ? item : null;

    /// <summary>
    /// How many members this selection will put into an archive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from what the caller already told us — <see cref="ShellItem.IsDirectory"/> — and from
    /// nothing else. No file is opened, no folder is walked, no path is probed, so this stays inside
    /// ARC-061 and inside a held right-click.
    /// </para>
    /// <para>
    /// A folder counts as <see cref="ArchiveMemberCount.Many"/> even when it happens to hold one
    /// file. That is D7's correction: the previous code asked <c>PermitsInputCount(Count)</c>, which
    /// counted clicks, so one folder answered "one" and GZIP was offered for something the engine
    /// refuses. Finding out the true number needs the disk, and the disk is not available here — so
    /// the unknown is treated as Many, which errs toward withholding an action rather than offering
    /// one that fails.
    /// </para>
    /// </remarks>
    public ArchiveMemberCount MemberCount =>
        Items.Count == 1 && !Items[0].IsDirectory
            ? ArchiveMemberCount.ExactlyOne
            : ArchiveMemberCount.Many;

    /// <summary>The paths, for handing to a request.</summary>
    public IReadOnlyList<string> Paths => [.. Items.Select(static i => i.Path)];

    /// <summary>Builds a selection from paths plus the caller's knowledge of which are folders.</summary>
    public static ShellSelection From(IEnumerable<string> paths, Func<string, bool> isDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(isDirectory);

        var items = new List<ShellItem>();
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                items.Add(ShellItem.For(path.Trim(), isDirectory(path)));
            }
        }

        return new ShellSelection(items);
    }
}
