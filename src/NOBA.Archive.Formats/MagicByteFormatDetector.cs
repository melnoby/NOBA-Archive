using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Formats;

/// <summary>
/// Identifies a container from its leading bytes. Content is authoritative and the extension is a
/// hint, because a file named <c>.zip</c> that is really a 7z must open as 7z - and the UI must say so
/// (ARCHITECTURE.md §5.4).
/// </summary>
/// <remarks>
/// This reads a header. It does not decompress anything, and it holds no format implementation.
/// </remarks>
public sealed class MagicByteFormatDetector : IFormatDetector
{
    public const int HeaderLength = 512;

    private static readonly Signature[] Signatures =
    [
        new(ArchiveFormat.SevenZip, 0, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 100),
        new(ArchiveFormat.Rar, 0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], 100), // RAR5
        new(ArchiveFormat.Rar, 0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00], 100),       // RAR4
        new(ArchiveFormat.Xz, 0, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], 100),
        new(ArchiveFormat.BZip2, 0, [0x42, 0x5A, 0x68], 90),
        new(ArchiveFormat.GZip, 0, [0x1F, 0x8B], 90),
        new(ArchiveFormat.Zip, 0, [0x50, 0x4B, 0x03, 0x04], 95),
        new(ArchiveFormat.Zip, 0, [0x50, 0x4B, 0x05, 0x06], 80), // empty archive
        new(ArchiveFormat.Zip, 0, [0x50, 0x4B, 0x07, 0x08], 80), // spanned marker
        // POSIX tar keeps "ustar" at offset 257; there is no signature at offset 0.
        new(ArchiveFormat.Tar, 257, [0x75, 0x73, 0x74, 0x61, 0x72], 85),
    ];

    /// <summary>
    /// Containers that are emphatically <em>not</em> archives we support, kept so that a misleading
    /// extension cannot overrule the bytes.
    /// </summary>
    /// <remarks>
    /// Found by running the alpha over a real Downloads folder: a password-protected Office document
    /// had been saved as <c>3.zip</c>. Nothing matched, the extension won by default, and the ZIP
    /// backend reported "End of Central Directory record could not be found" — which reads as "your
    /// archive is corrupt" when the truth is "this was never a ZIP". The file was fine; the message
    /// was wrong (rule 24).
    /// </remarks>
    private static readonly NonArchive[] NonArchives =
    [
        new([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], "a Compound File (a legacy Office document, an MSI, or an encrypted Office file)"),
        new([0x25, 0x50, 0x44, 0x46, 0x2D], "a PDF"),
        new([0x4D, 0x5A], "a Windows executable"),
        new([0x7F, 0x45, 0x4C, 0x46], "an ELF executable"),
        new([0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00], "a SQLite database"),
        new([0x7B, 0x5C, 0x72, 0x74, 0x66], "an RTF document"),
        new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "a PNG image"),
        new([0xFF, 0xD8, 0xFF], "a JPEG image"),
        // "FILE" + update-sequence offset 0x0030 + count 3: an NTFS master file table record, which is
        // what a recovery tool leaves behind when it restores an entry's metadata instead of its
        // contents. Found on a real .rar the user could not open in any tool - the file had been
        // recovered from a failing disk years earlier and had never been an archive since.
        new([0x46, 0x49, 0x4C, 0x45, 0x30, 0x00, 0x03, 0x00], "raw NTFS file-table data, not a file's contents (typically left by disk-recovery software)"),
    ];

    public async ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var buffer = new byte[HeaderLength];
        var read = 0;

        if (probe.CanSeek)
        {
            probe.Seek(0, SeekOrigin.Begin);
        }

        while (read < buffer.Length)
        {
            var chunk = await probe.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        if (probe.CanSeek)
        {
            probe.Seek(0, SeekOrigin.Begin);
        }

        return DetectFromHeader(buffer.AsSpan(0, read), fileName);
    }

    public FormatDetection DetectFromHeader(ReadOnlySpan<byte> header, string? fileName)
    {
        FormatDetection? best = null;

        foreach (var signature in Signatures)
        {
            if (!signature.Matches(header))
            {
                continue;
            }

            if (best is null || signature.Confidence > best.Confidence)
            {
                best = new FormatDetection(signature.Format, DetectionSource.Content, signature.Confidence);
            }
        }

        if (best is not null)
        {
            return best;
        }

        // Before the extension gets a say: if the bytes identify something that is definitely not an
        // archive, the extension is simply wrong and must not win. "Content is authoritative" has to
        // mean this too, or it only holds when content happens to agree.
        foreach (var other in NonArchives)
        {
            if (other.Matches(header))
            {
                return FormatDetection.None with { IdentifiedAs = other.Description };
            }
        }

        var guess = ArchiveFormat.GuessFromExtension(fileName);
        return guess is null
            ? FormatDetection.None
            : new FormatDetection(guess, DetectionSource.Extension, 40);
    }

    private readonly record struct Signature(ArchiveFormat Format, int Offset, byte[] Bytes, int Confidence)
    {
        public bool Matches(ReadOnlySpan<byte> header) =>
            header.Length >= Offset + Bytes.Length &&
            header.Slice(Offset, Bytes.Length).SequenceEqual(Bytes);
    }

    private readonly record struct NonArchive(byte[] Bytes, string Description)
    {
        public bool Matches(ReadOnlySpan<byte> header) =>
            header.Length >= Bytes.Length && header[..Bytes.Length].SequenceEqual(Bytes);
    }
}
