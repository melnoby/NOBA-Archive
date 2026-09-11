using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Abstractions;

/// <summary>Where the archive bytes come from. A path, or a stream the caller owns.</summary>
public sealed record ArchiveSource
{
    public string? FilePath { get; init; }

    public Stream? Stream { get; init; }

    public string? DisplayName { get; init; }

    public static ArchiveSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ArchiveSource { FilePath = path, DisplayName = Path.GetFileName(path) };
    }

    public static ArchiveSource FromStream(Stream stream, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ArchiveSource { Stream = stream, DisplayName = displayName };
    }
}

/// <summary>
/// A staged archive being written. The stream is open; publishing it to its final path is the
/// publisher's job, not the provider's.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="IExtractionWriter"/>, and the same argument (ADR-0005) run in the
/// other direction: a backend that cannot name a destination cannot write to the wrong one, and a
/// half-written archive never appears under the name the user will trust.
/// </remarks>
public interface IArchivePublication : IAsyncDisposable
{
    /// <summary>The staging stream the provider writes into.</summary>
    Stream Output { get; }

    string TargetPath { get; }

    /// <summary>Publishes the staged bytes atomically. Not calling it abandons the archive.</summary>
    ValueTask PublishAsync(CancellationToken cancellationToken);
}

/// <summary>Opens a staging stream beside the target and publishes it on success.</summary>
public interface IArchivePublisher
{
    ValueTask<IArchivePublication> BeginAsync(string targetPath, bool overwrite, CancellationToken cancellationToken);
}

public sealed record OpenOptions
{
    /// <summary>Skip content sniffing and open as this format ("Open as..." in the UI).</summary>
    public ArchiveFormat? ForceFormat { get; init; }

    /// <summary>Legacy code page for entry names without a UTF-8 flag. Null uses the platform default.</summary>
    public int? LegacyNameCodePage { get; init; }

    public static OpenOptions Default { get; } = new();
}

/// <summary>
/// A backend. Providers encode and decode; they never open, create, or write a file on disk
/// (ADR-0005). Bytes reach them through streams the caller opened and will close.
/// </summary>
public interface IArchiveProvider
{
    /// <summary>Stable identifier, e.g. <c>managed-zip</c> or <c>sevenzip-native</c>.</summary>
    string ProviderId { get; }

    IReadOnlyList<ArchiveFormat> SupportedFormats { get; }

    ArchiveCapabilities GetCapabilities(ArchiveFormat format);

    /// <summary>Registry tie-breaker when several providers handle a format. Higher wins.</summary>
    int PriorityFor(ArchiveFormat format);

    ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken);

    ValueTask<IArchiveSession> OpenAsync(
        ArchiveSource source,
        OpenOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts writing an archive into a stream the caller owns and will publish. The provider never
    /// learns a destination path, for the same reason it is never told one when extracting.
    /// </summary>
    ValueTask<IArchiveBuilder> CreateAsync(
        Stream output,
        CompressionOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken);
}

/// <summary>
/// How a format that does not pin its entry-name encoding ended up decoding them.
/// </summary>
/// <remarks>
/// ZIP is the reason this exists: bit 11 says "UTF-8" and without it the bytes are in some unrecorded
/// legacy code page. When <see cref="IsGuess"/> is true the answer was inferred, and a UI should say so
/// and offer to reinterpret rather than presenting a guess as a fact — the decoded name becomes a
/// filename on disk, so being quietly wrong means writing mojibake into someone's folder.
/// </remarks>
public sealed record EntryNameEncodingInfo(string DisplayName, int CodePage, bool IsGuess);

/// <summary>An opened archive.</summary>
public interface IArchiveSession : IAsyncDisposable
{
    ArchiveFormat Format { get; }

    /// <summary>Null when the format defines its own encoding and there is nothing to report.</summary>
    EntryNameEncodingInfo? NameEncoding => null;

    ArchiveCapabilities Capabilities { get; }

    ArchiveStatistics Statistics { get; }

    /// <summary>False when the backend cannot pause mid-item. The UI says so instead of faking it.</summary>
    bool SupportsPause { get; }

    IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Decodes one entry into a destination stream the caller owns. The provider must not touch the
    /// filesystem - that is the extraction writer's sole responsibility.
    /// </summary>
    ValueTask ExtractToAsync(
        ArchiveEntry entry,
        Stream destination,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken);

