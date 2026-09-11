using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Runtime.CompilerServices;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using SystemCompressionLevel = System.IO.Compression.CompressionLevel;

namespace NOBA.Archive.Formats.Managed;

/// <summary>
/// ZIP support on <c>System.IO.Compression</c>.
/// </summary>
/// <remarks>
/// <para>
/// Zero third-party dependencies, Zip64 out of the box, and fully streaming. What it deliberately does
/// <b>not</b> claim is encryption: the BCL cannot read or write encrypted ZIP entries, so
/// <see cref="ArchiveCapabilities.Encrypt"/> is absent and the UI will not offer it. Encrypted ZIP
/// arrives with the native backend in Phase 3 (FORMAT_SUPPORT.md §2).
/// </para>
/// <para>
/// Per ADR-0005 this provider decodes into a stream the caller supplies. It contains no filesystem
/// writes at all — an architecture test enforces that, and it is the reason a defect here cannot place
/// a file anywhere the user did not choose.
/// </para>
/// </remarks>
public sealed class ZipArchiveProvider : IArchiveProvider
{
    internal const int CopyBufferSize = 1 << 20;

    private readonly IFormatDetector _detector;

    public ZipArchiveProvider(IFormatDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
    }

    public string ProviderId => "managed-zip";

    public IReadOnlyList<ArchiveFormat> SupportedFormats => [ArchiveFormat.Zip];

    public ArchiveCapabilities GetCapabilities(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format == ArchiveFormat.Zip
            ? ArchiveCapabilities.Open
              | ArchiveCapabilities.ListEntries
              | ArchiveCapabilities.Extract
              | ArchiveCapabilities.Test
              | ArchiveCapabilities.Create
              | ArchiveCapabilities.AddEntries
              | ArchiveCapabilities.RandomAccessRead
              | ArchiveCapabilities.StreamingRead

              // Every ZIP entry carries a CRC32 over its uncompressed bytes, and the deflate stream
              // verifies it as the entry ends. Reading each entry to the end is therefore a real
              // content check, not only a structural one.
              | ArchiveCapabilities.ContentIntegrityCheck
            : ArchiveCapabilities.None;
    }

    /// <summary>Low priority: the native backend outranks this one where both can do the job.</summary>
    public int PriorityFor(ArchiveFormat format) => format == ArchiveFormat.Zip ? 10 : int.MinValue;

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

        try
        {
            // Read the central directory ourselves first: the BCL will not tell us the general-purpose
            // flags, and the name bytes are needed before anyone can decide what encoding they are in.
            var raw = ZipEncryptionProbe.ReadEntries(stream);
            var naming = ZipNameEncoding.Choose(raw, options.LegacyNameCodePage);

            // Handing the same encoding to ZipArchive is what keeps entry.FullName and our own decode
            // in agreement; a mismatch here would make the encrypted-entry lookup miss silently.
            var encrypted = raw
                .Where(static e => e.IsEncrypted)
                .Select(e => Decode(e, naming.Encoding))
                .ToHashSet(StringComparer.Ordinal);

            var archive = await ZipArchive.CreateAsync(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: true,
                entryNameEncoding: naming.Encoding,
                cancellationToken).ConfigureAwait(false);
            return new ZipSession(archive, encrypted, naming);
        }
        catch (InvalidDataException ex)
        {
            // The caller opened the stream and the caller closes it; unwinding here would be reaching
            // into someone else's resource.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                ex.Message,
                source.DisplayName,
                ex));
        }
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

        if (options.RequiresPassword)
        {
            // Honest refusal beats writing an unencrypted archive the user believes is protected.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedOperation,
                "The managed ZIP backend cannot create encrypted archives. Use 7Z, or wait for the " +
                "native ZIP backend."));
        }

        // leaveOpen: the publisher owns the stream and decides when it becomes a real archive.
        var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        return ValueTask.FromResult<IArchiveBuilder>(new ZipBuilder(archive, MapLevel(options)));
    }

    /// <summary>Bit 11 overrides the archive-wide choice: a single entry may declare itself UTF-8.</summary>
    private static string Decode(ZipRawEntry entry, Encoding chosen) =>
        entry.DeclaresUtf8 ? Encoding.UTF8.GetString(entry.RawName) : chosen.GetString(entry.RawName);

    internal static SystemCompressionLevel MapLevel(CompressionOptions options) => options.Level.Value switch
    {
        0 => SystemCompressionLevel.NoCompression,
        <= 3 => SystemCompressionLevel.Fastest,
        <= 7 => SystemCompressionLevel.Optimal,
        _ => SystemCompressionLevel.SmallestSize,
    };

}

