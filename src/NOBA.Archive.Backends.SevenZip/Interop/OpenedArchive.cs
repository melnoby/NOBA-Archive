namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <summary>
/// Keeps an opened archive and the managed objects it holds references to alive together, and tears
/// them down in the only safe order.
/// </summary>
/// <remarks>
/// <b>Finding, and a load-bearing one.</b> The handler retains its <c>IInStream</c> for as long as it is
/// open. Releasing the archive after freeing the stream object - which is what plain <c>using</c>
/// declarations do, since they unwind in reverse order of declaration - makes 7-Zip call
/// <c>Release</c> on freed memory and the process dies with SIGSEGV. The spike hit exactly that.
/// <para>
/// The rule for the production interop layer: <b>close and release the archive first, then release the
/// streams.</b> An <c>IArchiveSession</c> implementation must own both and dispose them in that order;
/// it must never leave the ordering to the caller's <c>using</c> statements.
/// </para>
/// </remarks>
public sealed class OpenedArchive : IDisposable
{
    private readonly InStreamWrapper _input;
    private readonly OpenCallback? _openCallback;
    private bool _disposed;

    public OpenedArchive(InArchive archive, InStreamWrapper input, OpenCallback? openCallback)
    {
        Archive = archive;
        _input = input;
        _openCallback = openCallback;
    }

    public int PasswordRequestsDuringOpen => _openCallback?.PasswordRequests ?? 0;

    public InArchive Archive { get; }

    public long BytesReadFromInput => _input.BytesRead;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Order matters. See the remarks above.
        Archive.Dispose();
        _input.Dispose();
        _openCallback?.Dispose();
    }
}
