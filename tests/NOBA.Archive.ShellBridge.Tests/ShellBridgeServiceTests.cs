using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.ShellBridge;
using Xunit;

namespace NOBA.Archive.ShellBridge.Tests;

/// <summary>
/// The modern shell surface's decisions, proven on a build machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule the adapter relies on is exercised here, with no COM, no Windows and no Explorer.</b>
/// That ordering is deliberate. Phase 5 has already spent four Windows runs and two VM runs on
/// defects a local control would have caught in seconds, and a shell extension is the worst possible
/// place to discover one: it runs inside a right-click, in a surrogate process, on somebody else's
/// machine.
/// </para>
/// <para>
/// The fixture is a build that can write 7Z, ZIP, TAR and GZIP and cannot write RAR - the real
/// capability shape, so the interesting cases are the real ones.
/// </para>
/// </remarks>
public sealed class ShellBridgeServiceTests
{
    private static readonly Dictionary<ArchiveFormat, ArchiveCapabilities> Build = new()
    {
        [ArchiveFormat.SevenZip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.Tar] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.GZip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
        [ArchiveFormat.Rar] = ArchiveCapabilities.ReadOnlySet,
    };

    private static FormatCapabilityReport ReportFor(ArchiveFormat format) =>
        new(format, Build.TryGetValue(format, out var c) ? c : ArchiveCapabilities.None);

    private static ShellBridgeCatalog Catalog() => new(
        ReportFor,
        CreatableFormats.From(Build.Keys, ReportFor),
        ShellRegistration.InterimTextFor,
        ShellRegistration.InterimMenuTitle);

    private static ShellSelection Select(params (string Path, bool IsDirectory)[] items) =>
        new([.. items.Select(i => ShellItem.For(i.Path, i.IsDirectory))]);

    private static ShellMenuEntryResult Entry(ShellMenuResult menu, string id) =>
        menu.Entries.Single(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    // ---------------------------------------------------------------------------------------------
    // The third state. This is what Phase 5 is for.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// SHELL-L9. On a folder, GZIP is a single-member format asked to hold many members. The classic
    /// surface had to omit it; this one shows it greyed with the model's own reason.
    /// </summary>
    [Fact]
    public void A_single_member_format_on_a_folder_is_disabled_not_hidden()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\stuff", true)), Catalog());

        var gzip = Entry(menu, "noba.compress.gzip");

        Assert.Equal("disabled", gzip.State);
        Assert.Equal(nameof(ShellBlocker.SingleMemberFormat), gzip.Blocker);
    }

    /// <summary>The formats that can hold many members stay enabled on the same selection.</summary>
    [Fact]
    public void A_multi_member_format_on_a_folder_is_enabled()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\stuff", true)), Catalog());

