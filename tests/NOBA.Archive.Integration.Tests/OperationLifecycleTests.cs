using Microsoft.Extensions.DependencyInjection;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Backends.SevenZip;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The operations path end to end: <c>OPX-001…008</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about operations is tested against a fake seam. These go through the composed
/// engine and a real archive on disk, because the questions here cannot be answered by a fake: does
/// the resource guard accept a large-but-honest archive, does pausing actually stop bytes landing on
/// disk, does cancelling actually stop them for good.
/// </para>
/// <para>
/// They exist because a fixture built for an earlier drop was refused by the guard at 748:1 and the
/// whole Windows session was spent looking at a Failed row. The archive was the bomb, not the bug —
/// but nothing on this side would have told us before shipping it.
/// </para>
/// </remarks>
public sealed class OperationLifecycleTests
{
    /// <summary>
    /// OPX-001: a large archive with an honest ratio is accepted and extracts.
    /// </summary>
    /// <remarks>
    /// Roughly 30:1 — the shape of logs or documents, and well under the 500:1 total-expansion limit.
    /// This is the fixture the operations screen is meant to be exercised with.
    /// </remarks>
    [Fact]
    public async Task A_large_archive_with_an_honest_ratio_is_accepted()
    {
        using var sandbox = new TempDirectory("OPX-001");
        var archive = SafeLargeArchive(sandbox, out var expectedFiles);
        await using var engine = new EngineFixture();

        var destination = sandbox.Combine("out");
        var outcome = await engine.ExtractAsync(archive, destination, cancellationToken: TestContext.Token);

        Assert.Equal(expectedFiles, outcome.EntriesWritten);
        Assert.Empty(outcome.Blocked);
        Assert.True(outcome.BytesWritten.Bytes > 0);
    }

    /// <summary>
    /// OPX-002: an archive with an extreme ratio is still refused.
    /// </summary>
    /// <remarks>
    /// The counterpart to OPX-001, and the reason the fixture was rebuilt rather than the guard
    /// relaxed. Nothing about making the tests pass may make this archive extractable.
    /// </remarks>
    [Fact]
    public async Task An_archive_with_an_extreme_ratio_is_refused()
    {
        using var sandbox = new TempDirectory("OPX-002");
        var archive = BombArchive(sandbox);
        await using var engine = new EngineFixture();

        var failure = await Assert.ThrowsAsync<Core.Errors.ArchiveOperationException>(
            async () => await engine.ExtractAsync(archive, sandbox.Combine("out"), cancellationToken: TestContext.Token));

        Assert.Equal(Core.Errors.ArchiveErrorCode.BombSuspected, failure.Error.Code);
        Assert.Contains("expands", failure.Error.TechnicalDetail);
    }

