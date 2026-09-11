using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The extraction workflow above the seam: <c>UI-060…069</c>.
/// </summary>
/// <remarks>
/// What the screen decides is where the files go, what happens to files already there, and how the
/// operation is described while it runs. What actually happens to a colliding file is the extraction
/// writer's business and is proven against a real disk in <c>EXT-006…010</c>; these tests prove the
/// decision reaches it intact, and that nothing about it is invented up here.
/// </remarks>
public sealed class ExtractWorkflowTests
{
    private static readonly LocalizedStrings English = LocalizedStrings.For(UiLanguage.English);
    private static readonly LocalizedStrings Arabic = LocalizedStrings.For(UiLanguage.Arabic);

    private static BrowseViewModel Opened(FakeWorkspace workspace)
    {
        var model = new BrowseViewModel(workspace);
        workspace.Listing = new ListOutcome(
            ArchiveFormat.SevenZip,
            new ArchiveStatistics(),
            [FakeWorkspace.File("readme.txt")]);

        model.OpenAsync("C:\\work\\noba-ops.7z", TestContext.Token).AsTask().GetAwaiter().GetResult();
        return model;
    }

    /// <summary>
    /// UI-060: the policy the person chose is the policy the engine is given.
    /// </summary>
    /// <remarks>
    /// The defect this rules out is the quiet one: a screen that collects a choice, forgets to pass it,
    /// and lets the engine's default stand. Every extraction would then behave identically however the
    /// dialog was answered, and the only symptom would be somebody's file being replaced.
    /// </remarks>
    [Theory]
    [InlineData(ConflictPolicy.Ask)]
    [InlineData(ConflictPolicy.Skip)]
    [InlineData(ConflictPolicy.Rename)]
    [InlineData(ConflictPolicy.Overwrite)]
    [InlineData(ConflictPolicy.Fail)]
    public async Task The_chosen_conflict_policy_reaches_the_engine(ConflictPolicy policy)
    {
        var workspace = new FakeWorkspace();
        var model = Opened(workspace);

        await model.ExtractAsync("C:\\out", selectionOnly: false, policy, TestContext.Token);

        Assert.Equal(policy, workspace.LastConflictPolicy);
        Assert.Equal("C:\\out", workspace.LastDestination);
    }

    /// <summary>
    /// UI-061: the safe policy is the one a person gets by not choosing.
    /// </summary>
    /// <remarks>
    /// Ask is the default and sits first; Overwrite is deliberately not first. A destructive option
    /// under the cursor when a dialog opens is a decision the product made and presented as the user's.
    /// </remarks>
    [Fact]
    public void Asking_is_the_default_and_overwrite_is_not_first()
    {
        Assert.Equal(ConflictPolicy.Ask, ExtractPlanner.Default);
        Assert.Equal(ConflictPolicy.Ask, ExtractPlanner.Policies[0]);
        Assert.NotEqual(ConflictPolicy.Overwrite, ExtractPlanner.Policies[0]);
        Assert.Equal(0, ExtractPlanner.IndexOf(ExtractPlanner.Default));

        // And the model's own default, for a caller that passes nothing.
        Assert.Equal(ConflictPolicy.Ask, ExtractPlanner.Default);
    }

    /// <summary>UI-062: every offered policy has a name, in both languages, and they are all different.</summary>
    [Fact]
    public void Every_policy_is_named_in_both_languages()
    {
        foreach (var text in new[] { English, Arabic })
        {
            var names = ExtractPlanner.Policies.Select(p => ExtractPlanner.NameOf(p, text)).ToList();

            Assert.Equal(ExtractPlanner.Policies.Count, names.Count);
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(names, static n => string.IsNullOrWhiteSpace(n));

            // A key that is missing renders as the key itself rather than as blank; that must not pass.
            Assert.DoesNotContain(names, static n => n.StartsWith("extract.", StringComparison.Ordinal));
        }
    }

