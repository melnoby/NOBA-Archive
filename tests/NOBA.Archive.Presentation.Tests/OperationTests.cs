using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Feature 02, slice 1: <c>OPS-001…012</c>.
/// </summary>
/// <remarks>
/// <para>
/// The queue itself is already covered in the application suite. What is new — and what these pin — is
/// that an operation is now something the UI can <em>address</em>: it has an id the moment it starts,
/// its state comes from the engine rather than from a screen's guess, and pause, resume and cancel
/// reach it.
/// </para>
/// <para>
/// The sharpest assertion here is <c>OPS-004</c>. An indeterminate operation must stay indeterminate
/// all the way to the surface; the moment a view model helpfully substitutes a zero, the engine's care
/// over <c>double?</c> has been thrown away and the bar starts lying.
/// </para>
/// </remarks>
public sealed class OperationTests
{
    private static readonly LocalizedStrings Arabic = LocalizedStrings.For(UiLanguage.Arabic);
    private static readonly LocalizedStrings English = LocalizedStrings.For(UiLanguage.English);

    /// <summary>OPS-001: starting an extraction registers an operation that can be acted on at once.</summary>
    [Fact]
    public async Task An_extraction_becomes_an_addressable_operation()
    {
        var (shell, workspace) = await Opened();

        var extract = shell.Browse.ExtractAsync("/out", selectionOnly: false, cancellationToken: TestContext.Token).AsTask();
        await extract;

        Assert.Single(workspace.Started);
        Assert.NotNull(shell.Operations.Active);
        Assert.Equal(workspace.Started[0], shell.Operations.Active!.Id);
    }

    /// <summary>OPS-002: the state comes from the queue, and the screen never sets it.</summary>
    [Fact]
    public async Task State_arrives_from_the_engine()
    {
        var (shell, workspace) = await Opened();
        await shell.Browse.ExtractAsync("/out", selectionOnly: false, cancellationToken: TestContext.Token);

        var operation = shell.Operations.Active!;
        Assert.Equal(OperationState.Completed, operation.State);
        Assert.True(operation.IsFinished);
        Assert.False(operation.CanCancel);
    }

    /// <summary>OPS-003: progress reaches the operation and is readable.</summary>
    [Fact]
    public void Progress_reaches_the_operation()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        workspace.Report(new OperationProgress
        {
            Id = id,
            Type = OperationType.Extract,
            State = OperationState.Running,
            CurrentItem = "photos/one.jpg",
            ItemsDone = 3,
            ItemsTotal = 10,
            BytesDone = 512,
            BytesTotal = 2048,
            AverageBytesPerSecond = 256,
        });

