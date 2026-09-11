using System.Formats.Tar;
using System.Runtime.CompilerServices;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Formats.Managed;

/// <summary>
/// TAR, on <c>System.Formats.Tar</c>.
/// </summary>
/// <remarks>
/// <para>
/// No new dependency: <c>System.Formats.Tar</c> has been in the shared framework since .NET 7, so this
/// provider carries the same supply-chain surface as the ZIP one — none. That mattered more than
/// convenience. A third-party codec in the extraction path is a decision about what this product trusts,
/// and it is not one to take as a side effect of adding a format.
/// </para>
/// <para>
/// <b>TAR is sequential, and this provider says so.</b> There is no central directory: the only way to
/// reach the tenth entry is to read past the first nine. So it reports <see cref="ArchiveCapabilities.StreamingRead"/>
/// and never <see cref="ArchiveCapabilities.RandomAccessRead"/>, and it implements batch extraction —
/// which for this format is not an optimisation but the difference between one pass and one pass per
/// entry.
/// </para>
/// <para>
/// <b>TAR has no content checksum.</b> The header carries one; the file data carries nothing. Test is
/// still offered, because "verify as far as the format allows" is what test means everywhere else in
/// this product — but the result carries an explicit finding saying so, because a clean verdict that
/// the reader mistakes for a content guarantee is worse than no verdict at all.
/// </para>
/// </remarks>
public sealed class TarArchiveProvider : IArchiveProvider
{
    private readonly IFormatDetector _detector;

    public TarArchiveProvider(IFormatDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
    }

    public string ProviderId => "managed-tar";

    public IReadOnlyList<ArchiveFormat> SupportedFormats => [ArchiveFormat.Tar];

    /// <summary>
    /// What TAR can actually do.
    /// </summary>
    /// <remarks>
    /// Absent on purpose: <see cref="ArchiveCapabilities.Encrypt"/> and
    /// <see cref="ArchiveCapabilities.SolidCompression"/>, because TAR does not compress at all;
    /// <see cref="ArchiveCapabilities.RandomAccessRead"/>, because there is no directory to seek by;
    /// <see cref="ArchiveCapabilities.RemoveEntries"/> and <see cref="ArchiveCapabilities.RenameEntries"/>,
    /// because updating an existing archive is not implemented for any format in this product yet.
    /// </remarks>
    public ArchiveCapabilities GetCapabilities(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return format == ArchiveFormat.Tar
            ? ArchiveCapabilities.ReadOnlySet
              | ArchiveCapabilities.Create
              | ArchiveCapabilities.AddEntries
              | ArchiveCapabilities.StreamingRead
              | ArchiveCapabilities.PreservesUnixMode
            : ArchiveCapabilities.None;
    }