internal sealed class ZipSession : IArchiveSession
{
    private readonly ZipArchive _archive;
    private readonly IReadOnlySet<string> _encryptedNames;
    private readonly Dictionary<string, ZipArchiveEntry> _byRawPath = new(StringComparer.Ordinal);

    /// <summary>How the entry names were decoded, so the UI can say so and offer to reinterpret.</summary>
    public ZipNameEncodingChoice Naming { get; }

    public EntryNameEncodingInfo? NameEncoding => new(
        Naming.Source switch
        {
            ZipNameEncodingSource.DeclaredUtf8 => "UTF-8 (declared by the archive)",
            ZipNameEncodingSource.Ascii => "ASCII",
            ZipNameEncodingSource.SniffedUtf8 => "UTF-8 (undeclared, detected)",
            ZipNameEncodingSource.CallerSpecified => $"code page {Naming.CodePage} (requested)",
            _ => $"code page {Naming.CodePage} (guessed from this machine)",
        },
        Naming.CodePage,
        Naming.IsGuess);

    public ZipSession(ZipArchive archive, IReadOnlySet<string> encryptedNames, ZipNameEncodingChoice naming)
    {
        _archive = archive;
        _encryptedNames = encryptedNames;
        Naming = naming;

        long files = 0;
        long directories = 0;
        long uncompressed = 0;
        long compressed = 0;

        foreach (var entry in archive.Entries)
        {
            _byRawPath[entry.FullName] = entry;
            if (IsDirectoryEntry(entry))
            {
                directories++;
            }
            else
            {
                files++;
                uncompressed += entry.Length;
                compressed += entry.CompressedLength;
            }
        }

        Statistics = new ArchiveStatistics
        {
            EntryCount = files + directories,
            FileCount = files,
            DirectoryCount = directories,
            EncryptedEntryCount = encryptedNames.Count,
            TotalUncompressedSize = ByteSize.FromBytes(uncompressed),
            TotalCompressedSize = ByteSize.FromBytes(compressed),
        };
    }

    public ArchiveFormat Format => ArchiveFormat.Zip;

    public ArchiveCapabilities Capabilities =>
        ArchiveCapabilities.Open | ArchiveCapabilities.ListEntries | ArchiveCapabilities.Extract |
        ArchiveCapabilities.Test | ArchiveCapabilities.RandomAccessRead;

    public ArchiveStatistics Statistics { get; }

    /// <summary>Managed decoding is a plain copy loop, so pausing between chunks is genuinely possible.</summary>
    public bool SupportsPause => true;

