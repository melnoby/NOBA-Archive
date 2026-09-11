using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>
/// One node of the folder tree built from a flat entry list.
/// </summary>
/// <remarks>
/// <para>
/// Archives store a flat list of paths; folders are implied by the separators and, in some formats, by
/// explicit directory entries that may be missing, duplicated, or present for folders that hold no
/// files. The tree is therefore <em>derived</em> here rather than trusted from the archive.
/// </para>
/// <para>
/// Every node keeps the entry it came from, if any. A folder that exists only because a file mentions
/// it has no entry, and must not be presented as though the archive declared it.
/// </para>
/// </remarks>
public sealed class ArchiveNode
{
    private readonly List<ArchiveNode> _children = [];

    private ArchiveNode(string name, string path, bool isFolder, ArchiveEntry? entry)
    {
        Name = name;
        Path = path;
        IsFolder = isFolder;
        Entry = entry;
    }

    /// <summary>The segment shown in the tree, not the whole path.</summary>
    public string Name { get; }

    /// <summary>Full path inside the archive, forward-slashed.</summary>
    public string Path { get; }

    public bool IsFolder { get; }

    /// <summary>Null for a folder the archive only implied.</summary>
    public ArchiveEntry? Entry { get; private set; }

    public IReadOnlyList<ArchiveNode> Children => _children;

    /// <summary>True when this entry, or anything under it, was refused by path policy.</summary>
    public bool ContainsRejected { get; private set; }

    public bool IsEncrypted => Entry?.IsEncrypted ?? false;

    /// <summary>Sum of the uncompressed sizes beneath this node, or the entry's own size.</summary>
    public ByteSize TotalSize { get; private set; } = ByteSize.Zero;

    public long FileCount { get; private set; }

    /// <summary>The name, isolated so it cannot reorder the row it is drawn in.</summary>
    public string DisplayName => BidiText.Isolate(Name);

    /// <summary>The full path, pinned left-to-right — it is a path, whatever it contains.</summary>
    public string DisplayPath => BidiText.IsolateAsLtr(Path);

    /// <summary>
    /// Builds the tree. Rejected entries are included and marked, never dropped.
    /// </summary>
    /// <remarks>
    /// Hiding a refused entry would leave the user unable to see that an archive tried something, which
    /// is the opposite of what the refusal is for. They appear in the tree under their raw path, flagged,
    /// and cannot be extracted — the writer refuses them again on its own account.
    /// </remarks>
    public static ArchiveNode BuildTree(IReadOnlyList<ArchiveEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var root = new ArchiveNode(string.Empty, string.Empty, isFolder: true, entry: null);

        foreach (var entry in entries)
        {
            var path = entry.DisplayPath.Replace('\\', '/').Trim('/');
            if (path.Length == 0)
            {
                continue;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var walked = string.Empty;

            for (var i = 0; i < segments.Length; i++)
            {
                var last = i == segments.Length - 1;
                walked = walked.Length == 0 ? segments[i] : $"{walked}/{segments[i]}";

                var isFolder = !last || entry.IsDirectory;
                var child = current.Find(segments[i]);

                if (child is null)
                {
                    child = new ArchiveNode(segments[i], walked, isFolder, last ? entry : null);
                    current._children.Add(child);
                }
                else if (last)
                {
                    // The archive declared a folder that files had already implied. Adopt the entry so
                    // its metadata is available, and keep it a folder.
                    child.Entry ??= entry;
                }

                current = child;
            }
        }

        root.Summarise();
        return root;
    }

    private ArchiveNode? Find(string name) =>
        _children.Find(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private void Summarise()
    {
        var rejected = Entry?.IsRejected ?? false;
        var size = Entry is { IsDirectory: false } ? Entry.UncompressedSize.Bytes : 0;
        long files = Entry is { IsDirectory: false } ? 1 : 0;

        foreach (var child in _children)
        {
            child.Summarise();
            rejected |= child.ContainsRejected;
            size += child.TotalSize.Bytes;
            files += child.FileCount;
        }

        // Folders first, then by name, using the ordinal comparison the security layer uses. Display
        // sorting that is culture-aware is a separate concern and is applied by the view, never here:
        // a culture-sensitive comparison anywhere near a path is a defect waiting to be exploited.
        _children.Sort(static (a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : string.CompareOrdinal(a.Name, b.Name));

        ContainsRejected = rejected;
        TotalSize = ByteSize.FromBytes(size);
        FileCount = files;
    }

    /// <summary>Every file at or below this node, in tree order.</summary>
    public IEnumerable<ArchiveNode> Files()
    {
        if (!IsFolder && Entry is not null)
        {
            yield return this;
        }

        foreach (var child in _children)
        {
            foreach (var file in child.Files())
            {
                yield return file;
            }
        }
    }
}
