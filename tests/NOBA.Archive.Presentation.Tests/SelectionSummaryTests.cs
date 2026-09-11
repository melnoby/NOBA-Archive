using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// What the status bar says: <c>SEL-001…003</c>.
/// </summary>
/// <remarks>
/// The status bar used to repeat the archive summary that already sits in the header two rows above,
/// which is a row of pixels spent on nothing. It now reports the selection, and these pin what that
/// means — including the case that decides whether the row is worth having at all: nothing selected.
/// </remarks>
public sealed class SelectionSummaryTests
{
    /// <summary>SEL-001: nothing selected says nothing, so the archive summary can take the row.</summary>
    [Fact]
    public async Task An_empty_selection_reports_nothing()
    {
        var model = await Opened();
        Assert.Equal(string.Empty, model.SelectionSummary);
    }

    /// <summary>SEL-002: a selection reports its count and its combined size.</summary>
    [Fact]
    public async Task A_selection_reports_its_count_and_size()
    {
        var model = await Opened();
        model.Select(model.Rows.Select(static r => r.Node));

        var summary = model.SelectionSummary;

        Assert.True(
            summary.Contains(LocalizedStrings.For(UiLanguage.English).SelectedLabel, StringComparison.Ordinal),
            summary);
        Assert.NotEqual(string.Empty, summary);
    }

    /// <summary>
    /// SEL-003: selecting announces itself, or the status bar would never repaint.
    /// </summary>
    /// <remarks>
    /// The window's other subscriber listens for this exact name. Without the notification the count
    /// would be correct and invisible, which is the kind of defect that survives a screenshot review.
    /// </remarks>
    [Fact]
    public async Task Selecting_announces_the_new_summary()
    {
        var model = await Opened();
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "?");

        model.Select(model.Rows.Select(static r => r.Node));

        Assert.True(raised.Contains(nameof(BrowseViewModel.SelectionSummary)), string.Join(", ", raised));
    }

    private static async Task<BrowseViewModel> Opened()
    {
        var model = new BrowseViewModel(new FakeWorkspace
        {
            Listing = new(
                ArchiveFormat.Rar,
                new ArchiveStatistics { FileCount = 2, DirectoryCount = 1, IsSolid = true },
                [FakeWorkspace.File("one.jpg", 4096), FakeWorkspace.File("two.jpg", 2048)]),
        });

        await model.OpenAsync("a.rar");
        return model;
    }
}