    public int PriorityFor(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format == ArchiveFormat.Tar ? 10 : int.MinValue;
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
            // Every operation after Open re-reads from the beginning, because that is the only way to
            // reach an entry in a tar. A stream that cannot rewind could be listed once and never
            // extracted, which is a worse product than refusing here with a reason.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "TAR is read sequentially, so the archive stream must be seekable.",
                source.DisplayName));
        }

        // The index is read once at open. It holds headers only - names, sizes, kinds, timestamps -
        // never content, so a tar of any size costs the same handful of bytes per entry here.
        var index = await ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
        return new TarSession(new CallerOwnedStream(stream), index);
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
                "TAR has no encryption. Choose a format that does, or write the archive unencrypted."));
        }

        // PAX rather than ustar or GNU. ustar caps a name at 100 bytes and has no defined encoding;
        // PAX stores long names and UTF-8 in extended headers, which is what makes an Arabic path or a
        // deep tree survive at all.
        return ValueTask.FromResult<IArchiveBuilder>(
            new TarBuilder(new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true)));
    }

    /// <summary>Header-only scan of the whole archive, from the current position.</summary>
    internal static async ValueTask<List<TarIndexEntry>> ReadIndexAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;

        var index = new List<TarIndexEntry>();
        try
        {
            await using var reader = new TarReader(stream, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
                is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsArchiveRoot(entry.Name))
                {
                    continue;
                }

                index.Add(TarIndexEntry.From(entry));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                ex.Message,
                inner: ex));
        }
        catch (EndOfStreamException ex)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                "The archive ends in the middle of an entry.",
                inner: ex));
        }

        return index;
    }

    /// <summary>
    /// True for the entry a tar uses to name its own root: <c>.</c> or <c>./</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GNU tar writes one of these first whenever an archive is made with <c>-C dir .</c>, which is how
    /// most tars in the world are made. It refers to the destination folder itself, so it names no
    /// content and creates nothing that is not already there.
    /// </para>
    /// <para>
    /// It is dropped here because of what happened when it was not. <c>ArchivePath</c> refuses it —
    /// correctly, it normalises to no segments at all — and the entry then surfaced through the whole
    /// product as a path the <em>security policy</em> had blocked. Every ordinary tar announced
    /// "1 entry would be refused on extraction by the security policy", which is a sentence that should
    /// mean an archive tried something, and here meant an archive was normal.
    /// </para>
    /// <para>
    /// This is not the path policy being relaxed: the policy is unchanged and still refuses <c>./</c> if
    /// anything hands it one. It is this provider declining to report a container's own root as one of
    /// its entries. <c>./foo</c> is a real entry and is kept.
    /// </para>
    /// </remarks>
    internal static bool IsArchiveRoot(string name) =>
        name is "." or "./" or ".\\";
}

/// <summary>
/// A stream this component reads but does not own.
/// </summary>
/// <remarks>
/// A wrapper rather than a bare <see cref="Stream"/> field, and the reason is worth stating: a session
/// holding a <c>Stream</c> makes CA2213 ask why it is never disposed, and the answer — ADR-0005, the
/// caller opened it and the caller closes it — is not something an analyzer can read from a comment.
/// Suppressing the rule would hide the question; naming the ownership answers it, and the next person
/// to hold one of these reads the intent off the type.
/// </remarks>
internal sealed record CallerOwnedStream(Stream Value);

/// <summary>One entry as the header described it. No content, and no reference to the reader.</summary>
internal sealed record TarIndexEntry(
    string Name,
    EntryKind Kind,
    long Length,
    DateTimeOffset? Modified,
    string? LinkTarget,
    UnixFileMode Mode)
{
    public static TarIndexEntry From(TarEntry entry) => new(
        entry.Name,
        KindOf(entry.EntryType),
        entry.Length,
        entry.ModificationTime == default ? null : entry.ModificationTime,
        string.IsNullOrEmpty(entry.LinkName) ? null : entry.LinkName,
        entry.Mode);

    /// <summary>
    /// Maps a tar type byte onto the product's own vocabulary.
    /// </summary>
    /// <remarks>
    /// Character and block devices, FIFOs and sockets all land on <see cref="EntryKind.Special"/>. The
    /// extraction writer refuses to materialise those, and it should: a tar that asks for a device node
    /// is asking for something no archive manager should hand it.
    /// </remarks>
    private static EntryKind KindOf(TarEntryType type) => type switch
    {
        TarEntryType.Directory => EntryKind.Directory,
        TarEntryType.SymbolicLink => EntryKind.SymbolicLink,
        TarEntryType.HardLink => EntryKind.HardLink,
        TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => EntryKind.File,
        _ => EntryKind.Special,
    };
}

internal sealed class TarSession : IArchiveSession
{
    private readonly CallerOwnedStream _source;
    private readonly List<TarIndexEntry> _index;