    ValueTask<ArchiveTestResult> TestAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// True when this session can extract many entries in a single pass over the archive.
    /// </summary>
    /// <remarks>
    /// False is a correctness-neutral answer: the caller falls back to one
    /// <see cref="ExtractToAsync"/> per entry and gets the same files. It is a performance property,
    /// and for solid archives a decisive one — a solid block decodes as a unit, so extracting n entries
    /// one at a time decodes the block n times.
    /// </remarks>
    bool SupportsBatchExtraction => false;

    /// <summary>
    /// Extracts many entries in one pass, asking <paramref name="open"/> where each one goes.
    /// </summary>
    /// <param name="open">
    /// Returns the destination stream for an entry, or null to skip it. Returning null is how a
    /// refused entry is declined without the backend ever learning why — or that it happened.
    /// </param>
    /// <param name="close">
    /// Called once per entry after the backend finishes with it, with whether the backend reported
    /// success. This is the hook that lets the writer publish or discard.
    /// </param>
    /// <remarks>
    /// The shape mirrors 7-Zip's own extraction callback exactly, which is the point: it is the widest
    /// contract that can be implemented over an inverted-control backend without the caller losing
    /// ownership of the filesystem.
    /// </remarks>
    ValueTask ExtractManyAsync(
        IReadOnlyList<ArchiveEntry> entries,
        Func<ArchiveEntry, CancellationToken, ValueTask<Stream?>> open,
        Func<ArchiveEntry, bool, CancellationToken, ValueTask> close,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This session does not support batch extraction; check SupportsBatchExtraction first.");
}

/// <summary>
/// One entry's bytes, opened on demand rather than handed over.
/// </summary>
/// <remarks>
/// <para>
/// A stream would be simpler, and it is what this took until 7Z creation arrived. 7-Zip's writer is a
/// <em>pull</em> model: <c>IOutArchive::UpdateItems</c> takes the whole item list at once and then asks
/// "give me the stream for item N" in whatever order it likes, at the moment it compresses. There is no
/// point during <c>AddAsync</c> at which that stream can be handed over, because the compressor has not
/// started; and holding every source file open until <c>CompleteAsync</c> would exhaust handles on any
/// real folder.
/// </para>
/// <para>
/// The same inversion as batch extraction, in the other direction: the backend asks, the caller
/// supplies. A managed backend that wants the bytes immediately simply calls
/// <see cref="OpenAsync"/> straight away.
/// </para>
/// <para>
/// <paramref name="Length"/> is required because 7-Zip asks for the entry's size <em>before</em> it
/// asks for its bytes — the size goes in the header it is building.
/// </para>
/// </remarks>
public sealed record EntryContentSource(Func<CancellationToken, ValueTask<Stream>> OpenAsync, long Length)
{
    /// <summary>Wraps a stream that is already open. The stream is not disposed by the builder.</summary>
    public static EntryContentSource FromOpenStream(Stream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new EntryContentSource(_ => ValueTask.FromResult<Stream>(new NonClosingStream(stream)), length);
    }
}

/// <summary>
/// Hands a stream to a backend that will dispose what it is given, without disposing the original.
/// </summary>
/// <remarks>
/// The ownership rule for <see cref="EntryContentSource"/> is that the backend disposes what
/// <c>OpenAsync</c> returns — that is the only rule under which a pull-model backend can close each
/// file as soon as it has read it. A caller that already holds an open stream needs a way to opt out of
/// that, and this is it.
/// </remarks>
#pragma warning disable CA2213, CA2215 // The whole point of this type is that it does not dispose what it wraps.
internal sealed class NonClosingStream : Stream
{
    private readonly Stream _inner;

    public NonClosingStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#pragma warning restore CA2213, CA2215

/// <summary>An archive being created or updated.</summary>
public interface IArchiveBuilder : IAsyncDisposable
{
    ArchiveFormat Format { get; }

    ArchiveCapabilities Capabilities { get; }

    ValueTask AddAsync(
        ArchivePath entryPath,
        EntryContentSource content,
        DateTimeOffset? modified,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken);

    ValueTask AddDirectoryAsync(ArchivePath entryPath, DateTimeOffset? modified, CancellationToken cancellationToken);

    /// <summary>Finalises the archive. Not calling this before disposal discards the work.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}
