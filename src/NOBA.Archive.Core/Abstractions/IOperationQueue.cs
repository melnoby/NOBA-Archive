using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Abstractions;

/// <param name="Destination">
/// Where the operation writes, when it writes anywhere. Null for one that does not — testing an
/// archive produces no files.
/// </param>
/// <remarks>
/// The destination is carried on the operation because an operation that writes somewhere is entitled
/// to say where. The extraction destination is chosen in a folder picker, handed to the engine, and
/// until now recorded nowhere at all: the row said "Extracting noba-ops.7z" and the finished result
/// said how many files, and between them there was no way for anyone — a user looking for their
/// files, or a gate checking that a cancel really stopped — to find out which folder had been
/// written to. That is a defect in the product, not only in the test harness.
/// </remarks>
public sealed record OperationRequest(
    OperationType Type,
    string Description,
    Func<OperationContext, CancellationToken, Task> Work)
{
    public string? Destination { get; init; }

    /// <summary>
    /// Whether this operation can be suspended part-way.
    /// </summary>
    /// <remarks>
    /// Not every kind can. Testing an archive hands the whole job to the backend and gets a verdict
    /// back; there is no copy loop to stand in and no pause token to await. ADR-0007 is explicit that
    /// a pause we cannot honour must be reported as unavailable rather than offered and ignored, so
    /// the queue refuses to pause an operation that says false here and the button is disabled.
    /// </remarks>
    public bool SupportsPause { get; init; } = true;
}

/// <summary>What a running operation is handed: its identity, a progress sink, and a pause gate.</summary>
public sealed record OperationContext(
    OperationId Id,
    OperationType Type,
    IProgress<OperationProgress> Progress,
    IPauseToken PauseToken);

/// <summary>Awaited inside copy loops. Real pause where the backend supports it, never faked.</summary>
public interface IPauseToken
{
    bool IsPaused { get; }

    ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The same gate, for a caller that has no continuation to return to.
    /// </summary>
    /// <remarks>
    /// A native backend drives extraction through synchronous write callbacks: there is no await to
    /// suspend, so blocking that call <em>is</em> the pause. Sync-over-async would be the obvious way
    /// to serve it and the wrong one — the engine is forbidden from blocking on a task, for good
    /// reasons that do not stop applying here. An implementation must therefore wait on something that
    /// was built to be waited on synchronously, and must remain cancellable while it does.
    /// </remarks>
    void WaitWhilePaused(CancellationToken cancellationToken);
}

/// <summary>
/// What an operation looks like from outside the queue.
/// </summary>
/// <remarks>
/// The timestamps are optional because a queued operation has not started and a running one has not
/// finished. They are here because <see cref="ArchiveOperation"/> has recorded them since the queue was
/// written and nothing could read them: a screen that wants to say how long an extraction took had the
/// answer computed for it and no way to reach it.
/// </remarks>
public sealed record OperationSnapshot(
    OperationId Id,
    OperationType Type,
    OperationState State,
    string Description,
    OperationProgress? LastProgress,
    ArchiveError? Error,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? Destination = null,
    bool SupportsPause = true)
{
    /// <summary>How long it ran, once it has stopped. Null while it is still going.</summary>
    public TimeSpan? Duration =>
        StartedAt is { } started && FinishedAt is { } finished ? finished - started : null;
}

public interface IOperationQueue : IAsyncDisposable
{
    int MaxConcurrentOperations { get; }

    IReadOnlyList<OperationSnapshot> Snapshot();

    OperationId Enqueue(OperationRequest request);

    bool TryPause(OperationId id);

    bool TryResume(OperationId id);

    bool TryCancel(OperationId id);

    /// <summary>Completes when the operation reaches a terminal state.</summary>
    Task<OperationSnapshot> WaitForCompletionAsync(OperationId id, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets every operation that has finished, and returns how many were removed.
    /// </summary>
    /// <remarks>
    /// Without this the queue is a leak with a user interface: every extraction anyone ever ran stays
    /// in the dictionary for the life of the process, and a "clear finished" button could only hide
    /// rows rather than actually clear anything. Running operations are never touched.
    /// </remarks>
    int RemoveFinished();

    event EventHandler<OperationSnapshot>? OperationChanged;

    event EventHandler<OperationProgress>? ProgressReported;
}
