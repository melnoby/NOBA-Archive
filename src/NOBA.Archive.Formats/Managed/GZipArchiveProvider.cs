using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using SystemCompressionLevel = System.IO.Compression.CompressionLevel;

namespace NOBA.Archive.Formats.Managed;

/// <summary>
/// GZIP — one compressed file, not an archive.
/// </summary>
/// <remarks>
/// <para>
/// This is the distinction the product has to hold on to. ZIP, 7Z, RAR and TAR are containers: they
/// hold many named entries. A <c>.gz</c> holds exactly one compressed stream, and the name is optional
/// metadata in its header. So this provider presents a gzip as a container with one entry — which is
/// true — and refuses to add a second, which is the part that keeps it true.
/// </para>
/// <para>
/// <b>What it deliberately does not claim.</b> <c>.tgz</c> is a tar inside a gzip, and this provider
/// does not open it as one: it would report a single unnamed blob and the person would learn nothing.
/// Composite formats need a layered session that decompresses the outer member and hands the inner
/// stream to the TAR provider, and that is named as deferred work rather than half-built here.
/// </para>
/// <para>
/// <b>The header is parsed by hand</b> because <see cref="GZipStream"/> does not expose it, and the two
/// things it holds are exactly the two a person wants: the original file name and when it was written.
/// Without them a decompressed <c>report.txt.gz</c> would come out as an entry called <c>report.txt</c>
/// only by luck of the file name, and with no timestamp at all.
/// </para>
/// </remarks>
public sealed class GZipArchiveProvider : IArchiveProvider
{
    private readonly IFormatDetector _detector;

    public GZipArchiveProvider(IFormatDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
    }

    public string ProviderId => "managed-gzip";

    public IReadOnlyList<ArchiveFormat> SupportedFormats => [ArchiveFormat.GZip];

    /// <summary>
    /// What a gzip member can do.
    /// </summary>
    /// <remarks>
    /// <see cref="ArchiveCapabilities.AddEntries"/> is absent, and that absence is the whole modelling
    /// decision: a gzip holds one member, so "add" has no meaning. <see cref="ArchiveCapabilities.Create"/>
    /// is present because compressing one file to <c>.gz</c> is a real thing a person does.
    /// </remarks>
    public ArchiveCapabilities GetCapabilities(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return format == ArchiveFormat.GZip
            ? ArchiveCapabilities.ReadOnlySet
              | ArchiveCapabilities.Create
              | ArchiveCapabilities.StreamingRead

              // gzip stores a CRC32 over the uncompressed member and GZipStream checks it at the end
              // of the stream, so testing a .gz really does verify its contents.
              | ArchiveCapabilities.ContentIntegrityCheck
            : ArchiveCapabilities.None;
    }

    public int PriorityFor(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format == ArchiveFormat.GZip ? 10 : int.MinValue;
    }

    public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
        _detector.DetectAsync(probe, fileName, cancellationToken);