        Assert.Equal("enabled", Entry(menu, "noba.compress.zip").State);
        Assert.Equal("enabled", Entry(menu, "noba.compress.7z").State);
    }

    /// <summary>An entry the model never offered is hidden, and says so rather than being absent.</summary>
    [Fact]
    public void Archive_only_entries_are_hidden_on_a_plain_file()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\notes.txt", false)), Catalog());

        Assert.Equal("hidden", Entry(menu, "noba.open").State);
        Assert.Equal("hidden", Entry(menu, "noba.test").State);
        Assert.Equal("hidden", Entry(menu, "noba.extracthere").State);
        Assert.Equal("hidden", Entry(menu, "noba.extractto").State);
    }

    /// <summary>A hidden entry carries no blocker: there is nothing to explain about an absence.</summary>
    [Fact]
    public void A_hidden_entry_has_no_blocker()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\notes.txt", false)), Catalog());

        Assert.Null(Entry(menu, "noba.open").Blocker);
    }

    /// <summary>An enabled entry carries no blocker either.</summary>
    [Fact]
    public void An_enabled_entry_has_no_blocker()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\bundle.7z", false)), Catalog());

        Assert.Null(Entry(menu, "noba.open").Blocker);
    }

    // ---------------------------------------------------------------------------------------------
    // The root
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_archive_selection_offers_the_archive_actions()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\bundle.7z", false)), Catalog());

        Assert.Equal("enabled", menu.RootState);
        Assert.Equal("enabled", Entry(menu, "noba.open").State);
        Assert.Equal("enabled", Entry(menu, "noba.test").State);
        Assert.Equal("enabled", Entry(menu, "noba.extracthere").State);
        Assert.Equal("enabled", Entry(menu, "noba.extractto").State);
    }

    /// <summary>An empty submenu must not appear at all (plan section D).</summary>
    [Fact]
    public void The_root_is_hidden_when_every_child_is_hidden()
    {
        var menu = ShellBridgeService.Menu(Select(), Catalog());

        Assert.Equal("hidden", menu.RootState);
        Assert.All(menu.Entries, e => Assert.Equal("hidden", e.State));
    }

    /// <summary>
    /// The root is visible when a child is merely greyed. A submenu that exists only to show one
    /// disabled line is still the correct answer - hiding it would take the third state away again.
    /// </summary>
    [Fact]
    public void The_root_is_visible_when_the_only_visible_child_is_disabled()
    {
        // A build that can write nothing but GZIP, asked about a folder: the one compress line is
        // disabled, "Create archive..." is offered, and the root must appear.
        var gzipOnly = new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.GZip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
        };
        FormatCapabilityReport Report(ArchiveFormat f) =>
            new(f, gzipOnly.TryGetValue(f, out var c) ? c : ArchiveCapabilities.None);

        var catalog = new ShellBridgeCatalog(
            Report,
            CreatableFormats.From(gzipOnly.Keys, Report),
            ShellRegistration.InterimTextFor,
            ShellRegistration.InterimMenuTitle);

        var menu = ShellBridgeService.Menu(Select((@"C:\d\stuff", true)), catalog);

        Assert.Equal("disabled", Entry(menu, "noba.compress.gzip").State);
        Assert.Equal("enabled", menu.RootState);
    }

    // ---------------------------------------------------------------------------------------------
    // Ids and labels come from ShellRegistration, not from here
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// SHELL-065. The ids the bridge publishes are exactly the ones the registration publishes - the
    /// same list, in the same order. Composing them here would be the capability derivation defect in
    /// a second costume.
    /// </summary>
    [Fact]
    public void Every_id_and_label_comes_from_the_registration()
    {
        var catalog = Catalog();
        var registered = ShellRegistration.Entries(catalog.ReportFor, catalog.CreatableFormats, catalog.TextFor);

        var menu = ShellBridgeService.Menu(Select((@"C:\d\bundle.7z", false)), catalog);

        Assert.Equal(
            registered.Select(r => r.Id).ToArray(),
            menu.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(
            registered.Select(r => r.Label).ToArray(),
            menu.Entries.Select(e => e.Title).ToArray());
    }

    /// <summary>A format that cannot be written yields no entry at all, not a hidden one.</summary>
    [Fact]
    public void A_format_that_cannot_be_written_is_not_in_the_menu_at_all()
    {
        var menu = ShellBridgeService.Menu(Select((@"C:\d\bundle.7z", false)), Catalog());

        Assert.DoesNotContain(menu.Entries, e => e.Id.Contains("rar", StringComparison.Ordinal));
    }

    /// <summary>
    /// Compress lines must not cross. Matching a menu entry to a registered entry on action alone
    /// would let ZIP's state land on 7Z - defect D1 in a new place.
    /// </summary>
    [Fact]
    public void Compress_entries_do_not_cross_formats()
    {
        // GZIP is the only one disabled on a folder; if the join were action-only, every compress
        // line would share one state.
        var menu = ShellBridgeService.Menu(Select((@"C:\d\stuff", true)), Catalog());

        var states = menu.Entries
            .Where(e => e.Id.StartsWith("noba.compress.", StringComparison.Ordinal))
            .ToDictionary(e => e.Id, e => e.State, StringComparer.Ordinal);

        Assert.Equal("disabled", states["noba.compress.gzip"]);
        Assert.Equal("enabled", states["noba.compress.zip"]);
        Assert.Equal("enabled", states["noba.compress.7z"]);
        Assert.Equal("enabled", states["noba.compress.tar"]);
    }

    /// <summary>
    /// The asymmetry that hid every archive action on the first run of this suite.
    /// </summary>
    /// <remarks>
    /// A registered archive action carries <c>Format: null</c> - <i>Open with NOBA Archive</i> is one
    /// line whatever the archive is - while the menu entry reports the format it resolved to. Matching
    /// format on both sides made all four archive lines hidden on a plain <c>.7z</c>. This pins the
    /// rule so a later refactor cannot quietly restore the symmetry.
    /// </remarks>
    [Fact]
    public void A_registration_that_names_no_format_still_matches_a_menu_entry_that_does()
    {
        var catalog = Catalog();
        var registered = ShellRegistration.Entries(catalog.ReportFor, catalog.CreatableFormats, catalog.TextFor);

        // The premise: the two sides really do disagree about format for this entry.
        var open = registered.Single(r => string.Equals(r.Id, "noba.open", StringComparison.Ordinal));
        Assert.Null(open.Format);

        var built = ShellMenuBuilder.Build(
            Select((@"C:\d\bundle.7z", false)), catalog.ReportFor, catalog.CreatableFormats);
        var builtOpen = built.Single(m => m.Action == ArchiveAction.OpenInApp);
        Assert.NotNull(builtOpen.Format);

        // ... and the join still finds it.
        var menu = ShellBridgeService.Menu(Select((@"C:\d\bundle.7z", false)), catalog);
        Assert.Equal("enabled", Entry(menu, "noba.open").State);
    }

    // ---------------------------------------------------------------------------------------------
    // Invoke - one invocation, one request, one archive
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// D4 and SHELL-057, the whole reason for Phase 5. Three files and one click produce ONE process
    /// carrying all three, not three processes each making an archive of one.
    /// </summary>
    [Fact]
    public void A_multi_selection_compresses_into_one_archive_in_one_process()
    {
        var plan = ShellBridgeService.Invoke(
            "noba.compress.zip",
            Select((@"C:\d\a.txt", false), (@"C:\d\b.txt", false), (@"C:\d\c.txt", false)),
            Catalog());

        Assert.NotNull(plan);
        Assert.Equal(ShellRegistration.CommandLineExecutable, plan!.Executable);
        Assert.Contains(plan.Arguments, a => string.Equals(a, @"C:\d\a.txt", StringComparison.Ordinal));
        Assert.Contains(plan.Arguments, a => string.Equals(a, @"C:\d\b.txt", StringComparison.Ordinal));
        Assert.Contains(plan.Arguments, a => string.Equals(a, @"C:\d\c.txt", StringComparison.Ordinal));
    }

    /// <summary>An argument array, never a command string - so quoting is not a question.</summary>
    [Fact]
    public void The_plan_is_an_argument_array_with_no_shell_metacharacters_needed()
    {
        var plan = ShellBridgeService.Invoke(
            "noba.compress.zip",
            Select((@"C:\d\a file; rm -rf.txt", false)),
            Catalog());

        Assert.NotNull(plan);
        Assert.Contains(plan!.Arguments, a => string.Equals(a, @"C:\d\a file; rm -rf.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Arguments, a => a.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Plan section E.3: re-asked, not trusted. The selection can change between the draw and the
    /// click, so a disabled entry must refuse at invoke time however it looked when drawn.
    /// </summary>
    [Fact]
    public void Invoking_a_disabled_entry_produces_no_plan()
    {
        var plan = ShellBridgeService.Invoke("noba.compress.gzip", Select((@"C:\d\stuff", true)), Catalog());

        Assert.Null(plan);
    }

    /// <summary>And a hidden one refuses too.</summary>
    [Fact]
    public void Invoking_a_hidden_entry_produces_no_plan()
    {
        var plan = ShellBridgeService.Invoke("noba.open", Select((@"C:\d\notes.txt", false)), Catalog());

        Assert.Null(plan);
    }

    /// <summary>An id nobody registered is not an error to guess at.</summary>
    [Fact]
    public void Invoking_an_unknown_entry_produces_no_plan()
    {
        var plan = ShellBridgeService.Invoke("noba.launch.calculator", Select((@"C:\d\bundle.7z", false)), Catalog());

        Assert.Null(plan);
    }

    /// <summary>An empty selection cannot be acted on, whatever entry is named.</summary>
    [Fact]
    public void Invoking_with_an_empty_selection_produces_no_plan()
    {
        Assert.Null(ShellBridgeService.Invoke("noba.compress.zip", Select(), Catalog()));
    }

    /// <summary>Extract here fixes its destination to the archive's own folder, not the CWD.</summary>
    [Fact]
    public void Extract_here_names_the_archives_own_folder()
    {
        var plan = ShellBridgeService.Invoke("noba.extracthere", Select((@"C:\d\bundle.7z", false)), Catalog());

        Assert.NotNull(plan);
        Assert.Contains(plan!.Arguments, a => string.Equals(a, @"C:\d", StringComparison.Ordinal));
    }
}
