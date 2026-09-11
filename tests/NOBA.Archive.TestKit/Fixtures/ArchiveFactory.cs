using System.IO.Compression;
using System.Text;

namespace NOBA.Archive.TestKit.Fixtures;

/// <summary>
/// Builds ZIP files with <see cref="System.IO.Compression"/> rather than through NOBA Archive.
/// </summary>
/// <remarks>
/// This is a deliberate choice. A hostile-archive suite that generated its fixtures with the code under
/// test could only ever prove the code is self-consistent: our own writer refuses <c>../</c>, so it
/// could never produce a traversal fixture in the first place. <see cref="ZipArchive.CreateEntry(string)"/>
/// accepts an arbitrary name, which is exactly the third-party-archiver behaviour these tests need to
/// defend against.
/// </remarks>
public static class ArchiveFactory
{
    /// <summary>Writes a ZIP whose entry names are taken literally, however hostile.</summary>
    public static string WriteZip(string path, params (string Name, string Content)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }

    public static string WriteZipBytes(string path, params (string Name, byte[] Content)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return path;
    }

    /// <summary>
    /// A compression bomb: one entry of highly redundant data whose expansion ratio is far past the
    /// guard's threshold, without needing a gigabyte on disk to prove it.
    /// </summary>
    public static string WriteRatioBomb(string path, long expandedBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("bomb.bin", CompressionLevel.SmallestSize);
        using var stream = entry.Open();

        var block = new byte[64 * 1024]; // all zeroes: deflate turns this into almost nothing
        var remaining = expandedBytes;
        while (remaining > 0)
        {
            var take = (int)Math.Min(block.Length, remaining);
            stream.Write(block, 0, take);
            remaining -= take;
        }

        return path;
    }

    /// <summary>
    /// Writes a ZIP by hand whose entry name is raw bytes in a legacy code page, with bit 11 clear.
    /// </summary>
    /// <remarks>
    /// <see cref="System.IO.Compression"/> cannot produce this: it always writes UTF-8 and sets the
    /// flag. But it is exactly what a 2009 copy of WinZip on an Arabic Windows box produced, and those
    /// archives are still in circulation — so the fixture has to be assembled from the format up.
    /// </remarks>
    public static string WriteLegacyNamedZip(string path, byte[] rawName, string content, bool declareUtf8 = false)
    {
        ArgumentNullException.ThrowIfNull(rawName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var data = Encoding.UTF8.GetBytes(content);
        var payload = Deflate(data);
        var crc = Crc32(data);
        var flags = (ushort)(declareUtf8 ? 0x0800 : 0x0000);

        const ushort VersionNeeded = 20;
        const ushort MethodDeflate = 8;
        const ushort DosTime = 0x6000;
        const ushort DosDate = 0x5A21;

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);

        writer.Write(0x04034B50u);
        writer.Write(VersionNeeded);
        writer.Write(flags);
        writer.Write(MethodDeflate);
        writer.Write(DosTime);
        writer.Write(DosDate);
        writer.Write(crc);
        writer.Write((uint)payload.Length);
        writer.Write((uint)data.Length);
        writer.Write((ushort)rawName.Length);
        writer.Write((ushort)0);
        writer.Write(rawName);
        writer.Write(payload);

        var centralOffset = (uint)file.Position;

        writer.Write(0x02014B50u);
        writer.Write(VersionNeeded);
        writer.Write(VersionNeeded);
        writer.Write(flags);
        writer.Write(MethodDeflate);
        writer.Write(DosTime);
        writer.Write(DosDate);
        writer.Write(crc);
        writer.Write((uint)payload.Length);
        writer.Write((uint)data.Length);
        writer.Write((ushort)rawName.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(rawName);

        var centralSize = (uint)file.Position - centralOffset;

        writer.Write(0x06054B50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0);

        return path;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Corrupts a byte inside the compressed payload, leaving the central directory intact.</summary>
    public static void CorruptPayload(string zipPath)
    {
        var bytes = File.ReadAllBytes(zipPath);

        // Local file header: signature(4) name-length at 26, extra-length at 28, then name+extra+data.
        var nameLength = BitConverter.ToUInt16(bytes, 26);
        var extraLength = BitConverter.ToUInt16(bytes, 28);
        var dataStart = 30 + nameLength + extraLength;

        bytes[dataStart] ^= 0xFF;
        File.WriteAllBytes(zipPath, bytes);
    }

    /// <summary>Truncates an archive so the central directory is gone entirely.</summary>
    public static void Truncate(string zipPath, double keepFraction)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var keep = (int)(bytes.Length * keepFraction);
        File.WriteAllBytes(zipPath, bytes.AsSpan(0, keep).ToArray());
    }
}

/// <summary>A temp directory that cleans itself up, so a failing test cannot leak fixtures.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string label)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"noba-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Every path under the directory, relative and separator-normalised, sorted.</summary>
    public IReadOnlyList<string> Snapshot(string subdirectory)
    {
        var root = Combine(subdirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => System.IO.Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
