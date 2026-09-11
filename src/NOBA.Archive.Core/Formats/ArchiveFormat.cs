using System.Collections.ObjectModel;

namespace NOBA.Archive.Core.Formats;

/// <summary>An archive container format. Identity is <see cref="Id"/>, never the file extension.</summary>
public sealed record ArchiveFormat
{
    private ArchiveFormat(string id, string displayName, string primaryExtension, params string[] extensions)
    {
        Id = id;
        DisplayName = displayName;
        PrimaryExtension = primaryExtension;
        Extensions = new ReadOnlyCollection<string>(extensions);
    }

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Includes the leading dot, e.g. <c>.7z</c>.</summary>
    public string PrimaryExtension { get; }

    public IReadOnlyList<string> Extensions { get; }

    public static readonly ArchiveFormat Zip = new("zip", "ZIP", ".zip", ".zip", ".zipx");
    public static readonly ArchiveFormat SevenZip = new("7z", "7Z", ".7z", ".7z");
    public static readonly ArchiveFormat Rar = new("rar", "RAR", ".rar", ".rar", ".r00");
    public static readonly ArchiveFormat Tar = new("tar", "TAR", ".tar", ".tar");
    // .tgz, .tbz2, .tbz and .txz are deliberately NOT claimed here.
    //
    // Each of them is a TAR *inside* one of these compressors, which is a different thing from the
    // compressor itself: a .gz holds one compressed stream, a .tgz holds one compressed stream whose
    // contents are an archive of many files. Listing .tgz as an extension of GZIP made the product
    // offer to open it and then show a single unnamed blob - true, and useless. Opening a composite
    // needs a layered session that unwraps the outer member and hands the inner stream to the TAR
    // provider; until that exists, claiming the extension would be claiming the capability.
    public static readonly ArchiveFormat GZip = new("gzip", "GZIP", ".gz", ".gz");
    public static readonly ArchiveFormat BZip2 = new("bzip2", "BZIP2", ".bz2", ".bz2");
    public static readonly ArchiveFormat Xz = new("xz", "XZ", ".xz", ".xz");
    public static readonly ArchiveFormat Unknown = new("unknown", "Unknown", string.Empty);

    public static readonly IReadOnlyList<ArchiveFormat> All =
        new ReadOnlyCollection<ArchiveFormat>([Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz]);

    public static ArchiveFormat? FromId(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A hint only. Content sniffing is authoritative (ARCHITECTURE.md §5.4).</summary>
    public static ArchiveFormat? GuessFromExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = fileName.AsSpan();
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var ext = name[dot..].ToString().ToLowerInvariant();
        return All.FirstOrDefault(f => f.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    public bool Equals(ArchiveFormat? other) => other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

    public override string ToString() => DisplayName;
}
