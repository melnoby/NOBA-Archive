using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Formats;

public sealed record ArchiveStatistics
{
    public long EntryCount { get; init; }

    public long FileCount { get; init; }

    public long DirectoryCount { get; init; }

    public long RejectedEntryCount { get; init; }

    public long EncryptedEntryCount { get; init; }

    public ByteSize TotalUncompressedSize { get; init; } = ByteSize.Zero;

    public ByteSize TotalCompressedSize { get; init; } = ByteSize.Zero;

    public bool IsSolid { get; init; }

    public int VolumeCount { get; init; } = 1;

    public bool HasEncryptedFileNames { get; init; }

    public static ArchiveStatistics Empty { get; } = new();

    /// <summary>Compressed / uncompressed. Null when the uncompressed total is unknown or zero.</summary>
    public double? CompressionRatio =>
        TotalUncompressedSize.Bytes == 0
            ? null
            : (double)TotalCompressedSize.Bytes / TotalUncompressedSize.Bytes;
}