    public TarSession(CallerOwnedStream source, List<TarIndexEntry> index)
    {
        _source = source;
        _index = index;

        long files = 0;
        long directories = 0;
        long bytes = 0;

        foreach (var entry in index)
        {
            if (entry.Kind == EntryKind.Directory)
            {
                directories++;
            }
            else
            {
                files++;
                bytes += entry.Length;
            }
        }

        Statistics = new ArchiveStatistics
        {
            EntryCount = files + directories,
            FileCount = files,
            DirectoryCount = directories,
            EncryptedEntryCount = 0,

            // Uncompressed and compressed are the same number, because tar does not compress. Reporting
            // them as equal is the honest answer; reporting compressed as zero would show a 100% ratio
            // for a format that has no ratio.
            TotalUncompressedSize = ByteSize.FromBytes(bytes),
            TotalCompressedSize = ByteSize.FromBytes(bytes),
        };
    }

    public ArchiveFormat Format => ArchiveFormat.Tar;

    public ArchiveCapabilities Capabilities =>
        ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.StreamingRead;

    public ArchiveStatistics Statistics { get; }

    /// <summary>A managed copy loop, so pausing between chunks is genuinely possible.</summary>
    public bool SupportsPause => true;

    /// <summary>
    /// One pass for many entries, which for tar is the only sensible way to extract more than one.
    /// </summary>
    /// <remarks>
    /// Extracting n entries one at a time means n scans of the whole archive. On a tar of any size that
    /// is quadratic, and it is not a subtle difference: it is the difference between reading a gigabyte
    /// once and reading it a thousand times.
    /// </remarks>
    public bool SupportsBatchExtraction => true;