        Assert.Equal(25d, operation.Percent);
        Assert.False(operation.IsIndeterminate);
        Assert.Equal("photos/one.jpg", operation.CurrentItem);
        Assert.Equal(3, operation.ItemsDone);
    }

    /// <summary>
    /// OPS-004: an unmeasurable operation stays unmeasurable. Never a helpful zero.
    /// </summary>
    /// <remarks>
    /// A solid archive cannot report a total until the block ends. The engine models that as a null
    /// percentage on purpose (ADR-0007); a view model that turned it into <c>0</c> would produce a bar
    /// that sits at the far left and looks stuck, which is a worse lie than admitting ignorance.
    /// </remarks>
    [Fact]
    public void An_unmeasurable_operation_never_reports_a_percentage()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("solid.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "solid.7z");

        workspace.Report(new OperationProgress
        {
            Id = id,
            Type = OperationType.Extract,
            State = OperationState.Running,
            BytesDone = 4096,
            BytesTotal = null,
            ItemsTotal = null,
            AverageBytesPerSecond = 1024,
        });

        Assert.Null(operation.Percent);
        Assert.True(operation.IsIndeterminate);

        var measures = OperationFormatter.MeasuresOf(operation, English);
        Assert.Contains(measures, p => p.Text == English.OperationIndeterminate);
        Assert.DoesNotContain(measures, static p => p.Text.Contains('%'));
    }

    /// <summary>OPS-005: pause, resume and cancel reach the queue and move the state.</summary>
    [Fact]
    public void Pause_resume_and_cancel_reach_the_engine()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        Assert.True(operation.CanPause);
        Assert.True(operations.Pause(id));
        Assert.Equal(OperationState.Paused, operation.State);
        Assert.True(operation.CanResume);
        Assert.False(operation.CanPause);

        Assert.True(operations.Resume(id));
        Assert.Equal(OperationState.Running, operation.State);

        // Two steps now, and the first one is what the person sees when they click. A cancellation
        // that jumped straight to Cancelled would be claiming the writer had stopped before it had.
        Assert.True(operations.Cancel(id));
        Assert.Equal(OperationState.Cancelling, operation.State);
        Assert.True(operation.IsCancelling);
        Assert.False(operation.IsFinished, "an operation that is still stopping has not finished");
        Assert.False(operation.CanCancel, "a second cancel cannot make it stop sooner");
        Assert.True(workspace.Cancelled.Contains(id), "the cancel never reached the queue");

        workspace.FinishCancel(id);
        Assert.Equal(OperationState.Cancelled, operation.State);
        Assert.True(operation.IsFinished);
    }

    /// <summary>OPS-006: a transition the state machine forbids is refused, not faked.</summary>
    [Fact]
    public void An_illegal_transition_is_refused()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        Assert.False(operations.Resume(id), "a running operation cannot be resumed");
        Assert.Equal(OperationState.Running, operation.State);

        operations.Cancel(id);
        Assert.False(operations.Pause(id), "a cancelled operation cannot be paused");
        Assert.False(operations.Cancel(id), "cancelling twice is not a second cancellation");
    }

    /// <summary>OPS-007: a cancelled extraction yields no outcome, and reports no error either.</summary>
    [Fact]
    public async Task A_cancelled_extraction_produces_no_outcome()
    {
        var (shell, workspace) = await Opened();
        var id = workspace.StartExtract("a.7z", "/out", null);
        workspace.TryCancel(id);

        var outcome = await workspace.WaitForExtractAsync(id, CancellationToken.None);

        Assert.Null(outcome);
    }

    /// <summary>OPS-008: the measures never put a digit and an Arabic letter in one fragment.</summary>
    [Fact]
    public void The_measures_never_mix_a_digit_with_arabic()
    {
        var operation = Running(out _);

        foreach (var part in OperationFormatter.MeasuresOf(operation, Arabic))
        {
            var digit = part.Text.Any(char.IsDigit);
            var arabic = part.Text.Any(static c => c is >= '؀' and <= 'ۿ');
            Assert.False(digit && arabic, $"mixed fragment: '{part.Text}'");
        }
    }

    /// <summary>OPS-009: the remaining time is a clock, in both languages, with no letters in it.</summary>
    [Fact]
    public void The_remaining_time_is_a_bare_clock()
    {
        var operation = Running(out _);

        foreach (var text in new[] { Arabic, English })
        {
            var parts = OperationFormatter.MeasuresOf(operation, text);
            var clock = parts.FirstOrDefault(static p => p.Text.Contains(':'));
            Assert.NotNull(clock);
            Assert.DoesNotContain(clock!.Text, char.IsLetter);
        }
    }

    /// <summary>OPS-010: the entry being worked on is isolated, so a hostile name cannot reorder the line.</summary>
    [Fact]
    public void The_current_entry_is_isolated()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        workspace.Report(new OperationProgress
        {
            Id = id,
            Type = OperationType.Extract,
            State = OperationState.Running,
            CurrentItem = "docs/تقرير.txt",
        });

        var shown = OperationFormatter.CurrentItemOf(operation);
        Assert.StartsWith(BidiText.LeftToRightIsolate.ToString(), shown);
        Assert.EndsWith(BidiText.PopDirectionalIsolate.ToString(), shown);
    }

    /// <summary>OPS-011: every state has a name in both languages.</summary>
    [Fact]
    public void Every_state_is_named_in_both_languages()
    {
        foreach (var state in Enum.GetValues<OperationState>())
        {
            foreach (var text in new[] { Arabic, English })
            {
                var label = OperationFormatter.StateLabel(state, text);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{state} has no label");
                Assert.DoesNotContain(".", label);
            }
        }
    }

    /// <summary>OPS-012: a report addressed to another operation is ignored.</summary>
    [Fact]
    public void A_report_for_another_operation_is_ignored()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var mine = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(mine, OperationType.Extract, "a.7z");
        var other = workspace.StartExtract("b.7z", "/out", null);

        workspace.Report(new OperationProgress
        {
            Id = other,
            Type = OperationType.Extract,
            State = OperationState.Running,
            BytesDone = 999,
            BytesTotal = 1000,
        });

        Assert.Equal(0, operation.BytesDone);
    }

    // ---------- helpers ----------

    private static OperationViewModel Running(out FakeWorkspace workspace)
    {
        workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        workspace.Report(new OperationProgress
        {
            Id = id,
            Type = OperationType.Extract,
            State = OperationState.Running,
            CurrentItem = "photos/one.jpg",
            ItemsDone = 1_842,
            ItemsTotal = 2_970,
            BytesDone = 2_587_000_000,
            BytesTotal = 4_166_000_000,
            AverageBytesPerSecond = 193_000_000,
            Elapsed = TimeSpan.FromSeconds(13),
        });

        return operation;
    }

    private static async Task<(ShellViewModel Shell, FakeWorkspace Workspace)> Opened()
    {
        var workspace = new FakeWorkspace
        {
            Listing = new(
                Core.Formats.ArchiveFormat.SevenZip,
                new Core.Formats.ArchiveStatistics { FileCount = 2 },
                [FakeWorkspace.File("one.jpg", 4096)]),
        };

        var shell = new ShellViewModel(workspace);
        await shell.Browse.OpenAsync("a.7z");
        return (shell, workspace);
    }

    /// <summary>OPS-013: the list reads newest first, and the order does not depend on a dictionary.</summary>
    [Fact]
    public void The_list_reads_newest_first()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);

        var first = workspace.StartExtract("first.7z", "/out", null);
        operations.Track(first, OperationType.Extract, "first.7z");
        var second = workspace.StartExtract("second.7z", "/out", null);
        operations.Track(second, OperationType.Extract, "second.7z");

        Assert.True(operations.HasAny);
        Assert.Equal(["second.7z", "first.7z"], operations.All.Select(static o => o.Description).ToArray());
    }

    /// <summary>OPS-014: an operation the queue reports but nobody started is still tracked.</summary>
    /// <remarks>
    /// The screen must show work it did not begin — an operation started from another screen, or one
    /// already running when the page was opened. Dropping it would make the list quietly incomplete.
    /// </remarks>
    [Fact]
    public void An_operation_nobody_tracked_still_appears()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);

        var id = workspace.StartExtract("elsewhere.7z", "/out", null);

        Assert.True(operations.HasAny);
        Assert.Contains(operations.All, o => o.Id == id);
    }

    /// <summary>OPS-015: dismissing only puts a finished operation away, never a running one.</summary>
    [Fact]
    public void Only_a_finished_operation_can_be_dismissed()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        operations.Track(id, OperationType.Extract, "a.7z");

        operations.DismissActive();
        Assert.True(operations.HasActive, "a running operation must not be dismissable");

        workspace.TryCancel(id);
        operations.DismissActive();
        Assert.True(
            operations.HasActive,
            "an operation that is still cancelling is not finished, and must not be dismissable either");

        workspace.FinishCancel(id);
        operations.DismissActive();
        Assert.False(operations.HasActive);
        Assert.True(operations.HasAny, "dismissing the strip does not erase the record");
    }

    /// <summary>OPS-016: the archive name is isolated before it is shown beside other text.</summary>
    [Fact]
    public void The_target_name_is_isolated()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("/tmp/تقرير.rar", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "تقرير.rar");

        var shown = OperationFormatter.TargetOf(operation);

        Assert.DoesNotContain(shown, char.IsControl);
        Assert.Contains("تقرير.rar", shown);
        Assert.NotEqual("تقرير.rar", shown);
    }

    // ---------- completion detail, clearing, failure, multiplicity ----------

    /// <summary>OPS-017: a completed operation reports files, bytes and how long it took.</summary>
    [Fact]
    public async Task A_completed_operation_reports_what_it_produced()
    {
        var (shell, workspace) = await Opened();
        workspace.ExtractResult = new Application.Handlers.ExtractOutcome
        {
            Format = Core.Formats.ArchiveFormat.SevenZip,
            EntriesWritten = 306,
            BytesWritten = Core.Primitives.ByteSize.FromBytes(826_277_888),
        };

        await shell.Browse.ExtractAsync("/out", selectionOnly: false, cancellationToken: TestContext.Token);

        var operation = shell.Operations.Active!;
        Assert.True(operation.HasResult);
        Assert.Equal(306, operation.Result!.ItemsWritten);
        Assert.NotNull(operation.Duration);

        var parts = OperationFormatter.ResultOf(operation, English);
        Assert.Contains(parts, static p => p.Text == "306");
        Assert.Contains(parts, static p => p.Text.Contains("MB", StringComparison.Ordinal));
        Assert.Contains(parts, static p => p.Text.Contains(':'));
    }

    /// <summary>
    /// OPS-018: a cancelled operation reports no production figures.
    /// </summary>
    /// <remarks>
    /// Whatever a cancelled extraction managed to write before it stopped is not an achievement, and
    /// showing it beside a checkmark-shaped layout is how a half-written folder gets mistaken for a
    /// finished one.
    /// </remarks>
    [Fact]
    public void A_cancelled_operation_reports_no_figures()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        operations.Cancel(id);
        workspace.FinishCancel(id);

        Assert.Equal(OperationState.Cancelled, operation.State);
        Assert.Empty(OperationFormatter.ResultOf(operation, English));
    }

    /// <summary>OPS-019: a failed operation says so, and carries the engine's reason.</summary>
    [Fact]
    public void A_failure_is_visible_and_explained()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        var operation = operations.Track(id, OperationType.Extract, "a.7z");

        workspace.Fail(id);

        Assert.Equal(OperationState.Failed, operation.State);
        Assert.True(operation.IsFinished);
        Assert.Equal("the disk went away", operation.ErrorDetail);
        Assert.Equal(English.OperationFailed, OperationFormatter.StateLabel(OperationState.Failed, English));
    }

    /// <summary>OPS-020: a failure does not take the rest of the centre with it.</summary>
    [Fact]
    public void A_failure_does_not_disturb_the_other_operations()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);

        var good = workspace.StartExtract("good.7z", "/out", null);
        operations.Track(good, OperationType.Extract, "good.7z");
        var bad = workspace.StartExtract("bad.7z", "/out", null);
        operations.Track(bad, OperationType.Extract, "bad.7z");

        workspace.Fail(bad);

        Assert.Equal(2, operations.All.Count);
        Assert.Equal(OperationState.Running, operations.All.Single(o => o.Id == good).State);
        Assert.Equal(OperationState.Failed, operations.All.Single(o => o.Id == bad).State);
    }

    /// <summary>OPS-021: clearing removes finished operations from the queue as well as the list.</summary>
    [Fact]
    public void Clearing_finished_reaches_the_queue()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);

        var done = workspace.StartExtract("done.7z", "/out", null);
        operations.Track(done, OperationType.Extract, "done.7z");
        var running = workspace.StartExtract("running.7z", "/out", null);
        operations.Track(running, OperationType.Extract, "running.7z");

        workspace.TryCancel(done);
        Assert.False(operations.HasFinished, "still cancelling is not finished");

        workspace.FinishCancel(done);
        Assert.True(operations.HasFinished);

        var removed = operations.ClearFinished();

        Assert.Equal(1, removed);
        Assert.False(operations.HasFinished);
        Assert.Single(operations.All);
        Assert.Equal(running, operations.All[0].Id);
        Assert.DoesNotContain(workspace.Operations(), s => s.Id == done);
    }

    /// <summary>OPS-022: clearing never removes a running operation, however much is asked of it.</summary>
    [Fact]
    public void Clearing_never_removes_running_work()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("a.7z", "/out", null);
        operations.Track(id, OperationType.Extract, "a.7z");

        Assert.Equal(0, operations.ClearFinished());
        Assert.Single(operations.All);
        Assert.Equal(OperationState.Running, operations.All[0].State);
    }

    /// <summary>OPS-023: two operations are both tracked, in order, and act independently.</summary>
    [Fact]
    public void Two_operations_are_tracked_independently()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);

        var first = workspace.StartExtract("first.7z", "/out", null);
        var a = operations.Track(first, OperationType.Extract, "first.7z");
        var second = workspace.StartExtract("second.7z", "/out", null);
        var b = operations.Track(second, OperationType.Extract, "second.7z");

        operations.Pause(first);

        Assert.Equal(OperationState.Paused, a.State);
        Assert.Equal(OperationState.Running, b.State);
        Assert.Equal(["second.7z", "first.7z"], operations.All.Select(static o => o.Description).ToArray());
    }

    /// <summary>OPS-024: every state has a glyph, and no two adjacent states share one.</summary>
    [Fact]
    public void Every_state_has_its_own_glyph()
    {
        var glyphs = Enum.GetValues<OperationState>()
            .Select(OperationFormatter.GlyphFor)
            .ToList();

        Assert.All(glyphs, g => Assert.False(string.IsNullOrEmpty(g)));
        Assert.Equal(glyphs.Count, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>OPS-025: the completed line keeps digits out of Arabic runs, like every other line.</summary>
    [Fact]
    public async Task The_completed_line_never_mixes_a_digit_with_arabic()
    {
        var (shell, workspace) = await Opened();
        workspace.ExtractResult = new Application.Handlers.ExtractOutcome
        {
            Format = Core.Formats.ArchiveFormat.SevenZip,
            EntriesWritten = 2_970,
            BytesWritten = Core.Primitives.ByteSize.FromBytes(4_166_000_000),
        };

        await shell.Browse.ExtractAsync("/out", selectionOnly: false, cancellationToken: TestContext.Token);

        foreach (var part in OperationFormatter.ResultOf(shell.Operations.Active!, Arabic))
        {
            var digit = part.Text.Any(char.IsDigit);
            var arabic = part.Text.Any(static c => c is >= '\u0600' and <= '\u06FF');
            Assert.False(digit && arabic, $"mixed fragment: '{part.Text}'");
        }
    }

    /// <summary>
    /// OPS-026: the operation survives the screen that started it.
    /// </summary>
    /// <remarks>
    /// The scenario the whole feature turns on: start on Browse, walk to Operations, walk back. The
    /// operation lives on the shell, so none of that touches it - and this is the test that fails the
    /// day someone moves it onto a page.
    /// </remarks>
    [Fact]
    public async Task An_operation_survives_navigating_between_screens()
    {
        var (shell, workspace) = await Opened();
        var id = workspace.StartExtract("big.7z", "/out", null);
        var operation = shell.Operations.Track(id, OperationType.Extract, "big.7z");

        shell.Section = ShellSection.Operations;
        shell.Section = ShellSection.Browse;
        shell.Section = ShellSection.Operations;

        Assert.Same(operation, shell.Operations.Active);
        Assert.Equal(OperationState.Running, operation.State);
        Assert.True(shell.Operations.Cancel(id), "the operation must still be cancellable after navigating");
    }

    /// <summary>OPS-027: a language switch does not disturb any operation.</summary>
    [Fact]
    public async Task Switching_language_does_not_disturb_an_operation()
    {
        var (shell, workspace) = await Opened();
        var id = workspace.StartExtract("big.7z", "/out", null);
        var operation = shell.Operations.Track(id, OperationType.Extract, "big.7z");

        shell.ToggleLanguage();

        Assert.Same(operation, shell.Operations.Active);
        Assert.Equal(OperationState.Running, operation.State);
    }
}
