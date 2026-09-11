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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = _ids.Next(request.Type);
        var entry = new Entry(
            new ArchiveOperation(id, request.Type, request.Description),
            request.Destination,
            request.SupportsPause);
        _entries[id.Value] = entry;
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

        lock (entry.Gate)
        {
            if (!entry.Operation.TryApply(OperationTrigger.Pause, _clock.UtcNow))
            {
                return false;
            }
        }

        entry.Pause.Pause();
        Raise(entry);
        return true;
    }

    public bool TryResume(OperationId id)
    {
        if (!_entries.TryGetValue(id.Value, out var entry))
        {
            return false;
        }

        lock (entry.Gate)
        {
            if (!entry.Operation.TryApply(OperationTrigger.Resume, _clock.UtcNow))
            {
                return false;
            }
        }

        entry.Pause.Resume();
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

        try
        {
            // Everything enqueued earlier has reached the slot gate before this one asks for a slot,
            // so a free slot goes to the operation that asked for it first. Never awaited with this
            // entry's token: a cancelled operation must not be able to strand the ones behind it, and
            // its own admission is released in the finally below either way.
            await predecessor.ConfigureAwait(false);
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
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

        try
        {
            if (!Apply(entry, OperationTrigger.Start))
            {
                return;
            }

            var progress = new Progress<OperationProgress>(p =>
            {
                entry.LastProgress = p;
                ProgressReported?.Invoke(this, p);
            });

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

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var entry in _entries.Values)
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
}
