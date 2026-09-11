using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using Xunit;

namespace NOBA.Archive.Core.Tests;

/// <summary>Exhaustive over state x trigger: the lifecycle is small enough that "most" is not good enough.</summary>
public sealed class OperationStateMachineTests
{
    [Theory]
    [InlineData(OperationState.Queued, OperationTrigger.Start, OperationState.Running)]
    [InlineData(OperationState.Queued, OperationTrigger.Cancel, OperationState.Cancelled)]
    [InlineData(OperationState.Queued, OperationTrigger.Fail, OperationState.Failed)]
    [InlineData(OperationState.Running, OperationTrigger.Pause, OperationState.Paused)]
    [InlineData(OperationState.Running, OperationTrigger.Complete, OperationState.Completed)]
    [InlineData(OperationState.Running, OperationTrigger.Cancel, OperationState.Cancelled)]
    [InlineData(OperationState.Running, OperationTrigger.Fail, OperationState.Failed)]
    [InlineData(OperationState.Paused, OperationTrigger.Resume, OperationState.Running)]
    [InlineData(OperationState.Paused, OperationTrigger.Cancel, OperationState.Cancelled)]
    [InlineData(OperationState.Paused, OperationTrigger.Fail, OperationState.Failed)]

    // Cancellation in two steps: asked for, then actually stopped.
    [InlineData(OperationState.Running, OperationTrigger.RequestCancel, OperationState.Cancelling)]
    [InlineData(OperationState.Paused, OperationTrigger.RequestCancel, OperationState.Cancelling)]
    [InlineData(OperationState.Queued, OperationTrigger.RequestCancel, OperationState.Cancelled)]
    [InlineData(OperationState.Cancelling, OperationTrigger.Cancel, OperationState.Cancelled)]
    [InlineData(OperationState.Cancelling, OperationTrigger.Fail, OperationState.Failed)]

    // The one that matters most: work that finished after the user said stop is still a cancellation.
    [InlineData(OperationState.Cancelling, OperationTrigger.Complete, OperationState.Cancelled)]
    public void Allows_the_legal_transitions(OperationState from, OperationTrigger trigger, OperationState expected)
    {
        var ok = OperationStateMachine.TryTransition(from, trigger, out var next);

        Assert.True(ok);
        Assert.Equal(expected, next);
    }

    [Fact]
    public void Rejects_every_transition_not_explicitly_allowed()
    {
        var allowed = new HashSet<(OperationState, OperationTrigger)>
        {
            (OperationState.Queued, OperationTrigger.Start),
            (OperationState.Queued, OperationTrigger.RequestCancel),
            (OperationState.Queued, OperationTrigger.Cancel),
            (OperationState.Queued, OperationTrigger.Fail),
            (OperationState.Running, OperationTrigger.Pause),
            (OperationState.Running, OperationTrigger.Complete),
            (OperationState.Running, OperationTrigger.RequestCancel),
            (OperationState.Running, OperationTrigger.Cancel),
            (OperationState.Running, OperationTrigger.Fail),
            (OperationState.Paused, OperationTrigger.Resume),
            (OperationState.Paused, OperationTrigger.RequestCancel),
            (OperationState.Paused, OperationTrigger.Cancel),
            (OperationState.Paused, OperationTrigger.Fail),

            // Cancelling accepts only the ways it can end. Notably absent: Pause and Resume - a
            // cancellation in progress cannot be suspended, and RequestCancel again, which would let a
            // second click restart a clock that is already running.
            (OperationState.Cancelling, OperationTrigger.Complete),
            (OperationState.Cancelling, OperationTrigger.Cancel),
            (OperationState.Cancelling, OperationTrigger.Fail),
        };

        foreach (var state in Enum.GetValues<OperationState>())
        {
            foreach (var trigger in Enum.GetValues<OperationTrigger>())
            {
                var ok = OperationStateMachine.TryTransition(state, trigger, out var next);
                if (allowed.Contains((state, trigger)))
                {
                    Assert.True(ok, $"{state} + {trigger} should be legal.");
                }
                else
                {
                    Assert.False(ok, $"{state} + {trigger} must not be legal (it produced {next}).");
                    Assert.Equal(state, next);
                }
            }
        }
    }

    [Fact]
    public void Terminal_states_accept_nothing()
    {
        foreach (var state in new[] { OperationState.Completed, OperationState.Cancelled, OperationState.Failed })
        {
            Assert.True(state.IsTerminal());
            Assert.Empty(OperationStateMachine.LegalTriggers(state));
        }
    }

    [Fact]
    public void Transition_throws_on_an_illegal_move()
    {
        Assert.Throws<InvalidOperationException>(
            () => OperationStateMachine.Transition(OperationState.Completed, OperationTrigger.Start));
    }

    [Fact]
    public void Operation_records_the_error_only_when_it_fails()
    {
        var op = new ArchiveOperation(
            OperationId.Create(OperationType.Extract, DateTimeOffset.UnixEpoch, 1),
            OperationType.Extract,
            "test");
        var now = DateTimeOffset.UnixEpoch;

        Assert.True(op.TryApply(OperationTrigger.Start, now));
        Assert.NotNull(op.StartedAt);
        Assert.Null(op.Error);

        var error = Errors.ArchiveError.Create(Errors.ArchiveErrorCode.DiskFull, "no space");
        Assert.True(op.TryApply(OperationTrigger.Fail, now, error));

        Assert.Equal(OperationState.Failed, op.State);
        Assert.NotNull(op.Error);
        Assert.Equal(Errors.ArchiveErrorCode.DiskFull, op.Error.Code);
        Assert.False(op.TryApply(OperationTrigger.Start, now));
    }
}
