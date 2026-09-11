using System.Collections.Specialized;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Which notification means what: <c>REN-001…004</c>.
/// </summary>
/// <remarks>
/// <para>
/// The window stopped responding with ten entries in the archive. Rebuilding the list called
/// <c>FileList.Items.Clear()</c>; the list view answered with <c>SelectionChanged</c>;
/// <c>OnSelectionChanged</c> called <see cref="BrowseViewModel.Select"/>; and <c>Select</c> raises
/// <c>CanExtractSelection</c> whether or not the selection changed. That notification rebuilt the list,
/// which cleared it again. The loop never depended on how many entries there were, which is why entry
/// count turned out to be a red herring.
/// </para>
/// <para>
/// The page cannot be built here, so these tests do not exercise the fix. They pin the two facts the fix
/// is built on: that a selection notification carries nothing a list needs, and that the list's content
/// is announced by <c>Rows</c> and by nothing else. Break either and the page's switch is wrong — which
/// is exactly what these should catch, since nothing else can.
/// </para>
/// </remarks>
public sealed class RenderSignalTests
{
    /// <summary>The properties the browse page rebuilds its file list for.</summary>
    private static readonly string[] ListTriggers =
    [
        nameof(BrowseViewModel.HasArchive),
        nameof(BrowseViewModel.ShowEncodingWarning),
        nameof(BrowseViewModel.ArchiveDisplayName),
        nameof(BrowseViewModel.SummaryLine),
    ];

    /// <summary>
    /// REN-001: selecting raises nothing the file list is rebuilt for.
    /// </summary>
    /// <remarks>
    /// The assertion that keeps the cycle severed. If <c>Select</c> ever also raised
    /// <c>HasArchive</c> — or anything else the page rebuilds for — the loop would come back, and it
    /// would come back as a frozen window rather than as a failing test.
    /// </remarks>
    [Fact]
    public async Task Selecting_raises_only_the_command_property()
    {
        var model = await OpenedModel();
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "?");

        model.Select(model.Rows.Select(static r => r.Node));

        // Stated as the invariant rather than as an exact list: what must never happen is a selection
        // notification that rebuilds the list, because that is the cycle that froze the window. Which
        // status properties a selection also updates is free to grow.
        Assert.NotEmpty(raised);
        Assert.All(raised, name => Assert.False(Array.Exists(ListTriggers, t => t == name), name));
    }

    /// <summary>
    /// REN-002: selecting nothing, twice, still notifies both times.
    /// </summary>
    /// <remarks>
    /// This is the property that made the loop unbounded rather than self-limiting: the notification is
    /// unconditional, so a page that rebuilds on it can never settle. The view model is not being
    /// changed to suppress it — an unconditional raise is a defensible choice for a collection whose
    /// contents it does not diff — so the page must not listen to it for content.
    /// </remarks>
    [Fact]
    public async Task Selecting_notifies_even_when_the_selection_did_not_change()
    {
        var model = await OpenedModel();
        var raised = new List<string>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "?");

        model.Select([]);
        model.Select([]);

        Assert.Equal(2, raised.Count(name => name == nameof(BrowseViewModel.CanExtractSelection)));
    }

    /// <summary>
    /// REN-003: navigating into a folder announces its contents on the collection, not as a property.
    /// </summary>
    /// <remarks>
    /// Why the page subscribes to <c>Rows.CollectionChanged</c> rather than filtering property names for
    /// content: no property describes the rows, so listening only to
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/> would leave folder navigation
    /// redrawing by coincidence, or not at all. The one property that does change is
    /// <c>CanGoUp</c> — whether there is now somewhere to go back to.
    /// </remarks>
    [Fact]
    public async Task Entering_a_folder_changes_the_rows_and_announces_only_the_way_back()
    {
        var model = await OpenedModel();
        var folder = model.Rows.First(static r => r.IsFolder).Node;

        var properties = new List<string>();
        var collectionChanges = 0;
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName ?? "?");
        ((INotifyCollectionChanged)model.Rows).CollectionChanged += (_, _) => collectionChanges++;

        model.Enter(folder);

        Assert.Equal([nameof(BrowseViewModel.CanGoUp)], properties);
        Assert.True(collectionChanges > 0, "Rows did not announce the new folder's contents");
        Assert.True(model.CanGoUp, "one folder deep there must be a way back");
    }

    /// <summary>
    /// REN-004: one open produces one burst, and it is a burst.
    /// </summary>
    /// <remarks>
    /// Recorded rather than asserted to a fixed number: the point is that a page rebuilding once per
    /// notification would rebuild many times for a single open, which is what the coalescing in
    /// <c>QueueListRender</c> exists to collapse.
    /// </remarks>
    [Fact]
    public async Task One_open_produces_many_notifications()
    {
        var model = new BrowseViewModel(new FakeWorkspace
        {
            Listing = new(
                NOBA.Archive.Core.Formats.ArchiveFormat.Rar,
                new NOBA.Archive.Core.Formats.ArchiveStatistics(),
                [FakeWorkspace.Directory("photos"), FakeWorkspace.File("photos/one.jpg")]),
        });

        var properties = 0;
        var collectionChanges = 0;
        model.PropertyChanged += (_, _) => properties++;
        ((INotifyCollectionChanged)model.Rows).CollectionChanged += (_, _) => collectionChanges++;

        await model.OpenAsync("a.rar");

        Assert.True(properties > 1, $"expected a burst of property notifications, saw {properties}");
        Assert.True(collectionChanges > 0, "the rows never announced themselves");
    }

    private static async Task<BrowseViewModel> OpenedModel()
    {
        var model = new BrowseViewModel(new FakeWorkspace
        {
            Listing = new(
                NOBA.Archive.Core.Formats.ArchiveFormat.Rar,
                new NOBA.Archive.Core.Formats.ArchiveStatistics(),
                [
                    FakeWorkspace.Directory("photos"),
                    FakeWorkspace.File("photos/one.jpg"),
                    FakeWorkspace.File("readme.txt"),
                ]),
        });

        await model.OpenAsync("a.rar");
        return model;
    }
}