    public async ValueTask<IArchiveSession> OpenAsync(
        ArchiveSource source,
        OpenOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = source.Stream
            ?? throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "This provider reads from a caller-owned stream; open the file through IFileSystem and " +
                "pass ArchiveSource.FromStream.",
                source.FilePath));

        if (!stream.CanSeek)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "The archive stream must be seekable.",
                source.DisplayName));
        }

        var header = await GZipHeader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        var name = header.FileName ?? DefaultEntryName(source.DisplayName);
        var size = await ReadStoredSizeAsync(stream, cancellationToken).ConfigureAwait(false);

        return new GZipSession(new CallerOwnedStream(stream), name, size, header.Modified, stream.Length);
    }

    public ValueTask<IArchiveBuilder> CreateAsync(
        Stream output,
        CompressionOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Encryption != EncryptionAlgorithm.None)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedMethod,
                "GZIP has no encryption. Choose a format that does, or write the archive unencrypted."));
        }

        return ValueTask.FromResult<IArchiveBuilder>(
            new GZipBuilder(new CallerOwnedStream(output), MapLevel(options.Level.Value)));
    }

    /// <summary>The name to show when the header carries none: the file's own, minus the suffix.</summary>
    internal static string DefaultEntryName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "data";
        }

        var name = Path.GetFileName(displayName);
        foreach (var suffix in new[] { ".gz", ".gzip" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = name[..^suffix.Length];
                return trimmed.Length > 0 ? trimmed : "data";
            }
        }

        return name;
    }

    /// <summary>
    /// The uncompressed size gzip stores in its last four bytes.
    /// </summary>
    /// <remarks>
    /// ISIZE is the length modulo 2³². For anything under 4 GiB it is exact; above that it is the
    /// remainder and there is no way to tell from the file which it is. Reported as-is rather than
    /// recomputed, because recomputing means decompressing the whole member just to fill in a column.
    /// </remarks>
    private static async ValueTask<long> ReadStoredSizeAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.Length < 4)
        {
            return 0;
        }

        var position = stream.Position;
        try
        {
            stream.Position = stream.Length - 4;
            var tail = new byte[4];
            await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt32LittleEndian(tail);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static SystemCompressionLevel MapLevel(int level) => level switch
    {
        <= 0 => SystemCompressionLevel.NoCompression,
        <= 3 => SystemCompressionLevel.Fastest,
        <= 7 => SystemCompressionLevel.Optimal,
        _ => SystemCompressionLevel.SmallestSize,
    };
}

/// <summary>The parts of a gzip header worth showing a person. RFC 1952 §2.3.</summary>
internal sealed record GZipHeader(string? FileName, DateTimeOffset? Modified)
{
    private const byte FlagText = 1 << 0;
    private const byte FlagHeaderCrc = 1 << 1;
    private const byte FlagExtra = 1 << 2;
    private const byte FlagName = 1 << 3;
    private const byte FlagComment = 1 << 4;

    public static async ValueTask<GZipHeader> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var position = stream.Position;
        try
        {
            stream.Position = 0;

            var fixedPart = new byte[10];
            await stream.ReadExactlyAsync(fixedPart, cancellationToken).ConfigureAwait(false);

            if (fixedPart[0] != 0x1F || fixedPart[1] != 0x8B)
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.CorruptData,
                    "This file does not begin with a GZIP header."));
            }

            var flags = fixedPart[3];
            var stamp = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(4, 4));

            // MTIME 0 means "not set" in RFC 1952, which is not the same as 1970.
            DateTimeOffset? modified = stamp == 0
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(stamp);

            if ((flags & FlagExtra) != 0)
            {
                var lengthBytes = new byte[2];
                await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
                stream.Position += BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            }

            var fileName = (flags & FlagName) != 0
                ? await ReadNulTerminatedAsync(stream, cancellationToken).ConfigureAwait(false)
                : null;

            // FCOMMENT and FHCRC are read past rather than kept: neither is worth showing, and both
            // have to be consumed for the offset arithmetic above to have meant anything.
            if ((flags & FlagComment) != 0)
            {
                await ReadNulTerminatedAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            if ((flags & FlagHeaderCrc) != 0)
            {
                stream.Position += 2;
            }

            _ = flags & FlagText;
            return new GZipHeader(string.IsNullOrWhiteSpace(fileName) ? null : fileName, modified);
        }
        catch (EndOfStreamException ex)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                "The file ends inside its GZIP header.",
                inner: ex));
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// A NUL-terminated name from the header.
    /// </summary>
    /// <remarks>
    /// RFC 1952 says ISO-8859-1. It is also attacker-controlled, so it is decoded and then handed
    /// straight to <c>ArchivePath</c> like every other entry name — nothing here decides it is safe.
    /// The 4 KiB cap stops a header with no terminator from being read to the end of the file.
    /// </remarks>
    private static async ValueTask<string?> ReadNulTerminatedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];

        while (bytes.Count < 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new EndOfStreamException();
            }

            if (one[0] == 0)
            {
                return Encoding.Latin1.GetString(bytes.ToArray());
            }

            bytes.Add(one[0]);
        }

        throw new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.CorruptData,
            "The GZIP header's name field has no terminator."));
    }
}

