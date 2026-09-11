using System.Buffers.Binary;

namespace NOBA.Archive.Formats.Managed;

/// <summary>
/// Reads the ZIP central directory just far enough to learn two things the BCL will not tell us:
/// which entries are encrypted, and what bytes their names are actually made of.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.IO.Compression</c> cannot decrypt, and it does not expose the general-purpose bit flag
/// either — so without this, an encrypted ZIP would fail with a generic "corrupt data" message, which
/// is exactly the kind of error rule 24 calls unacceptable. Reading one flag per entry turns that into
/// "this entry is encrypted".
/// </para>
/// <para>
/// The name bytes matter for the same reason: bit 11 of the general-purpose flag says "UTF-8", and
/// without it the bytes are in some legacy code page that only the raw form can reveal (INT-020).
/// </para>
/// <para>
/// This is a header inspection, not a ZIP implementation: it decodes no data, and where the layout is
/// anything other than plainly readable it reports "unknown" rather than guessing.
/// </para>
/// </remarks>
public static class ZipEncryptionProbe
{
    private const uint EndOfCentralDirectorySignature = 0x0605_4B50;
    private const uint CentralFileHeaderSignature = 0x0201_4B50;
    private const int EndOfCentralDirectoryLength = 22;
    private const int MaxCommentLength = 0xFFFF;
    private const ushort EncryptedFlag = 0x0001;
    private const ushort Utf8NameFlag = 0x0800;
    private const uint Zip64Sentinel = 0xFFFF_FFFF;

    /// <summary>
    /// Every central-directory entry as raw bytes. Empty when the directory could not be read —
    /// callers must not treat empty as proof of anything.
    /// </summary>
    public static IReadOnlyList<ZipRawEntry> ReadEntries(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            return [];
        }

        var origin = stream.Position;
        try
        {
            return TryLocateCentralDirectory(stream, out var offset, out var count)
                ? ReadRawEntries(stream, offset, count)
                : [];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return [];
        }
        finally
        {
            stream.Position = origin;
        }
    }

    private static bool TryLocateCentralDirectory(Stream stream, out long offset, out int count)
    {
        offset = 0;
        count = 0;

        var length = stream.Length;
        if (length < EndOfCentralDirectoryLength)
        {
            return false;
        }

        // The end-of-central-directory record sits at the tail, after a comment of up to 64 KiB.
        var searchLength = (int)Math.Min(length, EndOfCentralDirectoryLength + MaxCommentLength);
        var buffer = new byte[searchLength];
        stream.Position = length - searchLength;
        ReadExactly(stream, buffer);

        for (var i = searchLength - EndOfCentralDirectoryLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var entries = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 10));
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 16));

            // Zip64 puts sentinels here and the real values in a separate record. Rather than parse a
            // second format, report unknown - the caller already treats that as "cannot tell".
            if (directoryOffset == Zip64Sentinel || entries == 0xFFFF)
            {
                return false;
            }

            offset = directoryOffset;
            count = entries;
            return offset >= 0 && offset < length;
        }

        return false;
    }

    private static List<ZipRawEntry> ReadRawEntries(Stream stream, long offset, int count)
    {
        var entries = new List<ZipRawEntry>(count);
        stream.Position = offset;

        var header = new byte[46];
        for (var i = 0; i < count; i++)
        {
            ReadExactly(stream, header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralFileHeaderSignature)
            {
                break;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));

            var nameBytes = new byte[nameLength];
            ReadExactly(stream, nameBytes);

            // Deliberately not decoded here. Which encoding is correct is a decision for
            // ZipNameEncoding once every entry has been seen, so the raw bytes travel intact.
            entries.Add(new ZipRawEntry(
                nameBytes,
                (flags & Utf8NameFlag) != 0,
                (flags & EncryptedFlag) != 0));

            stream.Position += extraLength + commentLength;
        }

        return entries;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer[read..]);
            if (chunk == 0)
            {
                throw new EndOfStreamException();
            }

            read += chunk;
        }
    }
}

/// <summary>One central-directory entry, before anyone has decided what its name says.</summary>
public sealed record ZipRawEntry(byte[] RawName, bool DeclaresUtf8, bool IsEncrypted);
