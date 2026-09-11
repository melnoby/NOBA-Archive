using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Formats;

public enum EntryKind
{
    File = 0,
    Directory,
    SymbolicLink,
    HardLink,
    Special,
}

/// <summary>
/// One entry as the archive describes it. Everything here is attacker-controlled.
/// </summary>
/// <remarks>
/// <see cref="Path"/> is null exactly when <see cref="Rejection"/> is set: a hostile entry stays
/// listable and reportable instead of blowing up the listing. Nothing may ever be written using
/// <see cref="RawPath"/>.
/// </remarks>
public sealed record ArchiveEntry
{
    public required string RawPath { get; init; }

    public ArchivePath? Path { get; init; }

    public PathRejection? Rejection { get; init; }

    public required EntryKind Kind { get; init; }

    public ByteSize UncompressedSize { get; init; } = ByteSize.Zero;

    public ByteSize CompressedSize { get; init; } = ByteSize.Zero;

    public DateTimeOffset? Modified { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Accessed { get; init; }

    public FileAttributes? Attributes { get; init; }

    public uint? Crc32 { get; init; }

    public bool IsEncrypted { get; init; }

    public string? Comment { get; init; }

    /// <summary>Raw and untrusted. Validated against the destination before any link is created.</summary>
    public string? LinkTarget { get; init; }

    public bool IsRejected => Rejection is not null;

    public bool IsDirectory => Kind == EntryKind.Directory;

    public bool IsLink => Kind is EntryKind.SymbolicLink or EntryKind.HardLink;

    /// <summary>Name to show in the UI, or the raw path when the entry was rejected.</summary>
    public string DisplayPath => Path?.Value ?? RawPath;

    public static ArchiveEntry ForAcceptedPath(string rawPath, ArchivePath path, EntryKind kind) =>
        new() { RawPath = rawPath, Path = path, Kind = kind };

    public static ArchiveEntry ForRejectedPath(string rawPath, PathRejection rejection, EntryKind kind) =>
        new() { RawPath = rawPath, Rejection = rejection, Kind = kind };
}