internal sealed class GZipSession : IArchiveSession
{
    private readonly CallerOwnedStream _source;
    private readonly ArchiveEntry _entry;

    public GZipSession(
        CallerOwnedStream source,
        string entryName,
        long uncompressedSize,
        DateTimeOffset? modified,
        long compressedSize)
    {
        _source = source;

        var described = ArchivePath.TryCreate(entryName, out var path, out var rejection)
            ? ArchiveEntry.ForAcceptedPath(entryName, path, EntryKind.File)
            : ArchiveEntry.ForRejectedPath(entryName, rejection!, EntryKind.File);

        _entry = described with
        {
            UncompressedSize = ByteSize.FromBytes(Math.Max(0, uncompressedSize)),
            CompressedSize = ByteSize.FromBytes(Math.Max(0, compressedSize)),
            Modified = modified,
        };

        Statistics = new ArchiveStatistics
        {
            EntryCount = 1,
            FileCount = 1,
            DirectoryCount = 0,
            TotalUncompressedSize = _entry.UncompressedSize,
            TotalCompressedSize = _entry.CompressedSize,
        };
    }

    public ArchiveFormat Format => ArchiveFormat.GZip;

    public ArchiveCapabilities Capabilities =>
        ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.StreamingRead;

    public ArchiveStatistics Statistics { get; }

    public bool SupportsPause => true;