    /// <summary>UI-063: the prompt carries the whole choice, with the technical values isolated.</summary>
    /// <remarks>
    /// The archive name and the destination are paths, and a path dropped unisolated into an Arabic
    /// dialog reorders on screen into something that is not the path. Both are wrapped before they leave
    /// this layer, so the dialog cannot forget to.
    /// </remarks>
    [Fact]
    public void The_prompt_carries_the_choice_and_isolates_the_paths()
    {
        var prompt = ExtractPlanner.Build(
            Arabic,
            archiveDisplayName: "تقرير.7z",
            selectionOnly: true,
            destination: "C:\\Users\\m\\Desktop\\out",
            policy: ConflictPolicy.Rename,
            isRightToLeft: true,
            fontFamily: "Noto Sans Arabic");

        Assert.Equal(Arabic.ExtractTitle, prompt.Title);
        Assert.Equal(Arabic.ExtractScopeSelection, prompt.Scope);
        Assert.True(prompt.IsRightToLeft);

        Assert.Contains("تقرير.7z", prompt.Archive);
        Assert.NotEqual("تقرير.7z", prompt.Archive);

        Assert.NotNull(prompt.Destination);
        Assert.Contains("C:\\Users\\m\\Desktop\\out", prompt.Destination!);
        Assert.NotEqual("C:\\Users\\m\\Desktop\\out", prompt.Destination);

        Assert.Equal(ExtractPlanner.Policies.Count, prompt.ConflictChoices.Count);
        Assert.Equal(ConflictPolicy.Rename, prompt.ConflictChoices[prompt.SelectedConflictIndex].Policy);
    }

    /// <summary>UI-064: with no folder chosen yet, the prompt says so rather than inventing one.</summary>
    [Fact]
    public void A_prompt_with_no_destination_says_so()
    {
        var prompt = ExtractPlanner.Build(
            English, "a.7z", selectionOnly: false, destination: null,
            policy: ExtractPlanner.Default, isRightToLeft: false, fontFamily: "Segoe UI");

        Assert.Null(prompt.Destination);
        Assert.Equal(English.ExtractNoDestination, prompt.NoDestinationLabel);
        Assert.Equal(English.ExtractScopeAll, prompt.Scope);
    }

    /// <summary>UI-065: an extraction is described as extracting, and carries its destination.</summary>
    [Fact]
    public void The_row_says_extracting_and_shows_where()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("noba-ops.7z", "C:\\out", null, ConflictPolicy.Ask);
        var row = operations.Track(id, OperationType.Extract, "noba-ops.7z");

        Assert.Equal(English.OperationExtracting, OperationFormatter.VerbOf(row, English));
        Assert.NotEqual(English.OperationCreating, OperationFormatter.VerbOf(row, English));
        Assert.NotEqual(English.OperationTesting, OperationFormatter.VerbOf(row, English));

        // The destination the engine was actually given, carried on the snapshot rather than
        // remembered by the screen that started it.
        workspace.SetDestination(id, "C:\\out");
        Assert.Equal("C:\\out", row.Destination);

