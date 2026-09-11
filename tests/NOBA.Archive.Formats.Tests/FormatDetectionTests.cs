using NOBA.Archive.Core.Formats;
using NOBA.Archive.Formats;
using Xunit;

namespace NOBA.Archive.Formats.Tests;

/// <summary>
/// ARCHITECTURE.md §5.4: content is authoritative, the extension is a hint. A file named
/// <c>.zip</c> that is really a 7z opens as 7z, and the mismatch is something the UI can surface.
/// </summary>
public sealed class FormatDetectionTests
{
    private readonly MagicByteFormatDetector _detector = new();

    private static byte[] Header(params byte[] magic)
    {
        var buffer = new byte[512];
        magic.CopyTo(buffer, 0);
        return buffer;
    }

    [Fact]
    public void Detects_seven_zip_from_its_signature()
    {
        var detection = _detector.DetectFromHeader(Header(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C), "mystery.bin");

        Assert.Equal(ArchiveFormat.SevenZip, detection.Format);
        Assert.Equal(DetectionSource.Content, detection.Source);
    }

    [Fact]
    public void Detects_zip_from_its_local_file_header()
    {
        var detection = _detector.DetectFromHeader(Header(0x50, 0x4B, 0x03, 0x04), null);

        Assert.Equal(ArchiveFormat.Zip, detection.Format);
        Assert.Equal(DetectionSource.Content, detection.Source);
    }

    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 })]
    public void Detects_both_rar_generations(byte[] magic)
    {
        var detection = _detector.DetectFromHeader(Header(magic), null);

        Assert.Equal(ArchiveFormat.Rar, detection.Format);
    }

    /// <summary>POSIX tar has no signature at offset zero; "ustar" sits at 257.</summary>
    [Fact]
    public void Detects_tar_from_the_ustar_marker_at_offset_257()
    {
        var buffer = new byte[512];
        "ustar"u8.CopyTo(buffer.AsSpan(257));

        var detection = _detector.DetectFromHeader(buffer, null);

        Assert.Equal(ArchiveFormat.Tar, detection.Format);
    }

    /// <summary>The interesting case: the name lies and the content tells the truth.</summary>
    [Fact]
    public void Content_wins_over_a_misleading_extension()
    {
        var detection = _detector.DetectFromHeader(Header(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C), "definitely.zip");

        Assert.Equal(ArchiveFormat.SevenZip, detection.Format);
        Assert.True(detection.ContradictsExtension("definitely.zip"));
    }

    [Fact]
    public void Falls_back_to_the_extension_only_when_content_says_nothing()
    {
        var detection = _detector.DetectFromHeader(new byte[512], "archive.7z");

        Assert.Equal(ArchiveFormat.SevenZip, detection.Format);
        Assert.Equal(DetectionSource.Extension, detection.Source);
        Assert.False(detection.ContradictsExtension("archive.7z"));
    }

    [Fact]
    public void Reports_nothing_when_neither_content_nor_name_helps()
    {
        var detection = _detector.DetectFromHeader(new byte[512], "notes.txt");

        Assert.False(detection.IsKnown);
        Assert.Equal(DetectionSource.None, detection.Source);
    }

    [Fact]
    public async Task Detects_from_a_stream_and_rewinds_it()
    {
        using var stream = new MemoryStream(Header(0x1F, 0x8B));
        stream.Seek(3, SeekOrigin.Begin);

        var detection = await _detector.DetectAsync(stream, "data.bin", CancellationToken.None);

        Assert.Equal(ArchiveFormat.GZip, detection.Format);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task Handles_a_file_shorter_than_the_probe_window()
    {
        using var stream = new MemoryStream([0x50, 0x4B, 0x03, 0x04]);

        var detection = await _detector.DetectAsync(stream, null, CancellationToken.None);

        Assert.Equal(ArchiveFormat.Zip, detection.Format);
    }
}
