using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The create workflow and the capability contract it reads from: <c>UI-040…053</c>.
/// </summary>
/// <remarks>
/// <para>
/// The screen's whole job is to be honest about three things: what this build can write, what is still
/// missing before the button means anything, and how the last attempt ended. None of that needs an
/// archive, which is why these run against <see cref="FakeWorkspace"/> — and the fact that they can is
/// the evidence that no archive logic leaked into the view model.
/// </para>
/// <para>
/// The endings are tested separately from each other on purpose. "Did not complete" collapses a
/// cancellation and a failure into one state, and a product that reports a user's own decision as an
/// error sends them looking for a fault that never happened.
/// </para>
/// </remarks>
public sealed class CreateWorkflowTests
{
    /// <summary>A model with everything filled in except what the test is about.</summary>
    private static CreateViewModel Ready(FakeWorkspace workspace, OperationsViewModel? operations = null)
    {
        var model = new CreateViewModel(workspace, operations);
        model.Options.Format = ArchiveFormat.SevenZip;
        model.AddInputs(["C:\\work\\report.docx"]);
        model.DestinationFolder = "C:\\out";
        model.ArchiveName = "backup";
        return model;
    }

    /// <summary>UI-040: each missing piece is named, one at a time, in the order it is needed.</summary>
    /// <remarks>
    /// A disabled button with no explanation teaches a user that the product is arbitrary. Every state
    /// here has a sentence attached to it, and the last one is the only state in which the button works.
    /// </remarks>
    [Fact]
    public void Every_missing_piece_is_named()
    {
        var model = new CreateViewModel(new FakeWorkspace());

        Assert.Equal(CreateBlocker.NoInputs, model.Blocker);
        Assert.False(model.CanCreate);

        model.AddInputs(["C:\\work\\report.docx"]);
        Assert.Equal(CreateBlocker.NoDestination, model.Blocker);

        model.DestinationFolder = "C:\\out";
        Assert.Equal(CreateBlocker.NoName, model.Blocker);

        model.ArchiveName = "backup";
        Assert.Equal(CreateBlocker.None, model.Blocker);
        Assert.True(model.CanCreate);
    }

