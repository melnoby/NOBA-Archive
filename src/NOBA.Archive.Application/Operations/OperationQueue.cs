using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Operations;

public sealed record OperationQueueOptions
{
    /// <summary>
    /// One by default: compression already saturates every core, so running two archives at once
    /// mostly makes both slower.
    /// </summary>
    public int MaxConcurrentOperations { get; init; } = 1;

    public static OperationQueueOptions Default { get; } = new();
}

/// <summary>
/// Schedules operations, enforces the concurrency limit, and owns their state. State lives here and
/// never in a ViewModel, so the UI cannot invent a transition (ARCHITECTURE.md §7).
/// </summary>
public sealed class OperationQueue : IOperationQueue
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly OperationIdFactory _ids;
    private readonly IClock _clock;
    private readonly ILogger<OperationQueue> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>
    /// The admission chain: each enqueue waits for the one before it to reach the slot gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it the queue is not a queue. Every enqueue starts its runner with
    /// <see cref="Task.Run(Action)"/>, and two pool tasks reach <see cref="SemaphoreSlim.WaitAsync()"/>
    /// in whatever order the scheduler happens to pick — so with one slot, the <em>second</em>
    /// operation could take it and the first sit Queued behind work the user asked for later. The
    /// concurrency limit was always honoured; the ordering was not.
    /// </para>
    /// <para>
    /// Found by <c>OPQ-019</c> failing once in several full runs. The test was right and the queue was
    /// wrong, which is the only reason the test still says what it always said.
    /// </para>
    /// </remarks>
    private readonly Lock _admissionGate = new();
    private Task _lastAdmission = Task.CompletedTask;

    /// <summary>
    /// Makes "check <see cref="_disposed"/>, then add to <see cref="_entries"/>" (<see cref="Enqueue"/>)
    /// and "flip <see cref="_disposed"/>, then read <see cref="_entries"/>" (<see cref="DisposeAsync"/>)
    /// one critical section each, shared between the two. DROP 13: without it, an entry could be
    /// inserted after <c>DisposeAsync</c>'s enumeration over <see cref="_entries"/> had already started
    /// (<c>ConcurrentDictionary</c> enumeration is weakly consistent — it can miss a concurrent add),
    /// so that entry's runner would never be awaited or cancelled by shutdown and would instead race
    /// its own <c>_slots.WaitAsync</c> against <c>DisposeAsync</c> disposing <c>_slots</c> out from
    /// under it. With this lock, an enqueue and a dispose are always strictly before or strictly after
    /// one another — never interleaved.
    /// </summary>
    private readonly Lock _lifecycleGate = new();

    public OperationQueue(
        OperationIdFactory ids,
        IClock clock,
        ILogger<OperationQueue> logger,
        OperationQueueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _ids = ids;
        _clock = clock;
        _logger = logger;
        MaxConcurrentOperations = Math.Max(1, (options ?? OperationQueueOptions.Default).MaxConcurrentOperations);
        _slots = new SemaphoreSlim(MaxConcurrentOperations, MaxConcurrentOperations);
    }

    public int MaxConcurrentOperations { get; }

    public event EventHandler<OperationSnapshot>? OperationChanged;

    public event EventHandler<OperationProgress>? ProgressReported;

    public IReadOnlyList<OperationSnapshot> Snapshot() =>
        [.. _entries.Values.Select(static e => e.ToSnapshot())];

    public OperationId Enqueue(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = _ids.Next(request.Type);
        var entry = new Entry(
            new ArchiveOperation(id, request.Type, request.Description),
            request.Destination,
            request.SupportsPause);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _entries[id.Value] = entry;
        }

        Raise(entry);

        // The predecessor is captured here, on the caller's thread, in call order — which is the only
        // place the arrival order is still known. RunContinuationsAsynchronously so completing the
        // chain never runs the next operation's admission inline on the thread that finished this one.
        Task predecessor;
        lock (_admissionGate)
        {
            predecessor = _lastAdmission;
            entry.Admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastAdmission = entry.Admitted.Task;
        }

        entry.Runner = Task.Run(() => RunAsync(entry, request, predecessor), CancellationToken.None);
        return id;
    }

    public bool TryPause(OperationId id)
    {
        if (!_entries.TryGetValue(id.Value, out var entry))
        {
            return false;
        }

        // Refused here rather than left to the caller. A pause the work cannot honour would move the
        // badge and stop nothing, which is the exact failure ADR-0007 forbids and which this project
        // has already shipped once on the batch extraction path.
        if (!entry.SupportsPause)
        {
            return false;
        }

        // DROP 13: the state-machine transition and the gate call used to be two separate critical
        // sections, so an overlapping TryPause/TryResume pair on the same id could interleave as
        // Pause-applies, Resume-applies, Resume-gate, Pause-gate - leaving State == Running with the
        // gate closed, and the worker parked in WaitWhilePaused(Async) forever with no cancellation
        // involved to rescue it (PTK-004 only protects the cancel-vs-pause case, not this one). Both
        // now happen under the same lock, so the trigger and the gate it drives can never land in two
        // different orders for two overlapping callers.
        lock (entry.Gate)
        {
            if (!entry.Operation.TryApply(OperationTrigger.Pause, _clock.UtcNow))
            {
                return false;
            }

            entry.Pause.Pause();
        }

        Raise(entry);
        return true;
    }

    public bool TryResume(OperationId id)
    {
        if (!_entries.TryGetValue(id.Value, out var entry))
        {
            return false;
        }

        // See the matching note in TryPause: the trigger and the gate call are one critical section,
        // not two, so this can never land "between" an overlapping TryPause's own two steps.
        lock (entry.Gate)
        {
            if (!entry.Operation.TryApply(OperationTrigger.Resume, _clock.UtcNow))
            {
                return false;
            }

            entry.Pause.Resume();
        }

        Raise(entry);
        return true;
    }

    /// <summary>
    /// Asks the operation to stop, and says so immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state moves to <see cref="OperationState.Cancelling"/> before the token is signalled, so the
    /// screen can answer the click at once. It becomes <see cref="OperationState.Cancelled"/> only when
    /// the worker has actually unwound — which is what makes "cancellation is complete" a fact the UI
    /// reads rather than a guess it makes after a delay.
    /// </para>
    /// <para>
    /// A terminal operation returns false rather than pretending: the state machine refuses the
    /// transition, and cancelling something that already finished is not a cancellation.
    /// </para>
    /// </remarks>
    public bool TryCancel(OperationId id)
    {
        if (!_entries.TryGetValue(id.Value, out var entry))
        {
            return false;
        }

        if (!Apply(entry, OperationTrigger.RequestCancel))
        {
            return false;
        }

        // Release the pause gate first, or a paused operation would never observe the cancellation.
        entry.Pause.Resume();
        entry.Cancellation.Cancel();
        return true;
    }

    public async Task<OperationSnapshot> WaitForCompletionAsync(OperationId id, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(id.Value, out var entry))
        {
            throw new KeyNotFoundException($"No operation with id '{id}'.");
        }

        var runner = entry.Runner;
        if (runner is not null)
        {
            await runner.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return entry.ToSnapshot();
    }

    /// <summary>
    /// Drops every finished operation and disposes what it held.
    /// </summary>
    /// <remarks>
    /// Terminal states only. A running or paused operation owns a cancellation source and a pause gate
    /// that its worker is still holding, so removing it would leave the work running with nothing able
    /// to stop it — the exact opposite of what a queue is for.
    /// </remarks>
    public int RemoveFinished()
    {
        var removed = 0;

        foreach (var pair in _entries)
        {
            OperationState state;
            lock (pair.Value.Gate)
            {
                state = pair.Value.Operation.State;
            }

            if (state is not (OperationState.Completed or OperationState.Cancelled or OperationState.Failed))
            {
                continue;
            }

            if (_entries.TryRemove(pair.Key, out var entry))
            {
                entry.Dispose();
                removed++;
            }
        }

        return removed;
    }

    private async Task RunAsync(Entry entry, OperationRequest request, Task predecessor)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            entry.Cancellation.Token,
            _shutdown.Token);

        bool acquiredDuringShutdown;
        try
        {
            // Everything enqueued earlier has reached the slot gate before this one asks for a slot,
            // so a free slot goes to the operation that asked for it first. Never awaited with this
            // entry's token: a cancelled operation must not be able to strand the ones behind it, and
            // its own admission is released in the finally below either way.
            await predecessor.ConfigureAwait(false);
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);

            // SemaphoreSlim.WaitAsync races "a slot became available" against "the token fired"
            // internally, and either can win; during shutdown the slot this entry just took can only
            // have come from another entry's cancellation unwinding (DisposeAsync cancels _shutdown
            // before awaiting any runner), so a successful acquisition here does not by itself mean
            // this entry is clear to start.
            //
            // This checks the source tokens directly rather than linked.Token: CreateLinkedTokenSource
            // propagates a source's cancellation to the linked token via a registered callback, and
            // that callback had not always finished running by the time WaitAsync's own race was
            // decided — linked.Token.IsCancellationRequested could still read false here for a few
            // more scheduler steps after _shutdown.IsCancellationRequested was already true. Checking
            // the source directly has no such lag: CancelAsync/Cancel set it before either returns.
            // Found as a repeated-run failure (DisposeAsync leaving a queued operation Completed
            // instead of Cancelled roughly one run in five with a single slot and one entry already
            // running), the same way OPQ-019 was found, and the first fix attempt — checking
            // linked.Token.IsCancellationRequested instead of the sources — looked right and still
            // left the same flake measurable across repeated runs.
            acquiredDuringShutdown = _shutdown.IsCancellationRequested || entry.Cancellation.IsCancellationRequested;
            if (acquiredDuringShutdown)
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Apply(entry, OperationTrigger.Cancel);
            return;
        }
        finally
        {
            // Released once this entry holds a slot or has given up trying — either way the next
            // operation may now compete, and the chain cannot deadlock on an entry that never ran.
            entry.Admitted?.TrySetResult();
        }

        if (acquiredDuringShutdown)
        {
            Apply(entry, OperationTrigger.Cancel);
            return;
        }

        try
        {
            if (!Apply(entry, OperationTrigger.Start))
            {
                return;
            }

            // DROP 13: `Progress<T>` built inside a `Task.Run` runner has no captured
            // `SynchronizationContext`, so each `Report()` posts independently to the thread pool
            // with no ordering guarantee between them - under contention a later report's work item
            // can run before an earlier one's. `SequencedProgress` assigns the sequence number on the
            // producer's own thread, in call order, before posting, so a late-arriving stale report
            // can be told apart from a genuinely newer one and never regresses `entry.LastProgress`
            // (the field `ToSnapshot` actually reads). Every report still reaches `ProgressReported`
            // in whatever order its work item happens to run — fully serialising that too would need
            // a dedicated single-consumer dispatch queue, which is tracked as a documented limitation
            // rather than built here under this drop's time budget.
            var progress = new SequencedProgress(entry, p => ProgressReported?.Invoke(this, p));

            var context = new OperationContext(entry.Operation.Id, entry.Operation.Type, progress, entry.Pause);
            await request.Work(context, linked.Token).ConfigureAwait(false);
            Apply(entry, OperationTrigger.Complete);
        }
        catch (OperationCanceledException)
        {
            Apply(entry, OperationTrigger.Cancel);
        }
        catch (ArchiveOperationException ex)
        {
            Apply(entry, OperationTrigger.Fail, ex.Error);
        }
        catch (Exception ex)
        {
            // A non-archive exception escaping a handler is a defect: record it as a typed backend
            // failure so the user still gets an operation id, and log it for diagnosis.
            _logger.LogError(ex, "Operation {OperationId} failed unexpectedly.", entry.Operation.Id.Value);
            Apply(
                entry,
                OperationTrigger.Fail,
                ArchiveError.Create(ArchiveErrorCode.BackendFailure, ex.Message, inner: ex));
        }
        finally
        {
            _slots.Release();
        }
    }

    private bool Apply(Entry entry, OperationTrigger trigger, ArchiveError? error = null)
    {
        bool applied;
        lock (entry.Gate)
        {
            applied = entry.Operation.TryApply(trigger, _clock.UtcNow, error);
        }

        if (applied)
        {
            Raise(entry);
        }

        return applied;
    }

    private void Raise(Entry entry) => OperationChanged?.Invoke(this, entry.ToSnapshot());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Taken under the same lock Enqueue uses for its own disposed-check-and-insert, so this
        // snapshot either includes or strictly postdates every entry that will ever get a runner.
        Entry[] snapshot;
        lock (_lifecycleGate)
        {
            snapshot = [.. _entries.Values];
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var entry in snapshot)
        {
            entry.Pause.Resume();
            if (entry.Runner is { } runner)
            {
                try
                {
                    await runner.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            entry.Dispose();
        }

        _shutdown.Dispose();
        _slots.Dispose();
    }

    private sealed class Entry : IDisposable
    {
        public Entry(ArchiveOperation operation, string? destination, bool supportsPause)
        {
            Operation = operation;
            Destination = destination;
            SupportsPause = supportsPause;
        }

        /// <summary>Completed once this entry has reached the slot gate; the next entry waits on it.</summary>
        public TaskCompletionSource? Admitted { get; set; }

        public ArchiveOperation Operation { get; }

        /// <summary>Where this operation writes, if it writes anywhere. Fixed at enqueue.</summary>
        public string? Destination { get; }

        /// <summary>Whether this kind of work can be suspended. Fixed at enqueue.</summary>
        public bool SupportsPause { get; }

        public Lock Gate { get; } = new();

        public PauseToken Pause { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Runner { get; set; }

        public OperationProgress? LastProgress { get; set; }

        /// <summary>The sequence number of whichever report last won <see cref="LastProgress"/>.</summary>
        public long LastAppliedProgressSequence { get; set; }

        private long _progressSequence;

        /// <summary>Hands out sequence numbers in call order; see <see cref="SequencedProgress"/>.</summary>
        public long NextProgressSequence() => Interlocked.Increment(ref _progressSequence);

        public OperationSnapshot ToSnapshot()
        {
            lock (Gate)
            {
                return new OperationSnapshot(
                    Operation.Id,
                    Operation.Type,
                    Operation.State,
                    Operation.Description,
                    LastProgress,
                    Operation.Error,
                    Operation.StartedAt,
                    Operation.FinishedAt,
                    Destination,
                    SupportsPause);
            }
        }

        public void Dispose()
        {
            Pause.Dispose();
            Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Posts each report to the thread pool the same way a context-less <see cref="Progress{T}"/>
    /// already does (so this changes ordering guarantees, not threading behaviour), but captures a
    /// sequence number on the reporting thread, in call order, before posting - the one place call
    /// order is still known once the report crosses to a pool thread that offers none.
    /// </summary>
    private sealed class SequencedProgress : IProgress<OperationProgress>
    {
        private readonly Entry _entry;
        private readonly Action<OperationProgress> _onReported;

        public SequencedProgress(Entry entry, Action<OperationProgress> onReported)
        {
            _entry = entry;
            _onReported = onReported;
        }

        public void Report(OperationProgress value)
        {
            var sequence = _entry.NextProgressSequence();
            ThreadPool.QueueUserWorkItem(
                static state => state.Self.Apply(state.Sequence, state.Value),
                (Self: this, Sequence: sequence, Value: value),
                preferLocal: false);
        }

        private void Apply(long sequence, OperationProgress value)
        {
            // A later-numbered report that lands first already told `LastProgress` the truth; a
            // stale one arriving after must not be allowed to tell it a smaller one instead. The
            // report still reaches every subscriber below regardless - only the snapshot field's
            // monotonicity is guarded here (see the DROP 13 remark where this is constructed).
            lock (_entry.Gate)
            {
                if (sequence > _entry.LastAppliedProgressSequence)
                {
                    _entry.LastAppliedProgressSequence = sequence;
                    _entry.LastProgress = value;
                }
            }

            _onReported(value);
        }
    }
}
