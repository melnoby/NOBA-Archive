namespace NOBA.Archive.Core.Operations;

public enum OperationType
{
    Extract = 0,
    Create,
    Add,
    Remove,
    Test,
    List,
    Search,
}

public enum OperationState
{
    Queued = 0,
    Running,
    Paused,

    /// <summary>
    /// Cancellation has been asked for and the work has not stopped yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap between the two is real and it is visible. Cancelling an extraction sets a token; the
    /// writer notices it when it next comes up for air, finishes closing the file it is inside, removes
    /// the temporary file it was writing and only then unwinds. On a large entry that is seconds.
    /// </para>
    /// <para>
    /// Without this state the screen had two ways to describe those seconds, and both were lies:
    /// leave the badge on Running, which says the cancellation did not take, or show Cancelled while
    /// bytes are still being written, which is the specific claim this product refuses to make. So the
    /// state machine names the interval instead — the user is told the request was heard, and the badge
    /// turns Cancelled at the moment nothing is being written any more.
    /// </para>
    /// </remarks>
    Cancelling,

    Completed,
    Cancelled,
    Failed,
}

public enum OperationTrigger
{
    Start = 0,
    Pause,
    Resume,
    Complete,
    Cancel,

    /// <summary>The user asked to stop. The work is still running until <see cref="Cancel"/> follows.</summary>
    RequestCancel,

    Fail,
}

public static class OperationStateExtensions
{
    public static bool IsTerminal(this OperationState state) =>
        state is OperationState.Completed or OperationState.Cancelled or OperationState.Failed;

    /// <summary>
    /// The operation is still doing something — including winding itself down.
    /// </summary>
    /// <remarks>
    /// <see cref="OperationState.Cancelling"/> is active on purpose. It is not finished, and treating it
    /// as finished is how a screen ends up offering to dismiss a row whose writer is still running.
    /// </remarks>
    public static bool IsActive(this OperationState state) =>
        state is OperationState.Running or OperationState.Paused or OperationState.Cancelling;
}
