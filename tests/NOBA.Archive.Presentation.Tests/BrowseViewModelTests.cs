using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>The browse screen: <c>UI-001…012</c>.</summary>
public sealed class BrowseViewModelTests
{
    private static ListOutcome Listing(params ArchiveEntry[] entries) =>
        new(
            ArchiveFormat.SevenZip,
            new ArchiveStatistics
            {
                EntryCount = entries.Length,
                FileCount = entries.Count(static e => !e.IsDirectory),
                DirectoryCount = entries.Count(static e => e.IsDirectory),
                RejectedEntryCount = entries.Count(static e => e.IsRejected),
            },
            entries);

    /// <summary>UI-001: a flat entry list becomes a folder tree.</summary>
    /// <remarks>
    /// Archives store paths, not trees. Folders are implied by separators, and the implication has to
    /// be made here because no format guarantees a directory entry exists for every folder.
    /// </remarks>
    [Fact]
    public async Task A_flat_listing_becomes_a_tree()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("docs/reports/q1.txt"),
                FakeWorkspace.File("docs/readme.md"),
                FakeWorkspace.File("root.txt")),
        };

        var vm = new BrowseViewModel(workspace);
        Assert.True(await vm.OpenAsync("a.7z"));

        // Folders first, then names: docs/, then root.txt.
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.Rows[0].IsFolder);
        Assert.Equal("docs", vm.Rows[0].Node.Name);
        Assert.Equal("root.txt", vm.Rows[1].Node.Name);

        vm.Enter(vm.Rows[0].Node);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("reports", vm.Rows[0].Node.Name);
        Assert.Equal("readme.md", vm.Rows[1].Node.Name);

        vm.GoUp();
        Assert.Equal(2, vm.Rows.Count);
    }

    /// <summary>UI-002: folder sizes and counts roll up from their contents.</summary>
    [Fact]
    public async Task Folder_totals_roll_up()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("docs/a.txt", 100),
                FakeWorkspace.File("docs/deep/b.txt", 250)),
        };

        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.7z");

        Assert.Single(vm.Rows);
        var docs = vm.Rows[0].Node;
        Assert.Equal(350, docs.TotalSize.Bytes);
        Assert.Equal(2, docs.FileCount);
    }

    /// <summary>
    /// UI-003: a refused entry is shown, marked, and carries its reason.
    /// </summary>
    /// <remarks>
    /// Hiding it would leave the user unable to see that an archive attempted a traversal, which is
    /// most of the value of having refused it. The row is visible, flagged, and — per the design
    /// system — never marked by colour alone: the reason string is what the label renders.
    /// </remarks>
    [Fact]
    public async Task A_refused_entry_is_visible_and_explained()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("safe.txt"),
                FakeWorkspace.Rejected(@"..\..\evil.txt")),
        };

        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("hostile.7z");

        var rejected = vm.Rows.SelectMany(static r => Flatten(r.Node)).FirstOrDefault(static n => n.Entry?.IsRejected == true);
        Assert.NotNull(rejected);

        var row = new BrowseRow(rejected!, 0);
        Assert.True(row.IsRejected);
        Assert.False(string.IsNullOrWhiteSpace(row.RejectionDetail));
    }

    /// <summary>UI-004: a refused entry cannot be put into an extraction request.</summary>
    /// <remarks>
    /// The writer would refuse it again on its own account — this is not the control, it is the screen
    /// declining to ask. A selection that carried a raw hostile path down to the engine would be a UI
    /// that had started making policy decisions.
    /// </remarks>
    [Fact]
    public async Task A_refused_entry_is_never_sent_for_extraction()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("safe.txt"),
                FakeWorkspace.Rejected(@"..\..\evil.txt")),
        };

        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("hostile.7z");

        vm.Select(vm.Rows.Select(static r => r.Node));
        await vm.ExtractAsync("/tmp/out", selectionOnly: true);

        Assert.NotNull(workspace.LastSelection);
        Assert.True(
            !workspace.LastSelection!.Any(static p => p.Contains("..", StringComparison.Ordinal)),
            string.Join(", ", workspace.LastSelection!));
    }

    /// <summary>UI-005: search filters the listing already in memory, across the whole archive.</summary>
    [Fact]
    public async Task Search_finds_entries_in_any_folder()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("docs/reports/quarterly.txt"),
                FakeWorkspace.File("docs/readme.md"),
                FakeWorkspace.File("images/logo.png")),
        };

        var vm = new BrowseViewModel(workspace) { Culture = UiLanguage.English.Culture };
        await vm.OpenAsync("a.7z");

        vm.Search = "quarter";
        Assert.Single(vm.Rows);
        Assert.Equal("quarterly.txt", vm.Rows[0].Node.Name);

        vm.Search = string.Empty;
        Assert.Equal(2, vm.Rows.Count);
    }

    /// <summary>UI-006: search matches Arabic names under the Arabic culture.</summary>
    [Fact]
    public async Task Search_matches_arabic_names()
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(
                FakeWorkspace.File("docs/تقرير سنوي.txt"),
                FakeWorkspace.File("docs/readme.md")),
        };

        var vm = new BrowseViewModel(workspace) { Culture = UiLanguage.Arabic.Culture };
        await vm.OpenAsync("a.7z");

        vm.Search = "تقرير";
        Assert.Single(vm.Rows);
        Assert.Equal("تقرير سنوي.txt", vm.Rows[0].Node.Name);
    }

    /// <summary>UI-007: the engine's own diagnosis is what the user is shown.</summary>
    /// <remarks>
    /// "This file is raw NTFS file-table data, not an archive" is worth more than "could not open
    /// archive", and the screen is in no position to improve on a message it did not produce.
    /// </remarks>
    [Fact]
    public async Task A_failure_shows_the_engines_own_message()
    {
        var workspace = new FakeWorkspace
        {
            ListFailure = new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedFormat,
                "This file is raw NTFS file-table data, not an archive.")),
        };

        var vm = new BrowseViewModel(workspace);

        Assert.False(await vm.OpenAsync("recovered.rar"));
        Assert.Equal("This file is raw NTFS file-table data, not an archive.", vm.Status);
        Assert.False(vm.HasArchive);
    }

    /// <summary>UI-008: a guessed entry-name encoding is surfaced, never presented as fact.</summary>
    [Fact]
    public async Task A_guessed_name_encoding_raises_a_warning()
    {
        var workspace = new FakeWorkspace
        {
            Listing = new ListOutcome(
                ArchiveFormat.Zip,
                new ArchiveStatistics(),
                [FakeWorkspace.File("a.txt")],
                new EntryNameEncodingInfo("System default (1252)", 1252, IsGuess: true)),
        };

        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("legacy.zip");

        Assert.True(vm.ShowEncodingWarning);

        workspace.Listing = new ListOutcome(
            ArchiveFormat.Zip,
            new ArchiveStatistics(),
            [FakeWorkspace.File("a.txt")],
            new EntryNameEncodingInfo("UTF-8", 65001, IsGuess: false));

        await vm.OpenAsync("modern.zip");
        Assert.False(vm.ShowEncodingWarning);
    }

    /// <summary>UI-009: extract-selection is unavailable until something is selected.</summary>
    [Fact]
    public async Task Extract_selection_requires_a_selection()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("a.txt")) };
        var vm = new BrowseViewModel(workspace);

        Assert.False(vm.CanExtractAll);
        Assert.False(vm.CanExtractSelection);

        await vm.OpenAsync("a.7z");
        Assert.True(vm.CanExtractAll);
        Assert.False(vm.CanExtractSelection);

        vm.Select([vm.Rows[0].Node]);
        Assert.True(vm.CanExtractSelection);
    }

    /// <summary>UI-010: extracting everything sends no selection at all.</summary>
    [Fact]
    public async Task Extracting_everything_sends_no_selection()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("a.txt")) };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.7z");

        await vm.ExtractAsync("/tmp/out", selectionOnly: false);

        Assert.Null(workspace.LastSelection);
        Assert.Equal("/tmp/out", workspace.LastDestination);
    }

    /// <summary>UI-011: names and paths reach the view already isolated.</summary>
    [Fact]
    public async Task Display_strings_are_direction_isolated()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("مستندات/تقرير.txt")) };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("عربي.7z");

        var node = vm.Rows[0].Node;
        Assert.Equal(BidiText.FirstStrongIsolate, node.DisplayName[0]);
        Assert.Equal(BidiText.LeftToRightIsolate, node.DisplayPath[0]);
        Assert.Equal(BidiText.FirstStrongIsolate, vm.ArchiveDisplayName[0]);
    }

    /// <summary>UI-012: closing an archive leaves nothing of the last one behind.</summary>
    [Fact]
    public async Task Reset_clears_everything()
    {
        var workspace = new FakeWorkspace { Listing = Listing(FakeWorkspace.File("a.txt")) };
        var vm = new BrowseViewModel(workspace);
        await vm.OpenAsync("a.7z");

        vm.Reset();

        Assert.False(vm.HasArchive);
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Breadcrumbs);
        Assert.Null(vm.ArchivePath);
        Assert.Equal(string.Empty, vm.SummaryLine);
    }

    private static IEnumerable<ArchiveNode> Flatten(ArchiveNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
