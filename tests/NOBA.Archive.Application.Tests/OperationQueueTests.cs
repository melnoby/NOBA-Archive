using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Application.Tests;

public sealed class OperationQueueTests
{
    private static OperationQueue NewQueue(int concurrency = 1) =>
        new(
            new OperationIdFactory(new FakeClock()),
            new FakeClock(),
            new NullLogger<OperationQueue>(),
            new OperationQueueOptions { MaxConcurrentOperations = concurrency });

    [Fact]
    public async Task Runs_an_operation_to_completion()
    {
        await using var queue = NewQueue();
        var ran = false;

        var id = queue.Enqueue(new OperationRequest(OperationType.Test, "test", (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        }));

        var snapshot = await queue.WaitForCompletionAsync(id, TestContext.Token);

        Assert.True(ran);
        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public async Task Assigns_a_correlation_id_of_the_documented_shape()
    {
        await using var queue = NewQueue();

        var id = queue.Enqueue(new OperationRequest(OperationType.Extract, "extract", static (_, _) => Task.CompletedTask));
        await queue.WaitForCompletionAsync(id, TestContext.Token);

        Assert.StartsWith("EX-", id.Value);
    }

    [Fact]
    public async Task Translates_an_archive_error_into_a_failed_operation()
    {
        await using var queue = NewQueue();

        var id = queue.Enqueue(new OperationRequest(OperationType.Extract, "extract", static (_, _) =>
            throw new ArchiveOperationException(
                ArchiveError.Create(ArchiveErrorCode.DiskFull, "Required 18.4 GB, available 7.2 GB"))));

        var snapshot = await queue.WaitForCompletionAsync(id, TestContext.Token);

        Assert.Equal(OperationState.Failed, snapshot.State);
        Assert.NotNull(snapshot.Error);
        Assert.Equal(ArchiveErrorCode.DiskFull, snapshot.Error.Code);
    }

    /// <summary>An unexpected exception must still surface as a typed failure with an operation id.</summary>
    [Fact]
    public async Task Converts_an_unexpected_exception_into_a_backend_failure()
    {
        await using var queue = NewQueue();

        var id = queue.Enqueue(new OperationRequest(OperationType.Create, "create", static (_, _) =>
            throw new InvalidOperationException("something unforeseen")));

        var snapshot = await queue.WaitForCompletionAsync(id, TestContext.Token);

        Assert.Equal(OperationState.Failed, snapshot.State);
        Assert.NotNull(snapshot.Error);
        Assert.Equal(ArchiveErrorCode.BackendFailure, snapshot.Error.Code);
    }

    [Fact]
    public async Task Cancellation_reaches_the_work_and_ends_in_the_cancelled_state()
    {
        await using var queue = NewQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = queue.Enqueue(new OperationRequest(OperationType.Extract, "long", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));

        await started.Task;
        Assert.True(queue.TryCancel(id));

        var snapshot = await queue.WaitForCompletionAsync(id, TestContext.Token);
        Assert.Equal(OperationState.Cancelled, snapshot.State);
    }

    [Fact]
    public async Task Pause_and_resume_move_through_the_state_machine()
    {
        await using var queue = NewQueue();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = queue.Enqueue(new OperationRequest(OperationType.Extract, "pausable", async (ctx, ct) =>
        {
            running.TrySetResult();
            await release.Task.WaitAsync(ct);
            await ctx.PauseToken.WaitWhilePausedAsync(ct);
        }));

        await running.Task;
        Assert.True(queue.TryPause(id));
        Assert.Equal(OperationState.Paused, queue.Snapshot().Single(s => s.Id == id).State);

        Assert.True(queue.TryResume(id));
        Assert.Equal(OperationState.Running, queue.Snapshot().Single(s => s.Id == id).State);

        release.TrySetResult();
        var snapshot = await queue.WaitForCompletionAsync(id, TestContext.Token);
        Assert.Equal(OperationState.Completed, snapshot.State);
    }

    [Fact]
    public async Task Honours_the_concurrency_limit()
    {
        await using var queue = NewQueue(concurrency: 1);
        var concurrent = 0;
        var peak = 0;
        var gate = new SemaphoreSlim(0);

        var ids = new List<Core.Primitives.OperationId>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(queue.Enqueue(new OperationRequest(OperationType.Test, "concurrent", async (_, ct) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), now));
                await gate.WaitAsync(ct);
                Interlocked.Decrement(ref concurrent);
            })));
        }

        gate.Release(3);
        foreach (var id in ids)
        {
            await queue.WaitForCompletionAsync(id, TestContext.Token);
        }

        Assert.Equal(1, peak);
        gate.Dispose();
    }

    [Fact]
    public async Task Cancelling_an_unknown_operation_is_not_an_error()
    {
        await using var queue = NewQueue();

        Assert.False(queue.TryCancel(Core.Primitives.OperationId.Create(OperationType.Test, DateTimeOffset.UnixEpoch, 9)));
    }

    /// <summary>
    /// The snapshot carries the timing the operation has always recorded.
    /// </summary>
    /// <remarks>
    /// <c>ArchiveOperation</c> has stamped StartedAt and FinishedAt since the queue was written, and
    /// nothing outside could read them - so a screen wanting to say how long an extraction took had the
    /// answer computed for it and no way to reach it.
    /// </remarks>
    [Fact]
    public async Task A_finished_operation_reports_when_it_ran()
    {
        await using var queue = NewQueue();
        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "a.7z", static (_, _) => Task.CompletedTask));

        await queue.WaitForCompletionAsync(id, CancellationToken.None);
        var snapshot = queue.Snapshot().Single(s => s.Id == id);

        Assert.NotNull(snapshot.StartedAt);
        Assert.NotNull(snapshot.FinishedAt);
        Assert.NotNull(snapshot.Duration);
        Assert.True(snapshot.Duration >= TimeSpan.Zero);
    }

    /// <summary>A running operation has started and not finished, so it has no duration yet.</summary>
    [Fact]
    public async Task A_running_operation_has_no_duration()
    {
        await using var queue = NewQueue();
        var gate = new TaskCompletionSource();
        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "a.7z", (_, ct) => gate.Task.WaitAsync(ct)));

        OperationSnapshot? running = null;
        for (var i = 0; i < 100 && running?.State != OperationState.Running; i++)
        {
            running = queue.Snapshot().SingleOrDefault(s => s.Id == id);
            await Task.Delay(10, TestContext.Token);
        }

        Assert.Equal(OperationState.Running, running!.State);
        Assert.Null(running.Duration);

        gate.SetResult();
        await queue.WaitForCompletionAsync(id, CancellationToken.None);
    }

    /// <summary>Finished operations can be forgotten, and the queue stops growing for ever.</summary>
    [Fact]
    public async Task Finished_operations_can_be_removed()
    {
        await using var queue = NewQueue();
        var first = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "a.7z", static (_, _) => Task.CompletedTask));
        var second = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "b.7z", static (_, _) => Task.CompletedTask));

        await queue.WaitForCompletionAsync(first, CancellationToken.None);
        await queue.WaitForCompletionAsync(second, CancellationToken.None);

        Assert.Equal(2, queue.RemoveFinished());
        Assert.Empty(queue.Snapshot());
        Assert.Equal(0, queue.RemoveFinished());
    }

    /// <summary>
    /// Removing finished work never touches work that is still going.
    /// </summary>
    /// <remarks>
    /// A running entry owns the cancellation source and the pause gate its worker is holding. Removing
    /// it would leave the work running with nothing able to stop it, which is the exact opposite of
    /// what a queue exists for.
    /// </remarks>
    [Fact]
    public async Task Removing_finished_leaves_running_work_alone()
    {
        await using var queue = NewQueue(concurrency: 2);
        var gate = new TaskCompletionSource();

        var done = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "done.7z", static (_, _) => Task.CompletedTask));
        var running = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "running.7z", (_, ct) => gate.Task.WaitAsync(ct)));

        await queue.WaitForCompletionAsync(done, CancellationToken.None);

        Assert.Equal(1, queue.RemoveFinished());
        Assert.Single(queue.Snapshot());
        Assert.Equal(running, queue.Snapshot()[0].Id);
        Assert.True(queue.TryCancel(running), "the surviving operation must still be controllable");

        gate.TrySetResult();
    }

    /// <summary>
    /// A second operation waits its turn, and says so.
    /// </summary>
    /// <remarks>
    /// The default concurrency is one, because compression already saturates every core. That makes
    /// Queued a state a user will actually see, not a theoretical one.
    /// </remarks>
    [Fact]
    public async Task A_second_operation_reports_itself_queued()
    {
        await using var queue = NewQueue(concurrency: 1);
        var gate = new TaskCompletionSource();

        var first = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "first.7z", (_, ct) => gate.Task.WaitAsync(ct)));
        var second = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "second.7z", static (_, _) => Task.CompletedTask));

        OperationSnapshot? waiting = null;
        for (var i = 0; i < 100; i++)
        {
            waiting = queue.Snapshot().SingleOrDefault(s => s.Id == second);
            if (waiting?.State == OperationState.Queued && queue.Snapshot().Single(s => s.Id == first).State == OperationState.Running)
            {
                break;
            }

            await Task.Delay(10, TestContext.Token);
        }

        Assert.Equal(OperationState.Queued, waiting!.State);

        gate.SetResult();
        await queue.WaitForCompletionAsync(second, CancellationToken.None);
        Assert.Equal(OperationState.Completed, queue.Snapshot().Single(s => s.Id == second).State);
    }

    /// <summary>
    /// OPQ-021: operations start in the order they were enqueued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deterministic, and it has to be: the defect it covers was a race that surfaced roughly once in
    /// several full suite runs as <c>OPQ-019</c> finding its second operation already Completed. Every
    /// enqueue starts its runner on the thread pool, and with no admission chain the pool decides which
    /// of them reaches the slot gate first — so the queue honoured "one at a time" while quietly
    /// running the newest job first.
    /// </para>
    /// <para>
    /// Twenty operations rather than two, because two would agree with the old behaviour half the time.
    /// Each one records its own position on arrival and none can finish before the ones behind it have
    /// been enqueued, so the recorded order is the admission order and nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Operations_start_in_the_order_they_were_enqueued()
    {
        await using var queue = NewQueue(concurrency: 1);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var ids = new List<NOBA.Archive.Core.Primitives.OperationId>();

        for (var i = 0; i < 20; i++)
        {
            var position = i;
            ids.Add(queue.Enqueue(new OperationRequest(
                OperationType.Extract,
                $"job-{position}.7z",
                async (_, ct) =>
                {
                    lock (order)
                    {
                        order.Add(position);
                    }

                    // Held until every operation has been enqueued, so nothing can complete and free a
                    // slot while the rest are still arriving.
                    await gate.Task.WaitAsync(ct);
                })));
        }

        gate.SetResult();

        foreach (var id in ids)
        {
            await queue.WaitForCompletionAsync(id, TestContext.Token);
        }

        lock (order)
        {
            Assert.Equal(20, order.Count);
            Assert.Equal(string.Join(",", Enumerable.Range(0, 20)), string.Join(",", order));
        }
    }

    /// <summary>
    /// OPQ-022: cancelling is two states, and the first one is visible immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deterministic without a sleep. The work parks on a gate that does <em>not</em> observe the
    /// token, so requesting cancellation cannot possibly have unwound it yet — whatever the scheduler
    /// does, the state read on the next line is the state the user would see after clicking Cancel.
    /// Releasing the gate then lets the work notice the token and finish unwinding.
    /// </para>
    /// <para>
    /// The point is the gap between the two. Extraction closes a file and deletes a temporary before it
    /// returns; on a large entry that is seconds during which the old queue said Running, which reads
    /// as "your click did nothing".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cancelling_is_reported_before_the_work_has_stopped()
    {
        await using var queue = NewQueue();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "big.7z",
            async (_, ct) =>
            {
                running.TrySetResult();

                // Deliberately not passed the token: this is the file the writer is in the middle of.
                await hold.Task;
                ct.ThrowIfCancellationRequested();
            }));

        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Token);

        Assert.True(queue.TryCancel(id));
        Assert.Equal(OperationState.Cancelling, queue.Snapshot().Single(s => s.Id == id).State);

        // Not finished, and the queue will not pretend it is: clearing must leave it alone.
        Assert.Equal(0, queue.RemoveFinished());

        // A second press changes nothing, and says so rather than restarting anything.
        Assert.False(queue.TryCancel(id), "cancelling twice must not be accepted twice");

        hold.SetResult();
        await queue.WaitForCompletionAsync(id, TestContext.Token);

        Assert.Equal(OperationState.Cancelled, queue.Snapshot().Single(s => s.Id == id).State);
    }

    /// <summary>
    /// OPQ-023: work that runs to the end after Cancel was pressed is reported Cancelled, not Completed.
    /// </summary>
    /// <remarks>
    /// The race this closes is real and it is the one that matters most in this product: the user
    /// presses Cancel, the last entry happens to finish a millisecond later, and the operation reports
    /// Completed with a full set of figures. They would be entitled to believe the extraction is
    /// theirs to trust — and to delete the archive. Here the work ignores the token entirely and
    /// returns normally, which is the worst case, and the ending is still Cancelled.
    /// </remarks>
    [Fact]
    public async Task Work_that_finishes_after_a_cancel_is_still_cancelled()
    {
        await using var queue = NewQueue();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "stubborn.7z",
            async (_, _) =>
            {
                running.TrySetResult();
                await hold.Task;

                // Returns normally. The token is never looked at.
            }));

        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Token);

        Assert.True(queue.TryCancel(id));
        hold.SetResult();

        await queue.WaitForCompletionAsync(id, TestContext.Token);

        var snapshot = queue.Snapshot().Single(s => s.Id == id);
        Assert.Equal(OperationState.Cancelled, snapshot.State);
        Assert.NotEqual(OperationState.Completed, snapshot.State);
    }

    /// <summary>OPQ-024: cancelling something already finished is refused rather than pretended.</summary>
    [Fact]
    public async Task A_finished_operation_cannot_be_cancelled()
    {
        await using var queue = NewQueue();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract, "done.7z", static (_, _) => Task.CompletedTask));

        await queue.WaitForCompletionAsync(id, TestContext.Token);

        Assert.False(queue.TryCancel(id));
        Assert.Equal(OperationState.Completed, queue.Snapshot().Single(s => s.Id == id).State);
    }

    /// <summary>
    /// OPQ-020: the queue refuses to pause an operation that cannot be paused.
    /// </summary>
    /// <remarks>
    /// Testing an archive hands the whole job to the backend: there is no copy loop to stand in and no
    /// pause token to await. ADR-0007 requires a pause we cannot honour to be reported as unavailable
    /// rather than accepted and ignored, and the refusal belongs here — a screen deciding it for itself
    /// is exactly how the badge and the disk came to disagree on the batch extraction path.
    /// </remarks>
    [Fact]
    public async Task An_operation_that_cannot_pause_refuses_to_pause()
    {
        await using var queue = NewQueue();

        var gate = new TaskCompletionSource();
        var id = queue.Enqueue(new OperationRequest(
            OperationType.Test,
            "a.7z",
            async (_, _) => await gate.Task)
        {
            SupportsPause = false,
        });

        for (var i = 0; i < 200 && queue.Snapshot().Single(s => s.Id == id).State != OperationState.Running; i++)
        {
            await Task.Delay(10, TestContext.Token);
        }

        Assert.Equal(OperationState.Running, queue.Snapshot().Single(s => s.Id == id).State);

        Assert.False(queue.TryPause(id), "a test must not accept a pause it cannot honour");
        Assert.Equal(OperationState.Running, queue.Snapshot().Single(s => s.Id == id).State);
        Assert.False(queue.Snapshot().Single(s => s.Id == id).SupportsPause);

        // Still stoppable, which is the half that must keep working.
        Assert.True(queue.TryCancel(id));
        gate.SetResult();
        await queue.WaitForCompletionAsync(id, CancellationToken.None);
    }
}
