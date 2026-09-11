using NOBA.Archive.Application.Operations;
using Xunit;

namespace NOBA.Archive.Application.Tests;

/// <summary>
/// The synchronous half of the pause gate: <c>PTK-001…004</c>.
/// </summary>
/// <remarks>
/// It exists because a native backend drives extraction through synchronous write callbacks, where
/// there is no continuation to suspend. That makes it the only pause mechanism available on the batch
/// extraction path — the path that, until this drop, had no pause at all — so its behaviour under
/// resume and under cancellation is worth pinning rather than assuming.
/// </remarks>
public sealed class PauseTokenTests
{
    /// <summary>PTK-001: an unpaused gate does not detain anyone.</summary>
    [Fact]
    public void An_open_gate_returns_immediately()
    {
        using var token = new PauseToken();

        token.WaitWhilePaused(CancellationToken.None);

        Assert.False(token.IsPaused);
    }

    /// <summary>PTK-002: a paused gate holds the caller until it is resumed.</summary>
    [Fact]
    public async Task A_closed_gate_holds_until_resumed()
    {
        using var token = new PauseToken();
        token.Pause();

        var released = false;
        var waiter = Task.Run(
            () =>
            {
                token.WaitWhilePaused(CancellationToken.None);
                Volatile.Write(ref released, true);
            },
            TestContext.Token);

        await Task.Delay(150, TestContext.Token);
        Assert.False(Volatile.Read(ref released), "the gate let the caller through while paused");

        token.Resume();
        await waiter;

        Assert.True(Volatile.Read(ref released));
    }

    /// <summary>
    /// PTK-003: cancelling while paused releases the caller by throwing.
    /// </summary>
    /// <remarks>
    /// This is the case a user actually produces: pause, look at what is happening, then decide to
    /// cancel instead of resuming. A gate that only woke on Resume would hold the extraction thread
    /// until somebody pressed a button they had already decided against.
    /// </remarks>
    [Fact]
    public async Task Cancelling_while_paused_releases_the_caller()
    {
        using var token = new PauseToken();
        using var cancellation = new CancellationTokenSource();
        token.Pause();

        var waiter = Task.Run(
            () => token.WaitWhilePaused(cancellation.Token),
            TestContext.Token);

        await Task.Delay(100, TestContext.Token);
        Assert.False(waiter.IsCompleted, "the gate did not hold");

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.True(token.IsPaused, "cancelling must not quietly resume the operation");
    }

    /// <summary>PTK-004: an already-cancelled token is refused at the gate, paused or not.</summary>
    [Fact]
    public void An_already_cancelled_wait_throws_even_when_open()
    {
        using var token = new PauseToken();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => token.WaitWhilePaused(cancellation.Token));
    }
}
