using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Application.Operations;

/// <summary>
/// A real pause gate, awaited inside copy loops. Where a backend cannot pause mid-item the session
/// reports <c>SupportsPause == false</c> and the UI says so rather than showing a button that lies.
/// </summary>
public sealed class PauseToken : IPauseToken, IDisposable
{
    private readonly ManualResetEventSlim _gate = new(true);

    public bool IsPaused => !_gate.IsSet;

    public void Pause() => _gate.Reset();

    public void Resume() => _gate.Set();

    public ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        if (_gate.IsSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        return WaitCoreAsync(cancellationToken);
    }

    /// <summary>
    /// Blocks the calling thread until resumed or cancelled.
    /// </summary>
    /// <remarks>
    /// <see cref="ManualResetEventSlim.Wait(CancellationToken)"/> is what the gate is for: it parks the
    /// thread and wakes it the moment either Resume or the token fires. Nothing here blocks on a task,
    /// so the engine's no-sync-over-async rule holds — and the caller stops the instant it is resumed,
    /// rather than at the end of a polling interval.
    /// </remarks>
    public void WaitWhilePaused(CancellationToken cancellationToken) => _gate.Wait(cancellationToken);

    private async ValueTask WaitCoreAsync(CancellationToken cancellationToken)
    {
        // Poll rather than block a thread pool thread indefinitely; cancellation stays responsive.
        while (!_gate.IsSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => _gate.Dispose();
}
