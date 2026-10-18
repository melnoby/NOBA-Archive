using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using Xunit;

namespace NOBA.Archive.Core.Tests;

/// <summary>
/// DROP 07: a per-entry ratio, same null-when-zero convention as
/// <see cref="ArchiveStatistics.CompressionRatio"/> — this is the smallest piece of the drop, and the
/// one every bigger piece (sorting in <c>SearchArchiveHandler</c>) sits on top of.
/// </summary>
public sealed class ArchiveEntryTests
{
    [Fact]
    public void Compression_ratio_divides_compressed_by_uncompressed()
    {
        var entry = ArchiveEntry.ForAcceptedPath(
            "file.txt",
            CreatePath("file.txt"),
            EntryKind.File) with
        {
            UncompressedSize = ByteSize.FromBytes(1000),
            CompressedSize = ByteSize.FromBytes(250),
        };

        Assert.Equal(0.25, entry.CompressionRatio);
    }

    [Fact]
    public void Compression_ratio_is_null_when_uncompressed_is_zero()
    {
        var entry = ArchiveEntry.ForAcceptedPath(
            "empty.bin",
            CreatePath("empty.bin"),
            EntryKind.File);

        Assert.Equal(ByteSize.Zero, entry.UncompressedSize);
        Assert.Null(entry.CompressionRatio);
    }

    [Fact]
    public void Compression_ratio_is_null_for_a_directory()
    {
        var entry = ArchiveEntry.ForAcceptedPath("folder", CreatePath("folder"), EntryKind.Directory);

        Assert.Null(entry.CompressionRatio);
    }

    private static ArchivePath CreatePath(string raw)
    {
        Assert.True(ArchivePath.TryCreate(raw, out var path, out _));
        return path;
    }
}
