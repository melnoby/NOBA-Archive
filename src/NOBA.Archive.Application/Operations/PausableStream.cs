using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Application.Operations;

/// <summary>
/// A write stream that stops when the operation is paused and throws when it is cancelled.
/// </summary>
/// <remarks>
/// <para>
/// Pause and cancel used to be checked between entries, in the loops the handler drives itself. That
/// was true of two of the three extraction paths. The third — the batch path, where a solid 7z is
/// handed to the backend whole and the backend drives the loop — had no check anywhere, so pausing a
/// 3,800-entry extraction changed the badge on the screen and nothing else. The bytes kept landing.
/// </para>
/// <para>
/// Putting the check on the stream fixes that at the only place all three paths meet: nothing reaches
/// the disk except through a stream this layer handed out. It also raises the resolution from
/// per-entry to per-write, which matters for the archive that is one large file — the case where
/// per-entry checks are indistinguishable from no checks at all.
/// </para>
/// <para>
/// The waiting is synchronous, and deliberately so. 7-Zip's write callback is a synchronous native
/// call, so there is no continuation to return to; blocking that thread <em>is</em> the suspension.
/// It runs on a pool thread — the backend puts it there precisely so that blocking is safe — and
/// resuming is done by a different thread, so nothing here can deadlock against itself. It waits on
/// <see cref="IPauseToken.WaitWhilePaused"/> rather than blocking on the async one: the engine may not
/// block on a task, and an architecture test says so.
/// </para>
/// </remarks>
internal sealed class PausableStream : Stream
{
    private readonly Stream _inner;
    private readonly IPauseToken? _pause;
    private readonly CancellationToken _cancellation;
    private readonly bool _leaveOpen;

    /// <param name="leaveOpen">
    /// Whether the stream underneath outlives this wrapper. Both extraction paths pass <c>true</c>:
    /// the entry write session owns the temporary file and decides whether it is published or
    /// discarded, and closing it from here would publish a half-written entry under its real name.
    /// </param>
    public PausableStream(
        Stream inner, IPauseToken? pause, CancellationToken cancellation, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _pause = pause;
        _cancellation = cancellation;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Wraps only when there is something to wrap for.</summary>
    /// <remarks>
    /// A handler with no pause token and no cancellation gains nothing from the indirection, and the
    /// backend's <c>IOutStream</c> probe reads <see cref="Stream.CanSeek"/> off whatever it is given —
    /// so the fewer wrappers in that path, the fewer chances to answer it differently by accident.
    /// </remarks>
    public static Stream Wrap(Stream inner, IPauseToken? pause, CancellationToken cancellation) =>
        pause is null && !cancellation.CanBeCanceled ? inner : new PausableStream(inner, pause, cancellation);

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Gate();
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Gate();
        _inner.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Gate();
        _inner.WriteByte(value);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pause is not null)
        {
            await _pause.WaitWhilePausedAsync(_cancellation).ConfigureAwait(false);
        }

        _cancellation.ThrowIfCancellationRequested();
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <summary>
    /// Reading is gated too, because creation is the mirror image of extraction.
    /// </summary>
    /// <remarks>
    /// A backend building an archive pulls the source bytes when it wants them, so the only place this
    /// layer can stand between a paused operation and continued work is the stream it handed over. The
    /// create handler already waits between entries; without this, pausing while a single large file is
    /// being compressed would move the badge and nothing else — the same defect the extraction path
    /// shipped with, arriving from the other direction.
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count)
    {
        Gate();
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        Gate();
        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pause is not null)
        {
            await _pause.WaitWhilePausedAsync(_cancellation).ConfigureAwait(false);
        }

        _cancellation.ThrowIfCancellationRequested();
        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    /// <summary>
    /// Waits out a pause, then refuses to write if the operation has been cancelled.
    /// </summary>
    /// <remarks>
    /// The order matters. Checking cancellation first would let a cancel that arrives <em>during</em> a
    /// pause sit unnoticed until somebody resumed, which is the one moment a user is most likely to
    /// press it. <see cref="IPauseToken.WaitWhilePausedAsync"/> takes the token for exactly that
    /// reason: it returns by throwing when the wait is cancelled.
    /// </remarks>
    private void Gate()
    {
        _pause?.WaitWhilePaused(_cancellation);
        _cancellation.ThrowIfCancellationRequested();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
