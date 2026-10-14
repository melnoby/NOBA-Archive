using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Services;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Managing an open archive from the browse screen — adding, removing, and renaming entries:
/// <c>UI-013…020</c>.
/// </summary>
/// <remarks>
/// DROP 03's WinUI half. The handler underneath (<see cref="ManageArchiveHandler"/>) is covered end to
/// end by <c>MAN-001…010</c> in the integration suite; what belongs here is the screen's half of the
/// contract — which buttons the capability report and the selection actually unlock, what gets sent to
/// the workspace, and that a successful rewrite leaves the screen showing the archive it just produced
/// rather than the one it had in memory before the rewrite started.
/// </remarks>
public sealed class ManageWorkflowTests
{
    private static ListOutcome Listing(params ArchiveEntry[] entries) =>
        new(ArchiveFormat.Zip, new ArchiveStatistics { EntryCount = entries.Length }, entries);

    /// <summary>UI-013: all three actions are gated on the same capability the handler actually needs.</summary>
    /// <remarks>
    /// RAR in this build's fake registry declares <c>ReadOnlySet | SolidCompression</c> — no
    /// <c>Create</c>, no <c>AddEntries</c> — so it fails <c>CanUpdate</c> exactly as it does for the real
    /// provider registry (ADR-0004: read-only, permanently).
    /// </remarks>
    [Fact]
    public async Task Manage_actions_are_unavailable_for_a_read_only_format()
    {
        var workspace = new FakeWorkspace
        {
            Listing = new ListOutcome(ArchiveFormat.Rar, new ArchiveStatistics(), [FakeWorkspace.File("a.txt")]),
        };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.rar");

        Assert.False(vm.CanAddEntries);

        vm.Select([vm.Rows[0].Node]);
        Assert.False(vm.CanRemoveSelected);
        Assert.False(vm.CanRenameSelected);
    }

    /// <summary>UI-014: on a format that supports it, adding needs no selection at all.</summary>
    [Fact]
    public async Task Add_is_available_as_soon_as_an_updatable_archive_is_open()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("a.txt")) };
        var vm = new BrowseViewModel(workspace);

        Assert.False(vm.CanAddEntries);

        await vm.OpenAsync("a.zip");
        Assert.True(vm.CanAddEntries);
    }

    /// <summary>UI-015: removing needs a selection, exactly like extracting a selection does.</summary>
    [Fact]
    public async Task Remove_requires_a_selection()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("a.txt")) };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.zip");

        Assert.False(vm.CanRemoveSelected);

        vm.Select([vm.Rows[0].Node]);
        Assert.True(vm.CanRemoveSelected);
    }

    /// <summary>UI-016: a refused entry cannot be put into a removal request either.</summary>
    [Fact]
    public async Task A_refused_entry_cannot_be_removed()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(FakeWorkspace.Rejected(@"..\..\evil.txt")),
        };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("hostile.zip");

        vm.Select(vm.Rows.Select(static r => r.Node));
        Assert.False(vm.CanRemoveSelected);

        var outcome = await vm.RemoveSelectedAsync();
        Assert.Null(outcome);
        Assert.Empty(workspace.ManagesStarted);
    }

    /// <summary>UI-017: rename is only offered for exactly one real file, never a folder or many files.</summary>
    [Fact]
    public async Task Rename_requires_exactly_one_file()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("docs/a.txt"),
                FakeWorkspace.File("docs/b.txt")),
        };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.zip");

        var folder = vm.Rows[0].Node; // "docs", implied by the two files beneath it
        vm.Select([folder]);
        Assert.False(vm.CanRenameSelected);

        vm.Enter(folder);
        vm.Select(vm.Rows.Select(static r => r.Node)); // both files
        Assert.False(vm.CanRenameSelected);

        vm.Select([vm.Rows[0].Node]);
        Assert.True(vm.CanRenameSelected);
    }

    /// <summary>UI-018: removing sends every real path under the selection and reopens on success.</summary>
    [Fact]
    public async Task Remove_sends_every_file_under_the_selection_and_reopens()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("docs/a.txt"),
                FakeWorkspace.File("docs/b.txt"),
                FakeWorkspace.File("root.txt")),
        };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.zip");

        var docs = vm.Rows[0].Node;
        vm.Select([docs]);

        // After the rewrite the engine would report a smaller archive. The fake stands in for that.
        workspace.Listing = Listing(FakeWorkspace.File("root.txt"));
        workspace.ManageResult = new ManageOutcome { ArchivePath = "a.zip", EntriesKept = 1, EntriesRemoved = 2 };

        var outcome = await vm.RemoveSelectedAsync();

        Assert.NotNull(outcome);
        Assert.Equal(2, outcome!.EntriesRemoved);
        Assert.Equal(["docs/a.txt", "docs/b.txt"], workspace.LastManageEntriesToRemove);
        Assert.Empty(workspace.LastManageEntriesToRename!);
        Assert.Empty(workspace.LastManageSourcesToAdd!);

        // Reopened: the screen now shows what the rewrite actually produced, not the pre-rewrite tree.
        Assert.Single(vm.Rows);
        Assert.Equal("root.txt", vm.Rows[0].Node.Name);
        Assert.Empty(vm.Selection);
    }

    /// <summary>UI-019: a rename keeps the entry's folder and only changes its own path segment.</summary>
    [Fact]
    public async Task Rename_keeps_the_entrys_folder()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("docs/a.txt")) };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.zip");
        vm.Enter(vm.Rows[0].Node);

        var file = vm.Rows[0].Node;
        workspace.ManageResult = new ManageOutcome { ArchivePath = "a.zip", EntriesRenamed = 1, EntriesKept = 0 };

        await vm.RenameAsync(file, "renamed.txt");

        var renames = workspace.LastManageEntriesToRename!;
        Assert.Equal(1, renames.Count);
        Assert.Equal("docs/a.txt", renames[0].From);
        Assert.Equal("docs/renamed.txt", renames[0].To);
    }

    /// <summary>UI-020: a cancelled or failed rewrite reports nothing and does not reopen the archive.</summary>
    [Fact]
    public async Task A_failed_rewrite_leaves_the_screen_on_the_archive_it_had()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(FakeWorkspace.File("a.txt")),
            FailNextOperation = true,
        };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.zip");
        vm.Select([vm.Rows[0].Node]);

        var outcome = await vm.RemoveSelectedAsync();

        Assert.Null(outcome);
        // Still the archive as it was before the attempted rewrite - nothing was reopened.
        Assert.Single(vm.Rows);
        Assert.Equal("a.txt", vm.Rows[0].Node.Name);
        Assert.False(vm.IsBusy);
    }
}
