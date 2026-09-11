namespace NOBA.Archive.Core.Operations;

/// <summary>
/// The operation lifecycle as a pure function of (state, trigger). Lives in Core so it can be
/// exhaustively unit-tested and so no ViewModel can invent a transition of its own.
/// </summary>
public static class OperationStateMachine
{
    public static bool TryTransition(OperationState current, OperationTrigger trigger, out OperationState next)
    {
        OperationState? result = (current, trigger) switch
        {
            (OperationState.Queued, OperationTrigger.Start) => OperationState.Running,

            // Nothing has run, so there is nothing to wind down: a queued operation cancels outright.
            (OperationState.Queued, OperationTrigger.RequestCancel) => OperationState.Cancelled,
            (OperationState.Queued, OperationTrigger.Cancel) => OperationState.Cancelled,
            (OperationState.Queued, OperationTrigger.Fail) => OperationState.Failed,

            (OperationState.Running, OperationTrigger.Pause) => OperationState.Paused,
            (OperationState.Running, OperationTrigger.Complete) => OperationState.Completed,
            (OperationState.Running, OperationTrigger.RequestCancel) => OperationState.Cancelling,
            (OperationState.Running, OperationTrigger.Cancel) => OperationState.Cancelled,
            (OperationState.Running, OperationTrigger.Fail) => OperationState.Failed,

            (OperationState.Paused, OperationTrigger.Resume) => OperationState.Running,
            (OperationState.Paused, OperationTrigger.RequestCancel) => OperationState.Cancelling,
            (OperationState.Paused, OperationTrigger.Cancel) => OperationState.Cancelled,
            (OperationState.Paused, OperationTrigger.Fail) => OperationState.Failed,

            (OperationState.Cancelling, OperationTrigger.Cancel) => OperationState.Cancelled,

            // The whole point of the state. Work that runs to the end after the user asked it to stop
            // is still a cancellation: the request is what the person acted on, and reporting Completed
            // would tell them their decision was ignored while claiming the result is theirs to trust.
            (OperationState.Cancelling, OperationTrigger.Complete) => OperationState.Cancelled,

            // A genuine error while unwinding is still an error, and saying so beats hiding it inside a
            // cancellation the user would assume was clean.
            (OperationState.Cancelling, OperationTrigger.Fail) => OperationState.Failed,

            // Pause and Resume are refused here rather than ignored: a cancellation in progress cannot
            // be suspended, and offering it would be a control that does nothing.
            _ => null,
        };

        next = result ?? current;
        return result.HasValue;
    }

    /// <summary>Throws on an illegal transition. Callers that expect to be told "no" use <see cref="TryTransition"/>.</summary>
    public static OperationState Transition(OperationState current, OperationTrigger trigger)
    {
        if (!TryTransition(current, trigger, out var next))
        {
            throw new InvalidOperationException(
                $"'{trigger}' is not a legal trigger for an operation in state '{current}'.");
        }

        return next;
    }

    public static IReadOnlyList<OperationTrigger> LegalTriggers(OperationState state)
    {
        var legal = new List<OperationTrigger>();
        foreach (var trigger in Enum.GetValues<OperationTrigger>())
        {
            if (TryTransition(state, trigger, out _))
            {
                legal.Add(trigger);
            }
        }

        return legal;
    }
}
