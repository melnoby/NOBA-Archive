using System.Buffers.Binary;
using System.Text;

namespace NOBA.Archive.TestKit.Fixtures;

/// <summary>
/// Writes a genuinely ZipCrypto-encrypted ZIP, byte by byte.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.IO.Compression"/> cannot produce one, and neither can NOBA Archive — the managed
/// backend deliberately refuses to create encrypted archives. That leaves hand-assembly as the only way
/// to get a real fixture, and a real fixture is what SEC-016 needs: flipping the encryption bit on an
/// otherwise-plain archive would test the probe against a file no archiver would ever emit.
/// </para>
/// <para>
/// ZipCrypto is the legacy PKWARE stream cipher and is cryptographically broken. It is used here
/// precisely because it is what hostile and legacy archives in the wild actually contain; NOBA Archive
/// will never <em>write</em> it.
/// </para>
/// </remarks>
public static class EncryptedZipFactory
{
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    public static void WriteZipCryptoArchive(string path, string entryName, string password, string content)
    {
        var plaintext = Encoding.UTF8.GetBytes(content);
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        var crc = Crc32(plaintext);

        var encrypted = Encrypt(password, plaintext, crc);

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);

        const ushort VersionNeeded = 20;
        const ushort FlagEncrypted = 0x0001;
        const ushort MethodStore = 0;
        const ushort DosTime = 0x6000; // 12:00:00
        const ushort DosDate = 0x5A21; // 2025-01-01

        var localHeaderOffset = (uint)file.Position;

        writer.Write(0x04034B50u);
        writer.Write(VersionNeeded);
        writer.Write(FlagEncrypted);
        writer.Write(MethodStore);
        writer.Write(DosTime);
        writer.Write(DosDate);
        writer.Write(crc);
        writer.Write((uint)encrypted.Length);
        writer.Write((uint)plaintext.Length);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(nameBytes);
        writer.Write(encrypted);

        var centralOffset = (uint)file.Position;

        writer.Write(0x02014B50u);
        writer.Write(VersionNeeded); // version made by
        writer.Write(VersionNeeded);
        writer.Write(FlagEncrypted);
        writer.Write(MethodStore);
        writer.Write(DosTime);
        writer.Write(DosDate);
        writer.Write(crc);
        writer.Write((uint)encrypted.Length);
        writer.Write((uint)plaintext.Length);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0); // extra
        writer.Write((ushort)0); // comment
        writer.Write((ushort)0); // disk
        writer.Write((ushort)0); // internal attributes
        writer.Write(0u);        // external attributes
        writer.Write(localHeaderOffset);
        writer.Write(nameBytes);

        var centralSize = (uint)file.Position - centralOffset;

        writer.Write(0x06054B50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
    }

    /// <summary>PKWARE ZipCrypto: a 12-byte header whose last byte checks the password, then the payload.</summary>
    private static byte[] Encrypt(string password, ReadOnlySpan<byte> plaintext, uint crc)
    {
        var keys = InitialKeys(password);

        // Deterministic filler: this is a test fixture, and a reproducible archive is easier to debug
        // than a random one. Only the final byte is load-bearing.
        var header = new byte[12];
        for (var i = 0; i < header.Length; i++)
        {
            header[i] = (byte)(i * 7);
        }

        header[11] = (byte)(crc >> 24);

        var output = new byte[header.Length + plaintext.Length];
        for (var i = 0; i < header.Length; i++)
        {
            output[i] = EncryptByte(ref keys, header[i]);
        }

        for (var i = 0; i < plaintext.Length; i++)
        {
            output[header.Length + i] = EncryptByte(ref keys, plaintext[i]);
        }

        return output;
    }

    private static Keys InitialKeys(string password)
    {
        var keys = new Keys(0x12345678, 0x23456789, 0x34567890);
        foreach (var b in Encoding.UTF8.GetBytes(password))
        {
            UpdateKeys(ref keys, b);
        }

        return keys;
    }

    private static byte EncryptByte(ref Keys keys, byte plain)
    {
        var temp = (ushort)((keys.Key2 | 2) & 0xFFFF);
        var stream = (byte)(((temp * (temp ^ 1)) >> 8) & 0xFF);
        var cipher = (byte)(plain ^ stream);
        UpdateKeys(ref keys, plain);
        return cipher;
    }

    private static void UpdateKeys(ref Keys keys, byte value)
    {
        keys.Key0 = Crc32Step(keys.Key0, value);
        keys.Key1 = (keys.Key1 + (keys.Key0 & 0xFF)) * 134775813 + 1;
        keys.Key2 = Crc32Step(keys.Key2, (byte)(keys.Key1 >> 24));
    }

    private static uint Crc32Step(uint crc, byte value) => (crc >> 8) ^ Crc32Table[(crc ^ value) & 0xFF];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Crc32Step(crc, b);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private struct Keys(uint key0, uint key1, uint key2)
    {
        public uint Key0 = key0;
        public uint Key1 = key1;
        public uint Key2 = key2;
    }
}