        // Shown isolated, not raw: it is a Windows path that may be rendered inside an Arabic strip,
        // and an un-isolated one reorders into something that is not the path.
        var shown = OperationFormatter.DestinationOf(row);
        Assert.Contains("C:\\out", shown);
        Assert.NotEqual("C:\\out", shown);
    }

    /// <summary>
    /// UI-066: "Cancelling" and "Cancelled" are different words, in both languages.
    /// </summary>
    /// <remarks>
    /// If they were the same string the state would be invisible, and the product would be back to
    /// claiming a cancellation had finished while the writer was still inside a file.
    /// </remarks>
    [Fact]
    public void Cancelling_and_cancelled_do_not_read_the_same()
    {
        foreach (var text in new[] { English, Arabic })
        {
            var cancelling = OperationFormatter.StateLabel(OperationState.Cancelling, text);
            var cancelled = OperationFormatter.StateLabel(OperationState.Cancelled, text);

            Assert.False(string.IsNullOrWhiteSpace(cancelling));
            Assert.NotEqual(cancelled, cancelling);
            Assert.Equal(text.OperationCancelling, cancelling);
        }

        // And a glyph of its own, so the state does not rest on the word alone.
        Assert.NotEqual(
            OperationFormatter.GlyphFor(OperationState.Cancelled),
            OperationFormatter.GlyphFor(OperationState.Cancelling));
    }

    /// <summary>
    /// UI-067: while an extraction is cancelling, the screen offers nothing that would be refused.
    /// </summary>
    /// <remarks>
    /// This is the honest-controls rule applied to the new state: Pause cannot suspend a cancellation,
    /// Cancel cannot make it stop sooner, and Dismiss cannot clear a row whose writer is still running.
    /// </remarks>
    [Fact]
    public void A_cancelling_extraction_offers_no_control_that_would_be_refused()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var id = workspace.StartExtract("noba-ops.7z", "C:\\out", null, ConflictPolicy.Ask);
        var row = operations.Track(id, OperationType.Extract, "noba-ops.7z");

        Assert.True(operations.Cancel(id));

        Assert.Equal(OperationState.Cancelling, row.State);
        Assert.True(row.IsCancelling);
        Assert.True(row.IsRunning, "still working, so the strip must stay visible");
        Assert.False(row.IsFinished);
        Assert.False(row.CanCancel);
        Assert.False(row.CanPause);
        Assert.False(row.CanResume);
        Assert.Equal(0, operations.ClearFinished());

        workspace.FinishCancel(id);

        Assert.Equal(OperationState.Cancelled, row.State);
        Assert.False(row.IsCancelling);
        Assert.True(row.IsFinished);
    }

    /// <summary>
    /// UI-068: an extraction that was cancelled reports no result and no status figure.
    /// </summary>
    /// <remarks>
    /// The rule the whole drop turns on. A cancelled extraction has written some files, and reporting a
    /// count next to a green tick is how a person concludes the archive is safe to delete.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_extraction_reports_nothing_that_looks_like_success()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var model = Opened(workspace);

        // The queue ends this one cancelled, exactly as pressing Cancel mid-extraction does.
        workspace.CancelNextOperation = true;

        var outcome = await model.ExtractAsync(
            "C:\\out", selectionOnly: false, ConflictPolicy.Ask, TestContext.Token);

        var row = operations.Track(workspace.Started[^1], OperationType.Extract, "noba-ops.7z");

        Assert.Null(outcome);
        Assert.Equal(OperationState.Cancelled, row.State);
        Assert.Empty(OperationFormatter.ResultOf(row, English));

        // And no figure in the status line either: a count beside a finished extraction is what a
        // person reads as "it worked".
        Assert.True(
            string.IsNullOrEmpty(model.Status),
            $"a cancelled extraction reported '{model.Status}'");
    }

    /// <summary>UI-069: extracting a selection tells the engine which entries, not just "some".</summary>
    [Fact]
    public async Task A_selection_reaches_the_engine_as_entry_paths()
    {
        var workspace = new FakeWorkspace
        {
            Listing = new ListOutcome(
                ArchiveFormat.SevenZip,
                new ArchiveStatistics(),
                [FakeWorkspace.File("keep.txt"), FakeWorkspace.File("skip.txt")]),
        };

        var model = new BrowseViewModel(workspace);
        await model.OpenAsync("C:\\work\\a.7z", TestContext.Token);

        var wanted = model.Rows.First(r => r.Node.Entry?.Path?.Value == "keep.txt");
        model.Select([wanted.Node]);

        await model.ExtractAsync("C:\\out", selectionOnly: true, ConflictPolicy.Skip, TestContext.Token);

        Assert.NotNull(workspace.LastSelection);
        Assert.Single(workspace.LastSelection!);
        Assert.Equal("keep.txt", workspace.LastSelection![0]);
        Assert.Equal(ConflictPolicy.Skip, workspace.LastConflictPolicy);
    }
}