    public async IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return _entry;
    }

    public async ValueTask ExtractToAsync(
        ArchiveEntry entry,
        Stream destination,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        if (!string.Equals(entry.RawPath, _entry.RawPath, StringComparison.Ordinal))
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                $"'{entry.RawPath}' is not present in this archive.",
                entry.RawPath));
        }

        var stream = _source.Value;
        stream.Position = 0;

        await using var decompressor = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        await ZipSession.CopyAsync(decompressor, destination, bytesProgress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decompresses the member to nowhere.
    /// </summary>
    /// <remarks>
    /// Unlike TAR, this is a real content check: gzip stores a CRC32 over the uncompressed data and
    /// <see cref="GZipStream"/> verifies it as the stream ends. A corrupted member fails here.
    /// </remarks>
    public async ValueTask<ArchiveTestResult> TestAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var findings = new List<ArchiveTestFinding>();

        try
        {
            var stream = _source.Value;
            stream.Position = 0;

            await using var decompressor = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            await decompressor.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            findings.Add(new ArchiveTestFinding(_entry.RawPath, ex.Message, IsError: true));
        }

        return ArchiveTestResult.FromFindings(findings.Count == 0 ? 1 : 0, findings);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Writes one gzip member, and refuses a second.
/// </summary>
/// <remarks>
/// The refusal is the point. Compressing a folder to <c>.gz</c> is not a thing that can be done, and
/// the alternatives are all worse than an error: silently keeping the first file, silently keeping the
/// last, or concatenating members into something most tools will read as one file. It fails, with a
/// reason, and the publisher abandons the staging file so nothing is left behind.
/// </remarks>
internal sealed class GZipBuilder : IArchiveBuilder
{
    private readonly CallerOwnedStream _output;
    private readonly SystemCompressionLevel _level;
    private bool _written;

    public GZipBuilder(CallerOwnedStream output, SystemCompressionLevel level)
    {
        _output = output;
        _level = level;
    }

    public ArchiveFormat Format => ArchiveFormat.GZip;

    public ArchiveCapabilities Capabilities => ArchiveCapabilities.Create;

    /// <summary>
    /// Writes the one member: header, deflate stream, CRC32, length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the container is assembled here rather than by <see cref="GZipStream"/>.</b> GZipStream
    /// writes a minimal header with no file name in it, and the name is the whole point. Compressing
    /// <c>تقرير.txt</c> to <c>backup.gz</c> and getting <c>backup</c> back — no extension, no original
    /// name — is what happens when the field is left empty, and the person has lost information the
    /// format had room for. RFC 1952 §2.3.1 defines FNAME for exactly this.
    /// </para>
    /// <para>
    /// So the header and trailer are written directly and only the compressed payload comes from the
    /// BCL, through <see cref="DeflateStream"/> — which is what GZipStream wraps anyway. Nothing here
    /// implements compression; it implements the envelope.
    /// </para>
    /// </remarks>
    public async ValueTask AddAsync(
        ArchivePath entryPath,
        EntryContentSource content,
        DateTimeOffset? modified,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (_written)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedMethod,
                "A GZIP file holds exactly one file. To put several files in one archive, choose a "
                + "format that holds more than one.",
                entryPath.Value));
        }

        _written = true;

        var output = _output.Value;

        // The stored name is the leaf only. A gzip has no directories, so a path in FNAME would be a
        // path this format cannot honour - and one that a careless reader might hand to the filesystem.
        var storedName = Path.GetFileName(entryPath.Value);
        await WriteHeaderAsync(output, storedName, modified, cancellationToken).ConfigureAwait(false);

        uint crc = 0;
        long length = 0;

        await using (var source = await content.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var compressor = new DeflateStream(output, _level, leaveOpen: true))
        {
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                length += read;

                await compressor.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesProgress?.Report(read);
            }
        }

        // CRC32 over the uncompressed bytes, then the length modulo 2^32. Both little-endian, both
        // required: they are what makes a .gz verifiable at all.
        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(0, 4), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4, 4), (uint)(length & 0xFFFFFFFF));
        await output.WriteAsync(trailer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A gzip has no directories, so an empty one is nothing rather than an error.</summary>
    public ValueTask AddDirectoryAsync(
        ArchivePath entryPath,
        DateTimeOffset? modified,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_written)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "A GZIP file holds exactly one file, and none was written."));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async ValueTask WriteHeaderAsync(
        Stream output,
        string storedName,
        DateTimeOffset? modified,
        CancellationToken cancellationToken)
    {
        // RFC 1952 says ISO-8859-1 for FNAME. A name that cannot be represented there - an Arabic or
        // Japanese one - would be mangled into question marks, so it is simply not stored: the reader
        // then falls back to the archive's own name, which is lossy but never wrong. UTF-8 in this
        // field is a common extension and is exactly the kind of thing that makes another tool show
        // mojibake, so it is not written either.
        var nameBytes = Encoding.Latin1.GetBytes(storedName);
        var storable = storedName.Length > 0
            && Encoding.Latin1.GetString(nameBytes).Equals(storedName, StringComparison.Ordinal);

        var header = new byte[10];
        header[0] = 0x1F;
        header[1] = 0x8B;
        header[2] = 8;                               // deflate
        header[3] = (byte)(storable ? 1 << 3 : 0);   // FNAME

        var stamp = modified is { } when_ && when_.ToUnixTimeSeconds() is > 0 and <= uint.MaxValue
            ? (uint)when_.ToUnixTimeSeconds()
            : 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), stamp);

        header[8] = 0;     // no extra flags
        header[9] = 255;   // unknown OS, which is the honest answer for a portable writer

        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        if (storable)
        {
            await output.WriteAsync(nameBytes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// CRC-32 as gzip and ZIP define it: reflected, polynomial <c>0xEDB88320</c>.
/// </summary>
/// <remarks>
/// Written out rather than taken from a package. It is twenty lines of table lookup with a
/// specification that has not moved since 1996, and the alternative was adding a dependency to a
/// security-sensitive path for the sake of one loop.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var value = ~crc;
        foreach (var b in data)
        {
            value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
        }

        return ~value;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