    public async IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entry in _archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Describe(entry);
        }
    }

    public async ValueTask ExtractToAsync(
        ArchiveEntry entry,
        Stream destination,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        if (!_byRawPath.TryGetValue(entry.RawPath, out var zipEntry))
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                $"'{entry.RawPath}' is not present in this archive.",
                entry.RawPath));
        }

        if (_encryptedNames.Contains(zipEntry.FullName))
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedMethod,
                "This entry is encrypted. The managed ZIP backend cannot decrypt; encrypted ZIP " +
                "support arrives with the native backend.",
                entry.RawPath));
        }

        await using var source = await OpenEntryAsync(zipEntry, cancellationToken).ConfigureAwait(false);
        await CopyAsync(source, destination, bytesProgress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every entry to the end. That is the integrity test: the deflate stream verifies each
    /// entry's CRC as it finishes, so a corrupted archive fails here rather than silently producing
    /// wrong bytes.
    /// </summary>
    public async ValueTask<ArchiveTestResult> TestAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var findings = new List<ArchiveTestFinding>();
        long tested = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(ZipArchiveProvider.CopyBufferSize);
        try
        {
            foreach (var entry in _archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsDirectoryEntry(entry))
                {
                    continue;
                }

                if (_encryptedNames.Contains(entry.FullName))
                {
                    findings.Add(new ArchiveTestFinding(
                        entry.FullName,
                        "Encrypted; integrity could not be verified without decryption.",
                        IsError: false));
                    continue;
                }

                try
                {
                    await using var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    tested++;
                }
                catch (InvalidDataException ex)
                {
                    findings.Add(new ArchiveTestFinding(entry.FullName, ex.Message, IsError: true));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ArchiveTestResult.FromFindings(tested, findings);
    }

    internal ArchiveEntry Describe(ZipArchiveEntry entry)
    {
        var isDirectory = IsDirectoryEntry(entry);
        var raw = entry.FullName;

        var kind = isDirectory ? EntryKind.Directory : EntryKind.File;
        var described = ArchivePath.TryCreate(raw, out var path, out var rejection)
            ? ArchiveEntry.ForAcceptedPath(raw, path, kind)
            : ArchiveEntry.ForRejectedPath(raw, rejection!, kind);

        return described with
        {
            UncompressedSize = ByteSize.FromBytes(Math.Max(0, entry.Length)),
            CompressedSize = ByteSize.FromBytes(Math.Max(0, entry.CompressedLength)),
            Modified = entry.LastWriteTime,
            IsEncrypted = _encryptedNames.Contains(raw),
        };
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.Name.Length == 0 && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'));

    private static async ValueTask<Stream> OpenEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                ex.Message,
                entry.FullName,
                ex));
        }
    }

    internal static async ValueTask CopyAsync(
        Stream source,
        Stream destination,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ZipArchiveProvider.CopyBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read;
                try
                {
                    read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    throw new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.CorruptData,
                        ex.Message,
                        inner: ex));
                }

                if (read == 0)
                {
                    return;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesProgress?.Report(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public ValueTask DisposeAsync() => _archive.DisposeAsync();
}

internal sealed class ZipBuilder : IArchiveBuilder
{
    private readonly ZipArchive _archive;
    private readonly SystemCompressionLevel _level;
    private bool _completed;

    public ZipBuilder(ZipArchive archive, SystemCompressionLevel level)
    {
        _archive = archive;
        _level = level;
    }

    public ArchiveFormat Format => ArchiveFormat.Zip;

    public ArchiveCapabilities Capabilities => ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries;

    /// <summary>
    /// Converts an unambiguous instant into the local wall-clock time a ZIP entry actually stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ZIP entry's timestamp is an MS-DOS date/time: local wall clock, no timezone, no offset. The
    /// filesystem layer deliberately reports instants in UTC, because an instant is the only
    /// unambiguous thing to report. Handing that UTC value straight to the ZIP writer stores the UTC
    /// wall clock as if it were local, and every extracted file then comes out wrong by the machine's
    /// UTC offset.
    /// </para>
    /// <para>
    /// <c>System.IO.Compression</c> reads the value back by interpreting the DOS time as local — so
    /// converting here is what makes the round trip close, and it is also what makes the archive agree
    /// with 7-Zip, WinRAR and Explorer, which all write local time.
    /// </para>
    /// <para>
    /// This was invisible for the whole of Phase 2: the development container runs UTC, where the
    /// conversion is the identity. It took the first Windows run to surface it (R13), and
    /// <c>scripts/test.sh</c> now pins a non-UTC timezone so the class of bug cannot hide again.
    /// </para>
    /// </remarks>
    internal static DateTimeOffset ToDosLocalTime(DateTimeOffset instant) => instant.ToLocalTime();

    public async ValueTask AddAsync(
        ArchivePath entryPath,
        EntryContentSource content,
        DateTimeOffset? modified,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entry = _archive.CreateEntry(entryPath.Value, _level);
        if (modified is { } stamp)
        {
            entry.LastWriteTime = ToDosLocalTime(stamp);
        }

        // A managed writer wants the bytes now, so it opens now. The deferred shape exists for the
        // native backend, which cannot be handed a stream before its compressor has started.
        await using var source = await content.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ZipSession.CopyAsync(source, destination, bytesProgress, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask AddDirectoryAsync(ArchivePath entryPath, DateTimeOffset? modified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A trailing slash with no content is the conventional directory marker.
        var entry = _archive.CreateEntry(entryPath.Value + "/", SystemCompressionLevel.NoCompression);
        if (modified is { } stamp)
        {
            entry.LastWriteTime = ToDosLocalTime(stamp);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Writes the central directory. Until this runs the staged bytes are not a valid archive, which
    /// is what lets the publisher treat an abandoned build as "nothing happened".
    /// </summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _archive.DisposeAsync().ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _archive.DisposeAsync().ConfigureAwait(false);
        }
    }
}