    /// <summary>UI-041: a build that can write nothing says so instead of offering an empty list.</summary>
    [Fact]
    public void A_build_that_can_create_nothing_says_so_first()
    {
        var workspace = new FakeWorkspace(new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.Rar] = ArchiveCapabilities.ReadOnlySet,
        });

        var model = new CreateViewModel(workspace);
        model.AddInputs(["C:\\work\\report.docx"]);
        model.DestinationFolder = "C:\\out";
        model.ArchiveName = "backup";

        // Not "no name", not "no inputs": the reason nothing can happen is that this build has no writer
        // at all, and that is the sentence the screen has to show.
        Assert.Equal(CreateBlocker.NoCreatableFormat, model.Blocker);
        Assert.Null(model.TargetPath);
    }

    /// <summary>
    /// UI-042: the extension comes from the chosen format, whatever was typed.
    /// </summary>
    /// <remarks>
    /// Naming an archive "backup.zip" and then choosing 7Z must not produce a 7z file called
    /// <c>backup.zip</c>: every other tool on the machine reads the extension first, and the file would
    /// be mis-detected everywhere outside this product.
    /// </remarks>
    [Fact]
    public void The_extension_follows_the_format_not_the_typing()
    {
        var model = Ready(new FakeWorkspace());
        model.ArchiveName = "backup.zip";

        Assert.Equal(Path.Combine("C:\\out", "backup" + ArchiveFormat.SevenZip.PrimaryExtension), model.TargetPath);

        model.Options.Format = ArchiveFormat.Zip;
        Assert.Equal(Path.Combine("C:\\out", "backup" + ArchiveFormat.Zip.PrimaryExtension), model.TargetPath);
    }

    /// <summary>UI-043: the same path added twice is added once.</summary>
    /// <remarks>
    /// The engine would happily walk the same folder twice and produce an archive holding every entry in
    /// it two times over — valid, twice the size, and wrong.
    /// </remarks>
    [Fact]
    public void A_path_added_twice_is_only_added_once()
    {
        var model = new CreateViewModel(new FakeWorkspace());

        Assert.Equal(2, model.AddInputs(["C:\\a.txt", "C:\\b.txt"]));
        Assert.Equal(0, model.AddInputs(["C:\\a.txt"]));
        Assert.Equal(0, model.AddInputs(["c:\\A.TXT"]));
        Assert.Equal(2, model.Inputs.Count);

        Assert.True(model.RemoveInput("C:\\a.txt"));
        Assert.False(model.RemoveInput("C:\\a.txt"));

        model.ClearInputs();
        Assert.False(model.HasInputs);
    }

    /// <summary>UI-044: a successful creation reaches the engine with exactly what was on screen.</summary>
    [Fact]
    public async Task What_reaches_the_engine_is_what_was_on_screen()
    {
        var workspace = new FakeWorkspace();
        var model = Ready(workspace);
        model.AddInputs(["C:\\work\\photos"]);
        model.Options.Profile = CompressionProfile.MaximumCompression;

        var outcome = await model.CreateAsync(TestContext.Token);

        Assert.NotNull(outcome);
        Assert.Equal(CreateStatus.Completed, model.Status);
        Assert.False(model.IsBusy);

        Assert.Equal(model.TargetPath, workspace.LastCreateTarget);
        Assert.Equal("C:\\work\\report.docx | C:\\work\\photos", string.Join(" | ", workspace.LastCreateSources!));
        Assert.Equal(ArchiveFormat.SevenZip, workspace.LastCreateOptions!.Format);
        Assert.Equal(CompressionProfile.MaximumCompression, workspace.LastCreateOptions.Profile);
        Assert.False(workspace.LastCreateOverwrite);
    }

    /// <summary>
    /// UI-045: an existing archive is reported as a collision, not as a mysterious failure.
    /// </summary>
    /// <remarks>
    /// And nothing is retried behind the user's back. The consent is a separate, explicit act; a screen
    /// that "helpfully" set <c>Overwrite</c> and went again would destroy a file nobody agreed to lose.
    /// </remarks>
    [Fact]
    public async Task An_existing_archive_is_reported_and_not_replaced()
    {
        var workspace = new FakeWorkspace { TargetAlreadyExists = true };
        var model = Ready(workspace);

        Assert.Null(await model.CreateAsync(TestContext.Token));

        Assert.Equal(CreateStatus.Failed, model.Status);
        Assert.True(model.TargetExists);
        Assert.False(model.Overwrite);

        // Now the user consents, and only now.
        model.Overwrite = true;
        var outcome = await model.CreateAsync(TestContext.Token);

        Assert.NotNull(outcome);
        Assert.True(workspace.LastCreateOverwrite);
        Assert.Equal(CreateStatus.Completed, model.Status);

        // And the consent is spent: the next archive has to ask again.
        Assert.False(model.Overwrite);
    }

    /// <summary>UI-046: consent does not survive a change of target.</summary>
    [Fact]
    public void Consent_to_overwrite_does_not_follow_the_next_archive()
    {
        var model = Ready(new FakeWorkspace());
        model.Overwrite = true;

        model.ArchiveName = "something-else";

        Assert.False(model.Overwrite);
    }

    /// <summary>
    /// UI-047: a cancelled creation is reported as cancelled, never as done and never as broken.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is the one the product rules call out by name: a cancellation that ends
    /// up on screen as a completion. It is also why the status is read back from the queue rather than
    /// assumed from a null result — null only means "did not complete".
    /// </remarks>
    [Fact]
    public async Task A_cancelled_creation_is_not_a_completion()
    {
        var workspace = new FakeWorkspace { CancelNextOperation = true };
        var model = Ready(workspace);

        Assert.Null(await model.CreateAsync(TestContext.Token));

        Assert.Equal(CreateStatus.Cancelled, model.Status);
        Assert.NotEqual(CreateStatus.Completed, model.Status);
        Assert.NotEqual(CreateStatus.Failed, model.Status);
        Assert.Null(model.Result);
        Assert.False(model.IsBusy);
    }

    /// <summary>
    /// UI-048: a failure carries the engine's own sentence, not a generic one.
    /// </summary>
    /// <remarks>
    /// The queue records the error and hands back nothing, which is the same shape a cancellation has.
    /// Distinguishing them is the whole subject of <c>FailureFrom</c>, and this is its other half:
    /// UI-047 proves a cancellation is not called a failure, and this proves a failure is not called a
    /// cancellation — and that the reason survives to the screen instead of being replaced by "failed".
    /// </remarks>
    [Fact]
    public async Task A_failed_creation_carries_the_engines_own_reason()
    {
        var workspace = new FakeWorkspace { FailNextOperation = true };
        var model = Ready(workspace);

        Assert.Null(await model.CreateAsync(TestContext.Token));

        Assert.Equal(CreateStatus.Failed, model.Status);
        Assert.Null(model.Result);
        Assert.False(model.TargetExists);

        // The queue's own words, not a sentence this layer invented.
        Assert.NotNull(model.FailureDetail);
        Assert.Contains("the disk went away", model.FailureDetail!);
    }

    /// <summary>UI-049: the creation appears in the shared operation history, typed as a creation.</summary>
    /// <remarks>
    /// Not a second history kept by the create screen: the same list the operations centre renders, so
    /// navigating away and back shows the same operation with the same id.
    /// </remarks>
    [Fact]
    public async Task A_creation_lands_in_the_shared_operation_history()
    {
        var workspace = new FakeWorkspace();
        var operations = new OperationsViewModel(workspace);
        var model = Ready(workspace, operations);

        await model.CreateAsync(TestContext.Token);

        var row = Assert.Single(operations.All, static o => o.Type == OperationType.Create);
        Assert.Equal(workspace.CreatesStarted[0], row.Id);
        Assert.Equal(Path.GetFileName(workspace.LastCreateTarget!), row.Description);
    }

    /// <summary>
    /// UI-050: the capability report answers every product question from one capability set.
    /// </summary>
    /// <remarks>
    /// This is the contract the UI is supposed to read instead of naming formats. If it drifts from the
    /// flags, every screen that trusts it drifts with it.
    /// </remarks>
    [Fact]
    public void The_capability_report_projects_the_flags_faithfully()
    {
        var workspace = new FakeWorkspace();

        var sevenZip = workspace.ReportFor(ArchiveFormat.SevenZip);
        Assert.True(sevenZip.CanOpen);
        Assert.True(sevenZip.CanBrowse);
        Assert.True(sevenZip.CanTest);
        Assert.True(sevenZip.CanExtract);
        Assert.True(sevenZip.CanCreate);
        Assert.True(sevenZip.CanAdd);
        Assert.True(sevenZip.CanPasswordProtect);
        Assert.True(sevenZip.CanEncryptEntryNames);
        Assert.True(sevenZip.CanCompressSolid);
        Assert.True(sevenZip.CanConfigureCompression);
        Assert.False(sevenZip.IsReadOnly);

        // Not implemented, and therefore not claimed: removing and renaming inside an existing 7z.
        Assert.False(sevenZip.CanRemove);
        Assert.False(sevenZip.CanRename);
        Assert.False(sevenZip.CanUpdate);
        Assert.False(sevenZip.CanSplit);

        var zip = workspace.ReportFor(ArchiveFormat.Zip);
        Assert.True(zip.CanCreate);
        Assert.True(zip.CanConfigureCompression);
        Assert.False(zip.CanPasswordProtect);
        Assert.False(zip.CanCompressSolid);
        Assert.False(zip.IsReadOnly);
    }

    /// <summary>
    /// UI-051: a read-only format reports itself read-only rather than being special-cased by name.
    /// </summary>
    /// <remarks>
    /// RAR is the case in hand and is permanently read-only (ADR-0004), but nothing here mentions that
    /// decision: the report is derived from the flags, so a format that becomes writable one day needs
    /// no change on any screen.
    /// </remarks>
    [Fact]
    public void A_read_only_format_reports_itself_read_only()
    {
        var report = new FakeWorkspace().ReportFor(ArchiveFormat.Rar);

        Assert.True(report.IsReadOnly);
        Assert.True(report.CanOpen);
        Assert.True(report.CanBrowse);
        Assert.True(report.CanExtract);
        Assert.True(report.CanTest);

        Assert.False(report.CanCreate);
        Assert.False(report.CanAdd);
        Assert.False(report.CanUpdate);
        Assert.False(report.CanConfigureCompression);
    }

    /// <summary>UI-052: a format this build does not have at all reports nothing rather than throwing.</summary>
    [Fact]
    public void An_absent_format_reports_no_capabilities()
    {
        var workspace = new FakeWorkspace(new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
        });

        var report = workspace.ReportFor(ArchiveFormat.SevenZip);

        Assert.False(report.CanOpen);
        Assert.False(report.CanCreate);
        Assert.False(report.CanExtract);

        // And "read only" is not the answer for something that cannot be opened either.
        Assert.False(report.IsReadOnly);
    }

    /// <summary>
    /// UI-053: the open filter is derived from what this build can read, not written into the screen.
    /// </summary>
    /// <remarks>
    /// The WinUI page had <c>.zip</c>, <c>.7z</c> and <c>.rar</c> typed into it, which meant a build
    /// with no native library still offered to open a RAR and then failed on the file the user picked.
    /// The list is asked for now, and a build that can read one format offers one format.
    /// </remarks>
    [Fact]
    public void The_open_filter_is_what_this_build_can_actually_read()
    {
        var full = new BrowseViewModel(new FakeWorkspace()).OpenableExtensions;

        Assert.Contains(".zip", string.Join(" ", full));
        Assert.Contains(".7z", string.Join(" ", full));
        Assert.Contains(".rar", string.Join(" ", full));

        var zipOnly = new BrowseViewModel(new FakeWorkspace(new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet,
        })).OpenableExtensions;

        Assert.Equal(".zip .zipx", string.Join(" ", zipOnly));
    }

    /// <summary>UI-054: nothing happens when the button is pressed in a state that blocks it.</summary>
    [Fact]
    public async Task Creating_while_blocked_starts_nothing()
    {
        var workspace = new FakeWorkspace();
        var model = new CreateViewModel(workspace);

        Assert.Null(await model.CreateAsync(TestContext.Token));

        Assert.Empty(workspace.CreatesStarted);
        Assert.Equal(CreateStatus.Idle, model.Status);
    }
}