    public async IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entry in _index)
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

        var stream = _source.Value;
        stream.Position = 0;
        await using var reader = new TarReader(stream, leaveOpen: true);

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
            is { } candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(candidate.Name, entry.RawPath, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.DataStream is not { } data)
            {
                // A directory or a link: nothing to write, and the writer is the component that decides
                // what to do about that.
                return;
            }

            await ZipSession.CopyAsync(data, destination, bytesProgress, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.CorruptData,
            $"'{entry.RawPath}' is not present in this archive.",
            entry.RawPath));
    }

    /// <summary>
    /// Extracts many entries in a single pass over the archive.
    /// </summary>
    /// <remarks>
    /// The contract is 7-Zip's, and it is honoured the same way here: the caller says where each entry
    /// goes by returning a stream, or declines it by returning null, and is told afterwards whether the
    /// backend succeeded. The filesystem stays entirely on the caller's side of the line (ADR-0005).
    /// </remarks>
    public async ValueTask ExtractManyAsync(
        IReadOnlyList<ArchiveEntry> entries,
        Func<ArchiveEntry, CancellationToken, ValueTask<Stream?>> open,
        Func<ArchiveEntry, bool, CancellationToken, ValueTask> close,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);

        var wanted = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            wanted[entry.RawPath] = entry;
        }

        var stream = _source.Value;
        stream.Position = 0;
        await using var reader = new TarReader(stream, leaveOpen: true);

        while (wanted.Count > 0
            && await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
                is { } candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!wanted.Remove(candidate.Name, out var entry))
            {
                continue;
            }

            var destination = await open(entry, cancellationToken).ConfigureAwait(false);
            if (destination is null)
            {
                continue;
            }

            var succeeded = false;
            try
            {
                if (candidate.DataStream is { } data)
                {
                    await ZipSession.CopyAsync(data, destination, null, cancellationToken).ConfigureAwait(false);
                }

                succeeded = true;
            }
            finally
            {
                // Told either way, and before anything is rethrown: the writer publishes on success and
                // discards on failure, and skipping that on the failure path is how a half-written file
                // ends up under the real name.
                await close(entry, succeeded, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads every entry to the end.
    /// </summary>
    /// <remarks>
    /// That verifies the archive's <em>structure</em>: every header parses, every declared length is
    /// actually present, and the stream ends where it says it does. It does not and cannot verify
    /// content, because tar stores no checksum over file data. The finding below says so on every run,
    /// This session does not report <see cref="ArchiveCapabilities.ContentIntegrityCheck"/>, which is
    /// how the difference reaches the person: a verdict of "intact" that the reader takes as a content
    /// guarantee is the failure this product exists not to commit.
    /// </remarks>
    public async ValueTask<ArchiveTestResult> TestAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // No informational finding here, deliberately. What this test cannot check is a property of
        // the format, not something wrong with this archive - and reporting it as a finding made every
        // healthy tar come back "valid with warnings", which teaches a person to ignore warnings. The
        // fact is carried by ArchiveCapabilities.ContentIntegrityCheck instead, where a screen can say
        // it once without knowing which format it is looking at.
        var findings = new List<ArchiveTestFinding>();
        long tested = 0;

        try
        {
            var stream = _source.Value;
            stream.Position = 0;
            await using var reader = new TarReader(stream, leaveOpen: true);

            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
                is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.DataStream is { } data)
                {
                    await data.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                }

                tested++;
            }
        }
        catch (InvalidDataException ex)
        {
            findings.Add(new ArchiveTestFinding(string.Empty, ex.Message, IsError: true));
        }
        catch (EndOfStreamException)
        {
            findings.Add(new ArchiveTestFinding(
                string.Empty,
                "The archive ends in the middle of an entry.",
                IsError: true));
        }

        return ArchiveTestResult.FromFindings(tested, findings);
    }

    internal static ArchiveEntry Describe(TarIndexEntry entry)
    {
        var raw = entry.Name;
        var described = ArchivePath.TryCreate(raw, out var path, out var rejection)
            ? ArchiveEntry.ForAcceptedPath(raw, path, entry.Kind)
            : ArchiveEntry.ForRejectedPath(raw, rejection!, entry.Kind);

        return described with
        {
            UncompressedSize = ByteSize.FromBytes(Math.Max(0, entry.Length)),
            CompressedSize = ByteSize.FromBytes(Math.Max(0, entry.Length)),
            Modified = entry.Modified,
            LinkTarget = entry.LinkTarget,
        };
    }

    /// <summary>The stream belongs to the caller, so this closes nothing.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TarBuilder : IArchiveBuilder
{
    private readonly TarWriter _writer;

    public TarBuilder(TarWriter writer) => _writer = writer;

    public ArchiveFormat Format => ArchiveFormat.Tar;

    public ArchiveCapabilities Capabilities => ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries;

    public async ValueTask AddAsync(
        ArchivePath entryPath,
        EntryContentSource content,
        DateTimeOffset? modified,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await using var source = await content.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A tar header declares the entry's length before its bytes, so the length has to be known up
        // front. A source that cannot say costs one buffering pass; one that can - which is every file
        // on disk - costs nothing.
        var data = source.CanSeek ? source : await BufferAsync(source, cancellationToken).ConfigureAwait(false);

        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryPath.Value)
        {
            DataStream = data,
        };

        if (modified is { } stamp)
        {
            // UTC, not local. Unlike ZIP's MS-DOS stamp, a PAX mtime is an unambiguous instant, so the
            // value is stored as it was measured and needs no timezone correction on the way in or out.
            entry.ModificationTime = stamp.ToUniversalTime();
        }

        await _writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        bytesProgress?.Report(data.Length);

        if (!ReferenceEquals(data, source))
        {
            await data.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask AddDirectoryAsync(
        ArchivePath entryPath,
        DateTimeOffset? modified,
        CancellationToken cancellationToken)
    {
        var name = entryPath.Value.EndsWith('/') ? entryPath.Value : entryPath.Value + '/';
        var entry = new PaxTarEntry(TarEntryType.Directory, name);

        if (modified is { } stamp)
        {
            entry.ModificationTime = stamp.ToUniversalTime();
        }

        await _writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the two zero blocks that end a tar, by disposing the writer.</summary>
    public ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _writer.DisposeAsync();
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    private static async ValueTask<Stream> BufferAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }
}