    /// <summary>
    /// OPX-003: a refusal is recorded as a failed operation, and the queue stays usable.
    /// </summary>
    /// <remarks>
    /// This is what Windows showed: the operation registers, runs, and fails at zero percent with the
    /// guard's own sentence. What matters is that the queue is not left in a state that stops the next
    /// operation from working.
    /// </remarks>
    [Fact]
    public async Task A_refused_archive_becomes_a_failed_operation_without_breaking_the_queue()
    {
        using var sandbox = new TempDirectory("OPX-003");
        var bomb = BombArchive(sandbox);
        var safe = SafeLargeArchive(sandbox, out var expectedFiles);

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var refused = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "bomb.7z",
            (context, ct) => extract.HandleAsync(
                Request(bomb, sandbox.Combine("bomb-out")), context.Id, context.Progress, context.PauseToken, ct)));

        var snapshot = await queue.WaitForCompletionAsync(refused, CancellationToken.None);

        Assert.Equal(OperationState.Failed, snapshot.State);
        Assert.NotNull(snapshot.Error);
        Assert.NotNull(snapshot.StartedAt);
        Assert.NotNull(snapshot.FinishedAt);

        // The queue must still work afterwards, which is the whole point of the assertion.
        var accepted = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "safe.7z",
            (context, ct) => extract.HandleAsync(
                Request(safe, sandbox.Combine("safe-out")), context.Id, context.Progress, context.PauseToken, ct)));

        var second = await queue.WaitForCompletionAsync(accepted, CancellationToken.None);

        Assert.Equal(OperationState.Completed, second.State);
        Assert.Equal(expectedFiles, Directory.GetFiles(sandbox.Combine("safe-out"), "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>OPX-004: an accepted extraction reports progress that advances.</summary>
    [Fact]
    public async Task An_accepted_extraction_reports_advancing_progress()
    {
        using var sandbox = new TempDirectory("OPX-004");
        var archive = SafeLargeArchive(sandbox, out _);
        await using var engine = new EngineFixture();

        var reports = new List<OperationProgress>();
        var queue = engine.Get<IOperationQueue>();
        queue.ProgressReported += (_, p) =>
        {
            lock (reports)
            {
                reports.Add(p);
            }
        };

        var extract = engine.Get<ExtractArchiveHandler>();
        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "safe.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("out")), context.Id, context.Progress, context.PauseToken, ct)));

        await queue.WaitForCompletionAsync(id, CancellationToken.None);

        lock (reports)
        {
            Assert.True(reports.Count > 1, $"only {reports.Count} progress report(s)");
            Assert.True(reports[^1].BytesDone > reports[0].BytesDone, "bytes never advanced");
            Assert.True(reports[^1].ItemsDone > 0, "no item was ever reported");
            Assert.NotNull(reports[^1].BytesTotal);
        }
    }

    /// <summary>
    /// OPX-005: pausing stops bytes reaching the disk — on the path a real 7z actually takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous version of this test passed against a ZIP, and a ZIP session reports
    /// <c>SupportsBatchExtraction == false</c>. So it exercised the per-entry fallback loop, which has
    /// checked the pause token since the queue was written, and said nothing whatsoever about the
    /// batch path a solid 7z takes — the path that had no check at all. It was green for eight days
    /// while pausing a 3,800-entry extraction on Windows moved a badge and nothing else.
    /// </para>
    /// <para>
    /// Two things therefore have to be true before the assertion means anything, and both are asserted
    /// rather than assumed: the archive must be one the backend batches (OPX-009 pins that), and the
    /// extraction must already be writing when the pause arrives. The old helper waited two seconds for
    /// the first byte and then gave up <em>silently</em>, so a pause landing during the listing pass —
    /// where the token has always been honoured — counted as proof.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Pausing_stops_bytes_reaching_the_disk_on_the_batch_path()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("OPX-005");
        var archive = await BatchArchiveAsync();
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "batch.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct)));

        // Deep enough into the write phase that the listing pass is provably behind us.
        await WaitForWriting(destination, MidExtractionBytes);
        Assert.True(queue.TryPause(id), "the operation refused to pause");

        // Let anything already in flight land, then measure across a real interval.
        await Task.Delay(500, TestContext.Token);
        var settled = BytesOnDisk(destination);
        await Task.Delay(800, TestContext.Token);

        Assert.Equal(settled, BytesOnDisk(destination));
        Assert.Equal(OperationState.Paused, queue.Snapshot().Single(s => s.Id == id).State);

        // And it must not have quietly finished behind the pause, which would make the equality above
        // true for the least interesting reason there is.
        Assert.True(
            settled < BatchArchiveBytes,
            $"the extraction had already written all {BatchArchiveBytes} bytes before the pause");

        Assert.True(queue.TryResume(id), "the operation refused to resume");
        var final = await queue.WaitForCompletionAsync(id, CancellationToken.None);

        Assert.Equal(OperationState.Completed, final.State);
        Assert.True(BytesOnDisk(destination) > settled, "resuming wrote nothing further");
        Assert.Equal(BatchArchiveFileCount, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>
    /// OPX-006: cancelling stops the writing, on the batch path, and it does not resume.
    /// </summary>
    /// <remarks>
    /// Same correction as OPX-005, and the same reason. Cancel additionally has to <em>end</em> the
    /// extraction rather than merely starve it of destinations: declining every remaining entry keeps
    /// the disk clean but leaves 7-Zip decoding the rest of a solid block with nowhere to put it, so
    /// the operation would sit in Running for seconds after the button was pressed. The final assertion
    /// is that the entry count at the moment of cancellation is also the final one.
    /// </remarks>
    [Fact]
    public async Task Cancelling_stops_the_writing_for_good_on_the_batch_path()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("OPX-006");
        var archive = await BatchArchiveAsync();
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "batch.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct)));

        await WaitForWriting(destination, MidExtractionBytes);

        Assert.True(queue.TryCancel(id), "the operation refused to cancel");

        var snapshot = await queue.WaitForCompletionAsync(id, CancellationToken.None);
        Assert.Equal(OperationState.Cancelled, snapshot.State);

        var atCancellation = BytesOnDisk(destination);
        var atCancellationFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length;
        await Task.Delay(800, TestContext.Token);

        Assert.Equal(atCancellation, BytesOnDisk(destination));
        Assert.True(
            atCancellationFiles < BatchArchiveFileCount,
            "everything was written anyway, so the cancel did nothing");

        // Stopped, and stayed stopped: the entry count at the moment of cancellation is the final one.
        // A wall-clock bound was tried here and removed - measured both ways it was 0.3s either side,
        // so it discriminated between nothing and would have failed one day under load instead.
        Assert.Equal(atCancellationFiles, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
    }

    /// <summary>
    /// OPX-009: the archive these tests use really does take the batch path.
    /// </summary>
    /// <remarks>
    /// This is the control the previous suite lacked. OPX-005 and OPX-006 are only meaningful against a
    /// session that batches, and nothing in their bodies could tell the difference — a ZIP produced the
    /// same green tick while testing an entirely different loop. The contrast is asserted in both
    /// directions so that neither fixture can drift into the other's role unnoticed.
    /// </remarks>
    [Fact]
    public async Task The_batch_path_is_the_one_under_test()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        await using var engine = new EngineFixture();

        Assert.True(
            await BatchesAsync(engine, await BatchArchiveAsync(), ArchiveFormat.SevenZip),
            "the 7z fixture is not batched, so OPX-005 and OPX-006 would be testing the fallback loop");

        using var sandbox = new TempDirectory("OPX-009");
        Assert.False(
            await BatchesAsync(engine, SafeLargeArchive(sandbox, out _), ArchiveFormat.Zip),
            "the ZIP fixture is now batched too, so OPX-013 no longer covers the fallback loop");
    }

    /// <summary>
    /// OPX-010: a cancelled extraction is reported as cancelled, not as a damaged archive.
    /// </summary>
    /// <remarks>
    /// The abort reaches 7-Zip as a failed write callback, and <c>Extract</c> answers with a failure
    /// HRESULT that is indistinguishable from real corruption. Reporting that verbatim would tell
    /// somebody who had just pressed Cancel that their archive was broken.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_extraction_is_not_reported_as_a_damaged_archive()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("OPX-010");
        var archive = await BatchArchiveAsync();
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "batch.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct)));

        await WaitForWriting(destination, MidExtractionBytes);
        Assert.True(queue.TryCancel(id));

        var snapshot = await queue.WaitForCompletionAsync(id, CancellationToken.None);

        Assert.Equal(OperationState.Cancelled, snapshot.State);
        Assert.NotEqual(OperationState.Failed, snapshot.State);
        Assert.Null(snapshot.Error);
    }

    /// <summary>
    /// OPX-011: cancelled and completed operations are both final, both removable, and the running one is not.
    /// </summary>
    /// <remarks>
    /// This is the "Clear finished" contract as the screen uses it: a cancelled row is a record, not a
    /// failure to be hidden, and clearing must take the finished rows without disturbing whatever is
    /// still in flight.
    /// </remarks>
    [Fact]
    public async Task Cancelled_and_completed_operations_clear_together_and_the_running_one_stays()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("OPX-011");
        var archive = await BatchArchiveAsync();

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var completed = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "one.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("one")), context.Id, context.Progress, context.PauseToken, ct)));
        Assert.Equal(OperationState.Completed, (await queue.WaitForCompletionAsync(completed, CancellationToken.None)).State);

        var cancelled = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "two.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("two")), context.Id, context.Progress, context.PauseToken, ct)));
        await WaitForWriting(sandbox.Combine("two"), MidExtractionBytes);
        Assert.True(queue.TryCancel(cancelled));
        Assert.Equal(OperationState.Cancelled, (await queue.WaitForCompletionAsync(cancelled, CancellationToken.None)).State);

        var running = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "three.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("three")), context.Id, context.Progress, context.PauseToken, ct)));
        await WaitForWriting(sandbox.Combine("three"), MidExtractionBytes);

        Assert.Equal(2, queue.RemoveFinished());
        Assert.DoesNotContain(queue.Snapshot(), s => s.Id == completed);
        Assert.DoesNotContain(queue.Snapshot(), s => s.Id == cancelled);
        Assert.Contains(queue.Snapshot(), s => s.Id == running);

        Assert.True(queue.TryCancel(running));
        await queue.WaitForCompletionAsync(running, CancellationToken.None);
    }

    /// <summary>
    /// OPX-012: the expansion guard refuses a batched archive before a byte is written.
    /// </summary>
    /// <remarks>
    /// The guard's pre-flight check reads the archive's declared totals and runs before the extraction
    /// loop of either path — but "either path" is exactly the assumption this drop found to be false
    /// elsewhere, so it is now checked against a 7z rather than inferred from the ZIP case.
    /// </remarks>
    [Fact]
    public async Task The_expansion_guard_refuses_a_batched_archive_before_writing()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("OPX-012");
        var bomb = await SevenZipBombAsync(sandbox);
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<Core.Errors.ArchiveOperationException>(
            async () => await engine.ExtractAsync(bomb, destination, cancellationToken: TestContext.Token));

        Assert.Equal(Core.Errors.ArchiveErrorCode.BombSuspected, failure.Error.Code);
        Assert.Equal(0, BytesOnDisk(destination));
    }

    /// <summary>
    /// OPX-013: pausing also stops the fallback path, which is what a ZIP takes.
    /// </summary>
    /// <remarks>
    /// What OPX-005 used to prove, kept and labelled honestly. Two extraction paths mean two
    /// guarantees, and the drop that broke one of them did so precisely because the other was covered.
    /// </remarks>
    [Fact]
    public async Task Pausing_stops_bytes_reaching_the_disk_on_the_fallback_path()
    {
        using var sandbox = new TempDirectory("OPX-013");
        var archive = SafeLargeArchive(sandbox, out _);
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "safe.zip",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct)));

        await WaitForWriting(destination, MidExtractionBytes);
        Assert.True(queue.TryPause(id), "the operation refused to pause");

        await Task.Delay(500, TestContext.Token);
        var settled = BytesOnDisk(destination);
        await Task.Delay(800, TestContext.Token);

        Assert.Equal(settled, BytesOnDisk(destination));
        Assert.Equal(OperationState.Paused, queue.Snapshot().Single(s => s.Id == id).State);

        Assert.True(queue.TryResume(id), "the operation refused to resume");
        var final = await queue.WaitForCompletionAsync(id, CancellationToken.None);

        Assert.Equal(OperationState.Completed, final.State);
        Assert.True(BytesOnDisk(destination) > settled, "resuming wrote nothing further");
    }

    /// <summary>OPX-007: finished operations can be forgotten; running ones cannot.</summary>
    [Fact]
    public async Task Finished_operations_can_be_removed_and_running_ones_cannot()
    {
        using var sandbox = new TempDirectory("OPX-007");
        var archive = SafeLargeArchive(sandbox, out _);

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var first = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "one.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("one")), context.Id, context.Progress, context.PauseToken, ct)));

        await queue.WaitForCompletionAsync(first, CancellationToken.None);

        var second = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "two.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("two")), context.Id, context.Progress, context.PauseToken, ct)));

        await WaitForWriting(sandbox.Combine("two"), MidExtractionBytes);

        Assert.Equal(1, queue.RemoveFinished());
        Assert.DoesNotContain(queue.Snapshot(), s => s.Id == first);
        Assert.Contains(queue.Snapshot(), s => s.Id == second);

        await queue.WaitForCompletionAsync(second, CancellationToken.None);
    }

    /// <summary>OPX-008: a completed operation reports a duration the screen can show.</summary>
    [Fact]
    public async Task A_completed_operation_reports_a_duration()
    {
        using var sandbox = new TempDirectory("OPX-008");
        var archive = SafeLargeArchive(sandbox, out _);

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "safe.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, sandbox.Combine("out")), context.Id, context.Progress, context.PauseToken, ct)));

        var snapshot = await queue.WaitForCompletionAsync(id, CancellationToken.None);

        Assert.Equal(OperationState.Completed, snapshot.State);
        Assert.NotNull(snapshot.Duration);
        Assert.True(snapshot.Duration >= TimeSpan.Zero);
    }

    /// <summary>
    /// OPX-014: cancelling reaches inside a single large entry, not only between entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checking pause and cancel once per entry looks like coverage and is worth nothing to the person
    /// extracting a disc image: one entry means one check, taken before any of it was written. This
    /// fixture is three entries — enough for the backend to batch it — of which one holds almost all
    /// the bytes, so the cancel necessarily lands inside it.
    /// </para>
    /// <para>
    /// <b>What this does not prove.</b> It cannot isolate the per-write gate from the per-entry one.
    /// Both were tried as the only mechanism and this test passed either way, because neither
    /// observable can tell them apart: the half-written entry is abandoned, so its temporary file is
    /// deleted before the assertion runs, and byte progress is only reported when an entry
    /// <em>completes</em>, so a cancel inside the large one leaves it at zero in both designs. The
    /// per-write gate is here on the strength of the argument for it, not of this test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Cancelling_stops_inside_a_single_large_entry()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("OPX-014");
        var archive = await OneBigEntryArchiveAsync();
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "one-big.7z",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct)));

        await WaitForWriting(destination, MidExtractionBytes);
        Assert.True(queue.TryCancel(id), "the operation refused to cancel");

        var snapshot = await queue.WaitForCompletionAsync(id, CancellationToken.None);
        Assert.Equal(OperationState.Cancelled, snapshot.State);

        var landed = BytesOnDisk(destination);
        await Task.Delay(800, TestContext.Token);
        Assert.Equal(landed, BytesOnDisk(destination));

        Assert.True(
            landed < BigEntryBytes,
            $"{landed} of {BigEntryBytes} bytes are still on disk, so nothing stopped");
    }

    /// <summary>
    /// OPX-015: the queue records where an operation writes, so a finished one can be found.
    /// </summary>
    /// <remarks>
    /// The destination is chosen in a folder picker and was, until this drop, recorded nowhere: not on
    /// the operation, not in the snapshot, not on the screen. A whole Windows gate was spent inspecting
    /// an empty folder that had never been a destination at all, and nothing in the product could have
    /// said so. An operation that writes somewhere has to be able to answer where.
    /// </remarks>
    [Fact]
    public async Task An_operation_records_where_it_writes()
    {
        using var sandbox = new TempDirectory("OPX-015");
        var archive = SafeLargeArchive(sandbox, out _);
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "safe.zip",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct))
        {
            Destination = destination,
        });

        // Available while it runs, not only once it is over: the point is to find the files.
        Assert.Equal(destination, queue.Snapshot().Single(s => s.Id == id).Destination);

        var snapshot = await queue.WaitForCompletionAsync(id, CancellationToken.None);
        Assert.Equal(destination, snapshot.Destination);
    }

    /// <summary>
    /// OPX-016: cancelling the fallback path stops the writing too.
    /// </summary>
    /// <remarks>
    /// The mirror of OPX-006, on the loop a ZIP takes. Both paths are one behaviour to a user and two
    /// implementations underneath, and this suite already learned once what happens when only one of
    /// them is covered.
    /// </remarks>
    [Fact]
    public async Task Cancelling_stops_the_writing_on_the_fallback_path()
    {
        using var sandbox = new TempDirectory("OPX-016");
        var archive = SafeLargeArchive(sandbox, out var expectedFiles);
        var destination = sandbox.Combine("out");

        await using var engine = new EngineFixture();
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();

        var id = queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            "safe.zip",
            (context, ct) => extract.HandleAsync(
                Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct)));

        await WaitForWriting(destination, MidExtractionBytes);
        Assert.True(queue.TryCancel(id), "the operation refused to cancel");

        var snapshot = await queue.WaitForCompletionAsync(id, CancellationToken.None);
        Assert.Equal(OperationState.Cancelled, snapshot.State);

        var atCancellation = BytesOnDisk(destination);
        var filesAtCancellation = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length;
        await Task.Delay(800, TestContext.Token);

        Assert.Equal(atCancellation, BytesOnDisk(destination));
        Assert.Equal(filesAtCancellation, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        Assert.True(filesAtCancellation < expectedFiles, "everything was written anyway");
    }

    /// <summary>
    /// OPX-017: DROP 09 — two real creations, enqueued on the real queue with two slots, both actually
    /// run at the same time, and neither borrows a byte from the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other test above runs one real operation at a time; <c>OperationQueueTests</c>
    /// (Application.Tests) proves the scheduler's admission and overlap logic against fake delegates.
    /// Neither proves that <see cref="CreateArchiveHandler"/> — resolved once as a singleton by
    /// <c>AddNobaArchiveEngine</c>, like <see cref="IArchiveProviderRegistry"/> and
    /// <see cref="IFormatDetector"/> beneath it — tolerates two callers using it at the same moment.
    /// </para>
    /// <para>
    /// The rendezvous below is deliberate, not a nicety: both operations must call
    /// <c>ArriveAndWaitAsync</c> before either proceeds into the real handler, so a queue that quietly
    /// serialised them — contrary to the two slots it was configured with — would make this test hang
    /// and time out rather than pass by a timing accident.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_real_creations_run_concurrently_without_cross_talk()
    {
        using var sandbox = new TempDirectory("OPX-017");
        var fileA = sandbox.Combine("a", "alpha.txt");
        var fileB = sandbox.Combine("b", "beta.txt");
        Directory.CreateDirectory(sandbox.Combine("a"));
        Directory.CreateDirectory(sandbox.Combine("b"));
        await File.WriteAllTextAsync(fileA, "payload-alpha", TestContext.Token);
        await File.WriteAllTextAsync(fileB, "payload-beta", TestContext.Token);
        var archiveA = sandbox.Combine("a.zip");
        var archiveB = sandbox.Combine("b.zip");

        await using var engine = new EngineFixture(password: null, maxConcurrentOperations: 2);
        var queue = engine.Get<IOperationQueue>();
        var create = engine.Get<CreateArchiveHandler>();
        var rendezvous = new Rendezvous(2);

        Func<OperationContext, CancellationToken, Task> Work(string target, string source) => async (context, ct) =>
        {
            await rendezvous.ArriveAndWaitAsync(ct);
            await create.HandleAsync(
                new CreateRequest
                {
                    TargetPath = target,
                    Sources = [source],
                    Options = CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Balanced),
                },
                context.Id,
                context.Progress,
                context.PauseToken,
                ct);
        };

        var idA = queue.Enqueue(new OperationRequest(OperationType.Create, "a.zip", Work(archiveA, fileA)));
        var idB = queue.Enqueue(new OperationRequest(OperationType.Create, "b.zip", Work(archiveB, fileB)));

        var snapshotA = await queue.WaitForCompletionAsync(idA, TestContext.Token);
        var snapshotB = await queue.WaitForCompletionAsync(idB, TestContext.Token);

        Assert.Equal(OperationState.Completed, snapshotA.State);
        Assert.Equal(OperationState.Completed, snapshotB.State);

        var listA = await engine.ListAsync(archiveA, TestContext.Token);
        var listB = await engine.ListAsync(archiveB, TestContext.Token);
        Assert.Equal(["alpha.txt"], listA.Entries.Where(static e => !e.IsDirectory).Select(static e => e.DisplayPath));
        Assert.Equal(["beta.txt"], listB.Entries.Where(static e => !e.IsDirectory).Select(static e => e.DisplayPath));
    }

    /// <summary>
    /// OPX-018: DROP 09 — two real extractions, from two different archives into two different
    /// destinations, run concurrently and land only in their own folder.
    /// </summary>
    /// <remarks>
    /// The destinations are checked both ways: archive A's file must be the only thing under A's folder,
    /// and B's the only thing under B's — a shared buffer or a mis-captured closure in the handler would
    /// most plausibly show up as one file quietly appearing in, or vanishing from, the wrong folder.
    /// </remarks>
    [Fact]
    public async Task Two_real_extractions_run_concurrently_without_cross_talk()
    {
        using var sandbox = new TempDirectory("OPX-018");
        var archiveA = SafeLargeArchive(sandbox, out _);

        var sourceB = sandbox.Combine("source-b");
        Directory.CreateDirectory(sourceB);
        await File.WriteAllTextAsync(Path.Combine(sourceB, "only-in-b.txt"), "b-content", TestContext.Token);
        var archiveB = sandbox.Combine("b.zip");
        await using (var builder = new EngineFixture())
        {
            await builder.CreateAsync(archiveB, [sourceB], cancellationToken: TestContext.Token);
        }

        var destinationA = sandbox.Combine("out-a");
        var destinationB = sandbox.Combine("out-b");

        await using var engine = new EngineFixture(password: null, maxConcurrentOperations: 2);
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();
        var rendezvous = new Rendezvous(2);

        Func<OperationContext, CancellationToken, Task> Work(string archive, string destination) => async (context, ct) =>
        {
            await rendezvous.ArriveAndWaitAsync(ct);
            await extract.HandleAsync(Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct);
        };

        var idA = queue.Enqueue(new OperationRequest(OperationType.Extract, "a.zip", Work(archiveA, destinationA)));
        var idB = queue.Enqueue(new OperationRequest(OperationType.Extract, "b.zip", Work(archiveB, destinationB)));

        var snapshotA = await queue.WaitForCompletionAsync(idA, TestContext.Token);
        var snapshotB = await queue.WaitForCompletionAsync(idB, TestContext.Token);

        Assert.Equal(OperationState.Completed, snapshotA.State);
        Assert.Equal(OperationState.Completed, snapshotB.State);

        var aFiles = Directory.GetFiles(destinationA, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        var bFiles = Directory.GetFiles(destinationB, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.False(aFiles.Contains("only-in-b.txt"), "b's file landed in a's destination");
        Assert.Equal(["only-in-b.txt"], bFiles);
    }

    /// <summary>
    /// OPX-019: DROP 09 — a real operation failing concurrently does not disturb a sibling that is
    /// genuinely running at the same moment.
    /// </summary>
    /// <remarks>
    /// The sibling is held at the rendezvous until the failing one has arrived too, so the failure is
    /// recorded while the sibling's own real extraction is still in flight — not safely after it. The
    /// per-id dictionaries this test exercises (<c>_outcomes</c> / <c>_testResults</c> in
    /// <c>EngineWorkspace</c>, DEBT-06) cannot themselves be built or referenced from this project —
    /// <c>NOBA.Archive.App</c> is Windows-only — so this proves the identical pattern production code
    /// actually uses (an outcome parked by operation id, written from concurrent queue-driven work, read
    /// once by the caller that awaited it) rather than the App-layer class itself.
    /// </remarks>
    [Fact]
    public async Task A_failing_real_operation_does_not_disturb_a_concurrent_real_sibling()
    {
        using var sandbox = new TempDirectory("OPX-019");
        var archive = SafeLargeArchive(sandbox, out var expectedFiles);
        var destination = sandbox.Combine("out");
        var missingArchive = sandbox.Combine("does-not-exist.zip");

        await using var engine = new EngineFixture(password: null, maxConcurrentOperations: 2);
        var queue = engine.Get<IOperationQueue>();
        var extract = engine.Get<ExtractArchiveHandler>();
        var rendezvous = new Rendezvous(2);

        var failing = queue.Enqueue(new OperationRequest(OperationType.Extract, "missing.zip", async (context, ct) =>
        {
            await rendezvous.ArriveAndWaitAsync(ct);
            await extract.HandleAsync(
                Request(missingArchive, sandbox.Combine("missing-out")), context.Id, context.Progress, context.PauseToken, ct);
        }));
        var sibling = queue.Enqueue(new OperationRequest(OperationType.Extract, "safe.zip", async (context, ct) =>
        {
            await rendezvous.ArriveAndWaitAsync(ct);
            await extract.HandleAsync(Request(archive, destination), context.Id, context.Progress, context.PauseToken, ct);
        }));

        var failedSnapshot = await queue.WaitForCompletionAsync(failing, TestContext.Token);
        var siblingSnapshot = await queue.WaitForCompletionAsync(sibling, TestContext.Token);

        Assert.Equal(OperationState.Failed, failedSnapshot.State);
        Assert.NotNull(failedSnapshot.Error);

        Assert.Equal(OperationState.Completed, siblingSnapshot.State);
        Assert.Equal(expectedFiles, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
    }

    // ---------- fixtures ----------

    /// <summary>Every participant must arrive before any may proceed. DROP 09's concurrency tests use
    /// this instead of a sleep: a queue that fails to run them side by side makes the test hang and time
    /// out, rather than pass on a timing accident.</summary>
    private sealed class Rendezvous(int participants)
    {
        private int _arrived;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ArriveAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == participants)
            {
                _gate.TrySetResult();
            }

            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ExtractRequest Request(string archivePath, string destination) =>
        new()
        {
            ArchivePath = archivePath,
            Options = new ExtractionOptions { DestinationRoot = destination },
        };

    /// <summary>
    /// A ZIP big enough to watch, at a ratio the guard is happy with.
    /// </summary>
    /// <remarks>
    /// Text diluted with incompressible bytes. The dilution is the point: pure repeated text compresses
    /// hundreds to one and trips the bomb guard, which is exactly the mistake this file exists to stop
    /// anyone repeating.
    /// </remarks>
    /// <summary>Files in <see cref="SafeLarge"/>. Held here so the assertions and the builder agree.</summary>
    private const int SafeLargeFileCount = 60;

    /// <summary>
    /// A ZIP big enough to watch, at a ratio the guard is happy with, built once for the whole class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built once because three of these tests need an extraction that is still running when they act
    /// on it, and rebuilding two hundred megabytes per test would make the suite slower than the
    /// behaviour it is checking.
    /// </para>
    /// <para>
    /// Text diluted with incompressible bytes. The dilution is the point: pure repeated text compresses
    /// hundreds to one and trips the bomb guard, which is exactly the mistake this file exists to stop
    /// anyone repeating.
    /// </para>
    /// </remarks>
    private static readonly Lazy<string> SafeLarge = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "noba-opx-shared");
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "safe-large.zip");
        if (File.Exists(archive))
        {
            return archive;
        }

        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);

        var random = new Random(11);
        var text = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("2026-08-26T10:00:00Z INFO archive worker handled a record. ", 8)));

        var made = 0;
        foreach (var folder in new[] { "set-00", "set-01", "تقارير" })
        {
            var directory = Path.Combine(source, folder);
            Directory.CreateDirectory(directory);

            for (var i = 0; i < SafeLargeFileCount / 3; i++)
            {
                var name = folder == "تقارير" ? $"تقرير-{i}.txt" : $"log-{i}.txt";
                using var stream = File.Create(Path.Combine(directory, name));
                var noise = new byte[24];
                for (var block = 0; block < 9_000; block++)
                {
                    stream.Write(text);
                    random.NextBytes(noise);
                    stream.Write(noise);
                }

                made++;
            }
        }

        System.IO.Compression.ZipFile.CreateFromDirectory(
            source, archive, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(source, recursive: true);
        _ = made;
        return archive;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string SafeLargeArchive(TempDirectory sandbox, out int fileCount)
    {
        _ = sandbox;
        fileCount = SafeLargeFileCount;
        return SafeLarge.Value;
    }

    /// <summary>An archive whose declared totals are absurd, which the guard must refuse.</summary>
    private static string BombArchive(TempDirectory sandbox)
    {
        var source = sandbox.Combine("bomb-source");
        Directory.CreateDirectory(source);

        // Nothing but repetition: this is what compresses hundreds to one.
        var zeros = new byte[1024 * 1024];
        for (var i = 0; i < 8; i++)
        {
            using var stream = File.Create(Path.Combine(source, $"zeros-{i}.bin"));
            for (var block = 0; block < 12; block++)
            {
                stream.Write(zeros);
            }
        }

        var archive = sandbox.Combine("bomb.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, archive);
        Directory.Delete(source, recursive: true);
        return archive;
    }

    /// <summary>
    /// How much has landed, tolerating files that vanish mid-count.
    /// </summary>
    /// <remarks>
    /// The extraction writer writes to a temp name and renames on success, so a path returned by
    /// <c>GetFiles</c> can be gone by the time its length is asked for. That is the writer being
    /// careful, not a fault, and a counter that threw on it would be testing its own naivety.
    /// </remarks>
    private static long BytesOnDisk(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
                // Renamed out from under us between the listing and the measurement.
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return total;
    }

    // ---------- the batch fixture ----------

    /// <summary>Skips rather than lies when the native backend is not installed.</summary>
    private static bool RequireNativeBackend()
    {
        if (SevenZipLibraryLocator.Locate() is not null)
        {
            return true;
        }

        Assert.Skip(
            "The 7-Zip native library is not installed, so the batch extraction path cannot be exercised. Searched:"
            + Environment.NewLine + SevenZipLibraryLocator.DescribeSearch());
        return false;
    }

    private const int BatchArchiveFileCount = 1_200;
    private const int BatchArchiveFileBytes = 64 * 1024;
    private const long BatchArchiveBytes = (long)BatchArchiveFileCount * BatchArchiveFileBytes;

    /// <summary>
    /// How far in the pause and cancel tests wait before acting.
    /// </summary>
    /// <remarks>
    /// Enough bytes that the listing pass is certainly finished and the batch loop is certainly
    /// writing, and few enough that most of the archive is still to come — otherwise "the bytes
    /// stopped" and "the extraction had finished" are the same observation.
    /// </remarks>
    private const long MidExtractionBytes = 4L * 1024 * 1024;

    /// <summary>
    /// A 7Z the backend batches: many entries, an honest ratio, built once for the whole class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Many small entries rather than a few large ones, because per-entry cost is what makes the
    /// extraction last long enough to interrupt — and because the batch loop's whole reason to exist is
    /// entry count. 1,200 × 64 KiB is 75 MB written across 1,200 files, which takes seconds.
    /// </para>
    /// <para>
    /// Every 4 KiB block carries 64 bytes of randomness, which caps the ratio near 64:1 by
    /// construction — far below the 500:1 the guard refuses. The fixture is large because it holds real
    /// bytes, not because it expands, and nothing here may ever be relaxed to make a test pass.
    /// </para>
    /// </remarks>
    private static readonly Lazy<Task<string>> BatchArchive = new(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "noba-opx-batch");
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "batch-large.7z");
        if (File.Exists(archive))
        {
            return archive;
        }

        var source = Path.Combine(root, "source");
        if (Directory.Exists(source))
        {
            Directory.Delete(source, recursive: true);
        }

        var folders = new[] { "logs", "data", "تقارير الأداء", "سجلات النظام" };
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(source, folder));
        }

        var random = new Random(29);
        var line = System.Text.Encoding.ASCII.GetBytes(
            "2026-08-26T10:00:00Z INFO noba.archive.extract entry state=ok checksum=verified padding ");
        var template = new byte[BatchArchiveFileBytes];
        for (var offset = 0; offset < template.Length; offset += line.Length)
        {
            line.AsSpan(0, Math.Min(line.Length, template.Length - offset)).CopyTo(template.AsSpan(offset));
        }

        var buffer = new byte[BatchArchiveFileBytes];
        var noise = new byte[64];
        for (var i = 0; i < BatchArchiveFileCount; i++)
        {
            var folder = folders[i % folders.Length];
            var name = folder.Length > 0 && char.IsAscii(folder[0]) ? $"log-{i:D5}.txt" : $"تقرير-{i:D5}.log";

            template.CopyTo(buffer, 0);
            for (var offset = 0; offset < buffer.Length; offset += 4096)
            {
                random.NextBytes(noise);
                noise.CopyTo(buffer, offset);
            }

            await File.WriteAllBytesAsync(Path.Combine(source, folder, name), buffer);
        }

        await using (var engine = new EngineFixture())
        {
            await engine.CreateAsync(
                archive,
                [source],
                CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));
        }

        Directory.Delete(source, recursive: true);
        return archive;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private const long BigEntryBytes = 96L * 1024 * 1024;

    /// <summary>
    /// A batched 7Z whose bytes are almost all in one entry, so a cancel has to land inside it.
    /// </summary>
    private static readonly Lazy<Task<string>> OneBigEntryArchive = new(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "noba-opx-bigentry");
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "one-big.7z");
        if (File.Exists(archive))
        {
            return archive;
        }

        var source = Path.Combine(root, "source");
        if (Directory.Exists(source))
        {
            Directory.Delete(source, recursive: true);
        }

        Directory.CreateDirectory(source);

        var random = new Random(37);
        var block = new byte[1024 * 1024];
        var line = System.Text.Encoding.ASCII.GetBytes("noba archive large entry padding block ");
        for (var offset = 0; offset < block.Length; offset += line.Length)
        {
            line.AsSpan(0, Math.Min(line.Length, block.Length - offset)).CopyTo(block.AsSpan(offset));
        }

        var noise = new byte[64];
        await using (var big = File.Create(Path.Combine(source, "disc-image.bin")))
        {
            for (var written = 0L; written < BigEntryBytes; written += block.Length)
            {
                for (var offset = 0; offset < block.Length; offset += 4096)
                {
                    random.NextBytes(noise);
                    noise.CopyTo(block, offset);
                }

                await big.WriteAsync(block);
            }
        }

        // Two more so the backend batches: one entry would take the fallback loop instead.
        await File.WriteAllTextAsync(Path.Combine(source, "readme.txt"), "small");
        await File.WriteAllTextAsync(Path.Combine(source, "ملاحظات.txt"), "صغير");

        await using (var engine = new EngineFixture())
        {
            await engine.CreateAsync(
                archive,
                [source],
                CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));
        }

        Directory.Delete(source, recursive: true);
        return archive;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Task<string> OneBigEntryArchiveAsync() => OneBigEntryArchive.Value;

    private static Task<string> BatchArchiveAsync() => BatchArchive.Value;

    /// <summary>A 7Z whose declared totals are absurd, so the guard is exercised on the batched format.</summary>
    private static async Task<string> SevenZipBombAsync(TempDirectory sandbox)
    {
        var source = sandbox.Combine("bomb7z-source");
        Directory.CreateDirectory(source);

        var zeros = new byte[1024 * 1024];
        for (var i = 0; i < 8; i++)
        {
            await using var stream = File.Create(Path.Combine(source, $"zeros-{i}.bin"));
            for (var block = 0; block < 12; block++)
            {
                await stream.WriteAsync(zeros);
            }
        }

        var archive = sandbox.Combine("bomb.7z");
        await using (var engine = new EngineFixture())
        {
            await engine.CreateAsync(
                archive,
                [source],
                CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));
        }

        Directory.Delete(source, recursive: true);
        return archive;
    }

    /// <summary>Asks the backend itself whether it batches this archive, rather than assuming.</summary>
    private static async Task<bool> BatchesAsync(EngineFixture engine, string archivePath, ArchiveFormat format)
    {
        var provider = engine.Get<IArchiveProviderRegistry>().Resolve(format, ArchiveCapabilities.Extract);
        Assert.NotNull(provider);

        // Through IFileSystem and as a stream, because the native backend refuses a path outright:
        // ADR-0005 is enforced at its own front door, not only in the extraction writer.
        await using var content = engine.Get<IFileSystem>().OpenRead(archivePath);
        await using var session = await provider!.OpenAsync(
            ArchiveSource.FromStream(content, Path.GetFileName(archivePath)),
            new OpenOptions { ForceFormat = format },
            new NoPassword(),
            TestContext.Token);

        return session.SupportsBatchExtraction;
    }

    private sealed class NoPassword : IPasswordProvider
    {
        public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Password?>(null);
    }

    /// <summary>
    /// Waits until at least <paramref name="atLeast"/> bytes have landed, and fails if they never do.
    /// </summary>
    /// <remarks>
    /// The helper this replaces waited for one byte, for two seconds, and then returned as if it had
    /// succeeded. Everything downstream of it was measuring a phase of the extraction nobody had
    /// chosen. A wait that can expire silently is not a wait, it is a delay with a good story.
    /// </remarks>
    private static async Task WaitForWriting(string destination, long atLeast)
    {
        long bytes = 0;
        for (var i = 0; i < 1_500; i++)
        {
            bytes = BytesOnDisk(destination);
            if (bytes >= atLeast)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(
            bytes >= atLeast,
            $"only {bytes} byte(s) had been written to '{destination}' after 15s; " +
            $"the test needs at least {atLeast} to be sure the write phase is under way");
    }
}
