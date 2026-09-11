using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// Counts bytes as a provider decodes them and trips the archive-bomb guard mid-stream.
/// </summary>
/// <remarks>
/// The counting has to happen <em>here</em>, on bytes actually produced, because the declared
/// uncompressed size in archive metadata is attacker-controlled (THREAT_MODEL.md T3). A guard that
/// trusted the declared size would be defeated by declaring a small one.
/// </remarks>
internal sealed class GuardedWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly IResourceGuard _guard;
    private readonly ByteSize _compressedSize;
    private readonly string _entryPath;
    private long _written;
    private long _nextCheckAt = 64 * 1024;

    /// <summary>Takes ownership of <paramref name="inner"/>; disposing this disposes it.</summary>
    public GuardedWriteStream(Stream inner, IResourceGuard guard, ByteSize compressedSize, string entryPath)
    {
        _inner = inner;
        _guard = guard;
        _compressedSize = compressedSize;
        _entryPath = entryPath;
    }

    public long BytesWritten => _written;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Account(buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <summary>
    /// The final measurement, taken once the entry has been fully decoded.
    /// </summary>
    /// <remarks>
    /// This is not redundant with the streaming checks, and leaving it out was a real hole: the
    /// amortised schedule sets the next checkpoint past the current size, so an entry that ends
    /// between two checkpoints is never measured at its full size. A 512 MB entry expanding 1029:1
    /// sailed through precisely that way — every scheduled check saw a ratio still under the
    /// threshold, and the run ended before the next one came due.
    /// </remarks>
    public void VerifyFinalSize() => Check();

    private void Account(int count)
    {
        _written += count;
        if (_written < _nextCheckAt)
        {
            return;
        }

        // Amortised: the guard is consulted on a growing schedule rather than per write, so a
        // pathological entry is caught early without taxing a normal one.
        _nextCheckAt = Math.Max(_written * 2, _written + (1 << 20));
        Check();
    }

    private void Check()
    {
        var verdict = _guard.ObserveEntry(_compressedSize, ByteSize.FromBytes(_written));
        if (verdict.IsTripped)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.BombSuspected,
                verdict.Explanation,
                _entryPath));
        }
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
