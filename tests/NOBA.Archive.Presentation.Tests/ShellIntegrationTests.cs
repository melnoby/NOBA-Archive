using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// DROP 08 — what the Explorer menu offers, and where a chosen line goes: <c>SHELL-001…024</c>.
/// </summary>
/// <remarks>
/// <para>
/// The menu is the whole feature under test. Whether Windows draws it is Windows' business and is
/// checked by hand; what is checked here is that every line on it came from a capability report, that
/// no line promises what the engine would refuse, and that choosing one produces a request the
/// existing workspace seam already knows how to carry out.
/// </para>
/// <para>
/// Nothing here touches a disk, which is not a testing convenience — it is the property under test.
/// Menu construction runs inside a held right-click, so a selection is described by the caller and
/// never inspected.
/// </para>
/// </remarks>
public sealed class ShellIntegrationTests
{
    /// <summary>The build DROP 05 actually produced: 7Z, ZIP, TAR and GZIP writable, RAR read-only.</summary>
    private static readonly Dictionary<ArchiveFormat, ArchiveCapabilities> RealBuild = new()
    {
        [ArchiveFormat.SevenZip] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.Tar] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,

        // Create without AddEntries. This is what the GZIP provider really advertises.
        [ArchiveFormat.GZip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,

        // Read-only, permanently, per ADR-0004.
        [ArchiveFormat.Rar] = ArchiveCapabilities.ReadOnlySet,
    };

    private static FormatCapabilityReport Report(ArchiveFormat format) =>
        new(format, RealBuild.TryGetValue(format, out var caps) ? caps : ArchiveCapabilities.None);

    private static IReadOnlyList<ArchiveFormat> Creatable =>
        [ArchiveFormat.SevenZip, ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.GZip];

    private static ShellSelection Files(params string[] paths) =>
        ShellSelection.From(paths, static _ => false);

    private static ShellSelection Folders(params string[] paths) =>
        ShellSelection.From(paths, static _ => true);

    private static IReadOnlyList<ShellMenuEntry> Menu(ShellSelection selection) =>
        ShellMenuBuilder.Build(selection, Report, Creatable);

    private static ArchiveActionRequest? Choose(ShellSelection selection, ArchiveAction action, ArchiveFormat? format = null)
    {
        foreach (var entry in Menu(selection))
        {
            if (entry.Action == action && (format is null || entry.Format == format))
            {
                return ShellMenuBuilder.RequestFor(entry, selection);
            }
        }

        return null;
    }

    /// <summary>SHELL-001: one file produces a Create request naming that file.</summary>
    [Fact]
    public void A_single_file_produces_a_valid_create_request()
    {
        var request = Choose(Files(@"C:\Docs\report.pdf"), ArchiveAction.CreateArchive);

        Assert.NotNull(request);
        Assert.Equal(ArchiveAction.CreateArchive, request!.Action);
        Assert.Equal(@"C:\Docs\report.pdf", string.Join(" | ", request.InputPaths));
    }

    /// <summary>SHELL-002: several files produce ONE request carrying all of them.</summary>
    [Fact]
    public void Multiple_files_produce_one_request_with_every_path()
    {
        var request = Choose(
            Files(@"C:\Docs\report.pdf", @"C:\Docs\photo.jpg", @"C:\Docs\data.xlsx"),
            ArchiveAction.CreateArchive);

        Assert.NotNull(request);
        Assert.Equal(3, request!.InputPaths.Count);
        Assert.Equal(
            @"C:\Docs\report.pdf | C:\Docs\photo.jpg | C:\Docs\data.xlsx",
            string.Join(" | ", request.InputPaths));
    }

    /// <summary>SHELL-003: a folder is one input, and is never mistaken for an archive by its name.</summary>
    [Fact]
    public void A_folder_produces_a_valid_create_request()
    {
        var selection = Folders(@"C:\Projects\Backups.zip");

        Assert.False(selection.Items[0].LooksLikeArchive);
        Assert.Null(selection.SingleArchive);

        var request = Choose(selection, ArchiveAction.CreateArchive);
        Assert.NotNull(request);
        Assert.Single(request!.InputPaths);
    }

    /// <summary>SHELL-004: files and folders together, order preserved.</summary>
    [Fact]
    public void Mixed_files_and_folders_reach_the_request_in_order()
    {
        var selection = ShellSelection.From(
            [@"C:\Docs\report.pdf", @"C:\Projects\ProjectA", @"C:\Images\logo.png"],
            static p => !p.Contains('.', StringComparison.Ordinal));

        var request = Choose(selection, ArchiveAction.CreateArchive);

        Assert.NotNull(request);
        Assert.Equal(
            @"C:\Docs\report.pdf | C:\Projects\ProjectA | C:\Images\logo.png",
            string.Join(" | ", request!.InputPaths));
    }

    /// <summary>SHELL-005: an Arabic path reaches the engine byte for byte.</summary>
    [Fact]
    public void An_Arabic_path_survives_the_shell_boundary()
    {
        const string arabic = @"C:\Users\م\مستندات\تقرير سنوي.txt";

        var request = Choose(Files(arabic), ArchiveAction.CreateArchive);
        Assert.Equal(arabic, request!.InputPaths[0]);

        // And through the launch plan, where a naive implementation would quote or escape it.
        var plan = ShellLauncher.PlanFor(request);
        Assert.NotNull(plan);
        Assert.Contains(plan!.Arguments, a => a == arabic);
    }

    /// <summary>SHELL-006: and so does every other script.</summary>
    [Fact]
    public void A_Unicode_path_survives_the_shell_boundary()
    {
        const string japanese = @"C:\データ\日本語のファイル.txt";
        const string greek = @"C:\δεδομένα\Ελληνικά.txt";

        var request = Choose(Files(japanese, greek), ArchiveAction.CreateArchive);

        Assert.Equal(japanese + " | " + greek, string.Join(" | ", request!.InputPaths));
    }

    /// <summary>
    /// SHELL-007: RAR never offers creation.
    /// </summary>
    /// <remarks>
    /// And the reason it does not is the capability, not the extension: RAR is absent from the
    /// creatable list because its report says <c>CanCreate</c> is false, which is the same fact the
    /// Create screen reads.
    /// </remarks>
    [Fact]
    public void RAR_is_never_offered_for_creation()
    {
        // Even when a caller wrongly offers it as a candidate, the builder filters by capability.
        var menu = ShellMenuBuilder.Build(
            Files(@"C:\Docs\report.pdf"),
            Report,
            [ArchiveFormat.SevenZip, ArchiveFormat.Rar]);

        Assert.DoesNotContain(menu, e => e.Format == ArchiveFormat.Rar);
        Assert.False(Report(ArchiveFormat.Rar).CanCreate);
    }

    /// <summary>SHELL-008: a selected RAR offers exactly what it can do.</summary>
    [Fact]
    public void A_selected_RAR_offers_open_test_and_extract()
    {
        var menu = Menu(Files(@"C:\Docs\archive.rar"));
        var actions = menu.Select(static e => e.Action).ToList();

        Assert.Contains(actions, a => a == ArchiveAction.OpenInApp);
        Assert.Contains(actions, a => a == ArchiveAction.TestArchive);
        Assert.Contains(actions, a => a == ArchiveAction.ExtractHere);
        Assert.Contains(actions, a => a == ArchiveAction.ExtractTo);

        Assert.DoesNotContain(menu, e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.Rar);
    }

    /// <summary>SHELL-009: one file may be compressed to GZIP.</summary>
    [Fact]
    public void GZIP_is_offered_for_a_single_file()
    {
        var menu = Menu(Files(@"C:\Docs\report.pdf"));
        var gzip = menu.SingleOrDefault(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.GZip);

        Assert.NotNull(gzip);
        Assert.True(gzip!.IsEnabled);
        Assert.Equal(ShellBlocker.None, gzip.Blocker);
    }

    /// <summary>
    /// SHELL-010: two files may not, and the line says why.
    /// </summary>
    /// <remarks>
    /// Disabled with a reason rather than absent. A GZIP entry that silently vanishes when a second
    /// file is selected reads as a bug; one that greys out and names the restriction teaches the
    /// format in one glance.
    /// </remarks>
    [Fact]
    public void GZIP_is_refused_for_a_multiple_selection()
    {
        var menu = Menu(Files(@"C:\Docs\a.txt", @"C:\Docs\b.txt"));
        var gzip = menu.SingleOrDefault(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.GZip);

        Assert.NotNull(gzip);
        Assert.False(gzip!.IsEnabled);
        Assert.Equal(ShellBlocker.SingleMemberFormat, gzip.Blocker);

        // Disabled means unavailable, not merely grey.
        Assert.Null(ShellMenuBuilder.RequestFor(gzip, Files(@"C:\Docs\a.txt", @"C:\Docs\b.txt")));

        // The multi-member formats are untouched by it.
        Assert.True(menu.Single(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.Tar).IsEnabled);
    }

    /// <summary>SHELL-011: BZIP2 and XZ appear nowhere, because no provider claims them.</summary>
    [Fact]
    public void Unsupported_formats_are_never_offered()
    {
        var menu = ShellMenuBuilder.Build(
            Files(@"C:\Docs\report.pdf"),
            Report,
            [.. Creatable, ArchiveFormat.BZip2, ArchiveFormat.Xz]);

        Assert.DoesNotContain(menu, e => e.Format == ArchiveFormat.BZip2);
        Assert.DoesNotContain(menu, e => e.Format == ArchiveFormat.Xz);

        Assert.False(Report(ArchiveFormat.BZip2).IsAvailable);
        Assert.False(Report(ArchiveFormat.Xz).IsAvailable);
    }

    /// <summary>SHELL-012: an archive gets extract actions; a plain file does not.</summary>
    [Fact]
    public void Only_an_archive_offers_extraction()
    {
        var archive = Menu(Files(@"C:\Docs\bundle.7z")).Select(static e => e.Action).ToList();
        var plain = Menu(Files(@"C:\Docs\report.pdf")).Select(static e => e.Action).ToList();

        Assert.Contains(archive, a => a == ArchiveAction.ExtractHere);
        Assert.True(!plain.Any(a => a == ArchiveAction.ExtractHere), "ArchiveAction.ExtractHere should be absent");
        Assert.True(!plain.Any(a => a == ArchiveAction.OpenInApp), "ArchiveAction.OpenInApp should be absent");

        // Compression still applies to an archive: re-packing a zip inside a 7z is legitimate.
        Assert.Contains(archive, a => a == ArchiveAction.CompressTo);
    }

    /// <summary>SHELL-013: "Create archive…" opens the existing Create screen with the inputs loaded.</summary>
    [Fact]
    public void Create_archive_seeds_the_existing_create_workflow()
    {
        var shell = new ShellViewModel(new FakeWorkspace());
        var selection = ShellSelection.From(
            [@"C:\Projects\ProjectA", @"C:\Docs\report.pdf", @"C:\Images\photo.png"],
            static p => !p.Contains('.', StringComparison.Ordinal));

        var outcome = ShellRequestSeed.Apply(shell, Choose(selection, ArchiveAction.CreateArchive));

        Assert.Equal(SeedOutcome.CreateSeeded, outcome);
        Assert.Equal(ShellSection.Create, shell.Section);
        Assert.Equal(
            @"C:\Projects\ProjectA | C:\Docs\report.pdf | C:\Images\photo.png",
            string.Join(" | ", shell.Create.Inputs));

        // Seeded, not started.
        Assert.Equal(CreateStatus.Idle, shell.Create.Status);
        Assert.False(shell.Create.IsBusy);
    }

    /// <summary>SHELL-014: "Extract here" means the archive's own folder, and goes through the CLI verb.</summary>
    [Fact]
    public void Extract_here_targets_the_archives_own_folder()
    {
        var request = Choose(Files(@"C:\Docs\Reports\bundle.7z"), ArchiveAction.ExtractHere);

        Assert.NotNull(request);
        Assert.Equal(@"C:\Docs\Reports", request!.Destination);

        var plan = ShellLauncher.PlanFor(request);
        Assert.NotNull(plan);
        Assert.Equal(ShellRegistration.CommandLineExecutable, plan!.Executable);
        Assert.Equal("extract", plan.Arguments[0]);
        Assert.Contains(plan.Arguments, a => a == "-o");
        Assert.Contains(plan.Arguments, a => a == @"C:\Docs\Reports");
    }

    /// <summary>
    /// SHELL-015: "Extract to…" carries no destination, because the person supplies it.
    /// </summary>
    /// <remarks>
    /// It routes to the application rather than the CLI for exactly that reason — there is a question
    /// to ask, and the existing extract dialog is where it is asked.
    /// </remarks>
    [Fact]
    public void Extract_to_defers_the_destination_to_the_existing_dialog()
    {
        var request = Choose(Files(@"C:\Docs\bundle.7z"), ArchiveAction.ExtractTo);

        Assert.NotNull(request);
        Assert.Null(request!.Destination);

        var plan = ShellLauncher.PlanFor(request);
        Assert.Equal(ShellRegistration.ApplicationExecutable, plan!.Executable);
        Assert.DoesNotContain(plan.Arguments, a => a == "-o");

        var shell = new ShellViewModel(new FakeWorkspace());
        Assert.Equal(SeedOutcome.ExtractRequested, ShellRequestSeed.Apply(shell, request));
    }

    /// <summary>
    /// SHELL-016: the shell adds no conflict policy of its own.
    /// </summary>
    /// <remarks>
    /// The request has nowhere to put one, which is the design: whatever the existing planner defaults
    /// to is what a shell extraction gets, and it cannot drift because there is no second value.
    /// </remarks>
    [Fact]
    public void The_existing_conflict_policy_remains_the_only_one()
    {
        var request = Choose(Files(@"C:\Docs\bundle.7z"), ArchiveAction.ExtractHere);
        var plan = ShellLauncher.PlanFor(request!);

        Assert.DoesNotContain(plan!.Arguments, a => a == "--conflict");
        Assert.DoesNotContain(plan.Arguments, a => a.Contains("overwrite", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ConflictPolicy.Ask, ExtractPlanner.Default);
    }

    /// <summary>
    /// SHELL-017: a quick compress never asks to overwrite, so it can never do it silently.
    /// </summary>
    /// <remarks>
    /// The suggested name is the obvious one and nothing checks whether it is taken — deliberately.
    /// The engine refuses an occupied target, and that refusal is the product's one overwrite
    /// contract. A menu that quietly wrote "report (2).7z" would be a second policy, invented in the
    /// place a person reaches in one click.
    /// </remarks>
    [Fact]
    public void A_quick_compress_never_carries_permission_to_overwrite()
    {
        var request = Choose(Files(@"C:\Docs\report.pdf"), ArchiveAction.CompressTo, ArchiveFormat.SevenZip);
        var plan = ShellLauncher.PlanFor(request!);

        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.Arguments, a => a.Contains("overwrite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Arguments, a => a == "-f" || a == "--force");

        // The name is derived, not invented: report.pdf becomes report.7z in its own folder.
        Assert.Contains(plan.Arguments, a => a == @"C:\Docs\report.7z");
    }

    /// <summary>SHELL-018: nonsense selections produce no menu and no request, and never throw.</summary>
    [Fact]
    public void An_invalid_selection_produces_nothing_and_does_not_throw()
    {
        Assert.Empty(Menu(new ShellSelection([])));

        var empty = ShellSelection.From([null!, string.Empty, "   "], static _ => false);
        Assert.True(empty.IsEmpty);
        Assert.Empty(Menu(empty));

        Assert.Null(ShellMenuBuilder.RequestFor(
            ShellMenuEntry.Enabled(ArchiveAction.CreateArchive), new ShellSelection([])));
    }

    /// <summary>SHELL-019: a path with nothing behind it still produces a well-formed request.</summary>
    /// <remarks>
    /// Menu construction cannot know the file was deleted a moment ago — checking would mean a disk
    /// query per item. So the request is built, the engine fails to find it, and the existing
    /// not-found error is what the person sees. Guessing earlier would be slower and no more correct.
    /// </remarks>
    [Fact]
    public void A_missing_file_still_produces_a_safe_request()
    {
        var request = Choose(Files(@"C:\Docs\deleted.7z"), ArchiveAction.ExtractHere);

        Assert.NotNull(request);
        Assert.Equal(@"C:\Docs", request!.Destination);

        var plan = ShellLauncher.PlanFor(request);
        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.Arguments, string.IsNullOrWhiteSpace);
    }

    /// <summary>SHELL-020: building the menu starts nothing.</summary>
    [Fact]
    public void Building_the_menu_starts_no_work()
    {
        var workspace = new FakeWorkspace();
        var shell = new ShellViewModel(workspace);

        var menu = Menu(Files(@"C:\Docs\report.pdf", @"C:\Docs\photo.jpg"));
        Assert.NotEmpty(menu);

        // A menu was built and a request formed; still nothing has been queued.
        var request = Choose(Files(@"C:\Docs\report.pdf"), ArchiveAction.CreateArchive);
        Assert.NotNull(request);
        Assert.Empty(workspace.Started);

        // Even seeding the window queues nothing — the person still has to press Create.
        ShellRequestSeed.Apply(shell, request);
        Assert.Empty(workspace.Started);
    }

    /// <summary>
    /// SHELL-021: a folder in the selection is never walked.
    /// </summary>
    /// <remarks>
    /// Proven by construction: the only question asked about an item is the one the caller answers.
    /// The probe counts its calls, and a builder that enumerated would have to ask about paths nobody
    /// selected.
    /// </remarks>
    [Fact]
    public void Menu_construction_never_enumerates_a_folder()
    {
        var asked = new List<string>();
        var selection = ShellSelection.From(
            [@"C:\Projects\Huge", @"C:\Docs\report.pdf"],
            path =>
            {
                asked.Add(path);
                return !path.Contains('.', StringComparison.Ordinal);
            });

        var before = asked.Count;
        var menu = Menu(selection);
        var request = Choose(selection, ArchiveAction.CreateArchive);

        Assert.NotEmpty(menu);
        Assert.NotNull(request);

        // One question per selected item, asked while the selection was described. Not one more.
        Assert.Equal(2, before);
        Assert.Equal(before, asked.Count);
        Assert.Equal(2, request!.InputPaths.Count);
    }

    /// <summary>
    /// SHELL-022: registration metadata lives in one place and is internally consistent.
    /// </summary>
    [Fact]
    public void Registration_metadata_is_centralized()
    {
        Assert.NotEmpty(ShellRegistration.Verbs);

        // Every action the menu can produce has a verb, or it could be offered and not carried out.
        foreach (var action in Enum.GetValues<ArchiveAction>())
        {
            Assert.NotNull(ShellRegistration.VerbFor(action));
        }

        // Ids are unique and stable-looking: they end up in the registry.
        var ids = ShellRegistration.Verbs.Select(static v => v.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("noba.", id));

        // Every key is under the single publisher root.
        Assert.All(
            ShellRegistration.KeysCreatedOnInstall,
            key => Assert.StartsWith(ShellRegistration.RootKey, key));

        // Every label is a localisation key, not an English string baked into a registry value.
        var english = LocalizedStrings.For(UiLanguage.English);
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        foreach (var verb in ShellRegistration.Verbs)
        {
            Assert.NotEqual(verb.TextKey, english[verb.TextKey]);
            Assert.NotEqual(verb.TextKey, arabic[verb.TextKey]);
        }
    }

    /// <summary>
    /// SHELL-023: uninstall removes exactly what install created, deepest first.
    /// </summary>
    /// <remarks>
    /// The asymmetry this prevents is the expensive one. An install key with no matching removal is a
    /// machine left pointing at a deleted executable, and nothing left that knows the string.
    /// </remarks>
    [Fact]
    public void Unregistration_removes_everything_registration_created()
    {
        var created = ShellRegistration.KeysCreatedOnInstall;
        var removed = ShellRegistration.KeysRemovedOnUninstall;

        Assert.Equal(
            string.Join(" | ", created.OrderBy(static k => k, StringComparer.Ordinal)),
            string.Join(" | ", removed.OrderBy(static k => k, StringComparer.Ordinal)));

        // A key is never removed before something beneath it: Windows refuses to delete a key that
        // still has children, and a half-finished uninstall is exactly the orphan this list prevents.
        // Sibling order is free — only ancestry constrains it.
        for (var parent = 0; parent < removed.Count; parent++)
        {
            for (var child = parent + 1; child < removed.Count; child++)
            {
                Assert.False(
                    removed[child].StartsWith(removed[parent] + @"\", StringComparison.Ordinal),
                    $"'{removed[parent]}' is removed before its child '{removed[child]}'");
            }
        }

        Assert.Equal(ShellRegistration.RootKey, removed[^1]);
    }

    /// <summary>
    /// SHELL-024: every action routes to a NOBA executable, and never to a command interpreter.
    /// </summary>
    /// <remarks>
    /// Arguments are a list, never a line. A file legally named <c>a" &amp; del /q *.*.txt</c> is one
    /// argument containing those characters; through a command string it would be a command.
    /// </remarks>
    [Fact]
    public void Every_action_routes_through_a_NOBA_executable_with_structured_arguments()
    {
        const string hostile = @"C:\Docs\a"" & del /q *.*.txt";

        var selections = new (ArchiveAction Action, ShellSelection Selection)[]
        {
            (ArchiveAction.OpenInApp, Files(@"C:\Docs\bundle.7z")),
            (ArchiveAction.TestArchive, Files(@"C:\Docs\bundle.7z")),
            (ArchiveAction.ExtractHere, Files(@"C:\Docs\bundle.7z")),
            (ArchiveAction.ExtractTo, Files(@"C:\Docs\bundle.7z")),
            (ArchiveAction.CompressTo, Files(hostile)),
            (ArchiveAction.CreateArchive, Files(hostile)),
        };

        foreach (var (action, selection) in selections)
        {
            var request = Choose(selection, action);
            Assert.NotNull(request);

            var plan = ShellLauncher.PlanFor(request!);
            Assert.NotNull(plan);

            Assert.True(
                plan!.Executable == ShellRegistration.ApplicationExecutable
                || plan.Executable == ShellRegistration.CommandLineExecutable,
                $"{action} launches '{plan.Executable}'");

            Assert.DoesNotContain(plan.Arguments, a => a.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(plan.Arguments, a => a.Contains("powershell", StringComparison.OrdinalIgnoreCase));
        }

        // The hostile name arrives as exactly one argument, unquoted and unmodified.
        var created = ShellLauncher.PlanFor(Choose(Files(hostile), ArchiveAction.CreateArchive)!);
        Assert.Equal(1, created!.Arguments.Count(a => a == hostile));
    }

    /// <summary>SHELL-025: the single-member rule has exactly one implementation.</summary>
    /// <remarks>
    /// The shell menu and the Create screen are asked the same question about the same build, and
    /// must not disagree. They cannot, because both call
    /// <see cref="FormatCapabilityReport.PermitsInputCount"/>.
    /// </remarks>
    [Fact]
    public void The_shell_and_the_create_screen_agree_about_single_member_formats()
    {
        var workspace = new FakeWorkspace(new Dictionary<ArchiveFormat, ArchiveCapabilities>(RealBuild));
        var model = new CreateViewModel(workspace, operations: null, exists: static _ => true);
        model.Options.Format = ArchiveFormat.GZip;
        model.DestinationFolder = @"D:\Archives";
        model.ArchiveName = "backup";

        foreach (var count in new[] { 1, 2, 3 })
        {
            var paths = Enumerable.Range(0, count).Select(i => $@"C:\Docs\f{i}.txt").ToArray();

            var entry = Menu(Files(paths))
                .Single(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.GZip);

            model.ClearInputs();
            model.AddInputs(paths);

            Assert.Equal(entry.IsEnabled, model.CanCreate);
        }
    }

    /// <summary>
    /// SHELL-028: a selection naming the same thing twice seeds one input.
    /// </summary>
    /// <remarks>
    /// Explorer will not normally hand over a duplicate, but a selection spanning a junction or a
    /// mapped drive can, and the shell seeds through the same <see cref="CreateViewModel.AddInputs"/>
    /// the picker and the drop zone use — so the protection is the one that already exists rather than
    /// a second copy written for this path.
    /// </remarks>
    [Fact]
    public void A_repeated_path_in_the_selection_seeds_one_input()
    {
        var shell = new ShellViewModel(new FakeWorkspace());
        var selection = Files(@"C:\Docs\File.txt", @"c:\docs\file.txt", @"C:\Docs\other.txt");

        var outcome = ShellRequestSeed.Apply(shell, Choose(selection, ArchiveAction.CreateArchive));

        Assert.Equal(SeedOutcome.CreateSeeded, outcome);
        Assert.Equal(2, shell.Create.Inputs.Count);
        Assert.Equal(
            @"C:\Docs\File.txt | C:\Docs\other.txt",
            string.Join(" | ", shell.Create.Inputs));
    }

    /// <summary>SHELL-026: the containing folder is derived without touching the disk, roots included.</summary>
    [Fact]
    public void The_containing_folder_is_derived_from_the_path_alone()
    {
        Assert.Equal(@"C:\Docs", ShellMenuBuilder.ContainingFolder(@"C:\Docs\report.pdf"));
        Assert.Equal(@"C:\Docs", ShellMenuBuilder.ContainingFolder(@"C:\Docs\Sub\"));
        Assert.Equal(@"C:\", ShellMenuBuilder.ContainingFolder(@"C:\report.pdf"));
        Assert.Equal(@"\\server\share", ShellMenuBuilder.ContainingFolder(@"\\server\share\bundle.7z"));

        Assert.Null(ShellMenuBuilder.ContainingFolder(@"C:\"));
        Assert.Null(ShellMenuBuilder.ContainingFolder(null));
        Assert.Null(ShellMenuBuilder.ContainingFolder("   "));
    }

    /// <summary>
    /// SHELL-029: what the launcher writes, the application reads back.
    /// </summary>
    /// <remarks>
    /// The round trip is the contract. Testing the parser against hand-written arguments would prove
    /// the parser agrees with the test, not with the menu.
    /// </remarks>
    [Fact]
    public void The_application_reads_back_what_the_launcher_wrote()
    {
        var cases = new (ArchiveAction Action, ShellSelection Selection)[]
        {
            (ArchiveAction.OpenInApp, Files(@"C:\Docs\bundle.7z")),
            (ArchiveAction.ExtractTo, Files(@"C:\Docs\bundle.7z")),
            (ArchiveAction.CreateArchive, Files(@"C:\Docs\report.pdf", @"C:\مستندات\تقرير.txt")),
        };

        foreach (var (action, selection) in cases)
        {
            var sent = Choose(selection, action);
            var plan = ShellLauncher.PlanFor(sent!);
            Assert.Equal(ShellRegistration.ApplicationExecutable, plan!.Executable);

            var received = ShellCommandLine.Parse(plan.Arguments);

            Assert.NotNull(received);
            Assert.Equal(action, received!.Action);
            Assert.Equal(string.Join(" | ", sent!.InputPaths), string.Join(" | ", received.InputPaths));
        }
    }

    /// <summary>SHELL-030: anything that is not a shell launch produces the ordinary window.</summary>
    [Fact]
    public void A_command_line_that_is_not_a_shell_launch_is_refused()
    {
        Assert.Null(ShellCommandLine.Parse(null));
        Assert.Null(ShellCommandLine.Parse([]));
        Assert.Null(ShellCommandLine.Parse(["--open"]));
        Assert.Null(ShellCommandLine.Parse([@"C:\Docs\bundle.7z"]));
        Assert.Null(ShellCommandLine.Parse(["--nonsense", @"C:\Docs\bundle.7z"]));

        // A CLI verb is not an application verb: "extract" belongs to noba-ar.
        Assert.Null(ShellCommandLine.Parse(["extract", @"C:\Docs\bundle.7z"]));

        // An option smuggled in after the verb is refused rather than read as a path.
        Assert.Null(ShellCommandLine.Parse(["--open", "--conflict", "overwrite"]));

        // Open acts on one archive; a second path means the caller was not the menu.
        Assert.Null(ShellCommandLine.Parse(["--open", @"C:\a.7z", @"C:\b.7z"]));

        // And --create legitimately takes many.
        Assert.NotNull(ShellCommandLine.Parse(["--create", @"C:\a.txt", @"C:\b.txt"]));
    }

    /// <summary>
    /// SHELL-031: the interim registry labels match the real localized text, and are resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classic handler needs one English string per verb in the registry, and
    /// <see cref="LocalizedStrings"/> holds the product's real text. Two copies of a string is two
    /// copies that can disagree, and the way that shows up is a menu entry worded differently from the
    /// dialog it opens.
    /// </para>
    /// <para>
    /// <b>Amended for DROP 08 rev 2.</b> Equality alone was satisfiable by the defect: both sides read
    /// <c>"Compress to {0}"</c>, so the test passed while <c>{0}</c> shipped to the user (D1). A test
    /// that certifies the bug is worse than no test, because it is cited as evidence the area is
    /// covered. Equality is still required — and now so is being a finished string.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_interim_registry_labels_match_the_localized_text()
    {
        var english = LocalizedStrings.For(UiLanguage.English);

        foreach (var verb in ShellRegistration.Verbs)
        {
            Assert.Equal(english[verb.TextKey], verb.InterimLabel);

        }

        // The verb's text may be a template - "Compress to {0}" is a legitimate format string. What
        // must be finished is the ENTRY the shell shows, which is where the format has been chosen.
        foreach (var entry in Entries(UiLanguage.English))
        {
            Assert.True(
                UnresolvedPlaceholdersIn(entry.Label).Count == 0,
                $"'{entry.Id}' registers the label \"{entry.Label}\", which still carries "
                + $"{string.Join(", ", UnresolvedPlaceholdersIn(entry.Label))}. A registry MUIVerb "
                + "is shown verbatim; a format template reaches the user as literal braces (D1).");
        }

        Assert.Equal(english.ShellMenu, ShellRegistration.InterimMenuTitle);
        Assert.Empty(UnresolvedPlaceholdersIn(ShellRegistration.InterimMenuTitle));
    }

    /// <summary>
    /// SHELL-059: every label that reaches the registration surface is resolved, in both languages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boundary matters. <c>LocalizedStrings["shell.compressTo"]</c> is <em>legitimately</em> a
    /// format template — <c>ShellMenuBuilder</c> emits one CompressTo entry per format and the name
    /// fills the hole. What must never happen is that template arriving at the shell with the hole
    /// still in it, which is exactly what the registration does today.
    /// </para>
    /// <para>
    /// So this asserts on the <em>registration surface</em>, not on the string table: a verb the
    /// registration can express must have a finished label in English and in Arabic. A verb whose text
    /// is a template is not registrable as one entry — it is registrable as one entry per format,
    /// which is SHELL-056's job. This test states the consequence; SHELL-056 states the cause.
    /// </para>
    /// <para>
    /// <b>Expected red until the format dimension exists.</b> Closing this by deleting the placeholder
    /// from the string would be weakening the assertion, not satisfying it: the label would then read
    /// "Compress to" with no format named.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_registered_label_is_resolved_in_both_languages()
    {
        var languages = new[] { UiLanguage.English, UiLanguage.Arabic };
        var offenders = new List<string>();

        foreach (var language in languages)
        {
            var text = LocalizedStrings.For(language);

            foreach (var entry in Entries(language))
            {
                var holes = UnresolvedPlaceholdersIn(entry.Label);

                if (holes.Count > 0)
                {
                    offenders.Add(
                        $"{language.Tag}: '{entry.Id}' would register \"{entry.Label}\" "
                        + $"carrying {string.Join(", ", holes)}");
                }
            }

            var menu = text.ShellMenu;
            if (UnresolvedPlaceholdersIn(menu).Count > 0)
            {
                offenders.Add($"{language.Tag}: the submenu title \"{menu}\" is unresolved");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "an unresolved placeholder reaches the shell registration (D1):\n  "
            + string.Join("\n  ", offenders)
            + "\n\nA verb whose text is a format template must expand to one entry per format "
            + "(SHELL-056), not register once with the template.");
    }

    /// <summary>
    /// SHELL-062: the single-member rule counts archive members, not selected items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D7. <c>PermitsInputCount(selection.Count)</c> asks "how many things did you click?" when the
    /// question is "how many members will the archive hold?" A folder is one click and many members,
    /// so GZIP is offered for a single folder and the engine then refuses it — the exact
    /// menu-promises-what-the-engine-denies failure the capability model exists to prevent.
    /// <c>format-gate.ps1</c> gate 14 already proves the engine's side: compressing a folder to
    /// <c>.gz</c> must fail and leave nothing behind.
    /// </para>
    /// <para>
    /// Per OPEN-2 this is decided by the shared capability model, with no archive-specific or
    /// folder-specific special case in the shell. The rule needs a truthful input, not an exception.
    /// </para>
    /// <para>
    /// <b>Expected red.</b> Pulled forward from Phase 2 because the D7 instruction was to add the
    /// failing contract before the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_single_folder_is_not_offered_a_single_member_format()
    {
        var oneFolder = Folders(@"C:\Projects\ProjectA");
        var oneFile = Files(@"C:\Docs\report.txt");

        var folderGzip = Menu(oneFolder)
            .SingleOrDefault(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.GZip);
        var fileGzip = Menu(oneFile)
            .SingleOrDefault(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.GZip);

        // The control: one real file is exactly one member, and GZIP is right to accept it.
        Assert.NotNull(fileGzip);
        Assert.True(fileGzip!.IsEnabled, "one file is one member; GZIP must remain available for it");

        Assert.NotNull(folderGzip);
        Assert.False(
            folderGzip!.IsEnabled,
            "GZIP is offered for a single folder (D7). A folder is one selection and many members, "
            + "and the engine refuses it - format-gate.ps1 gate 14 'gzip-refuses-many' proves that. "
            + "The count handed to PermitsInputCount must be members, not clicks.");

        Assert.Equal(ShellBlocker.SingleMemberFormat, folderGzip.Blocker);

        // And the multi-member formats are untouched by the correction.
        Assert.True(
            Menu(oneFolder).Single(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.Tar).IsEnabled,
            "TAR holds many members; a folder must remain compressible to it");
    }

    /// <summary>
    /// SHELL-059b: the placeholder rule fires on a template and accepts a finished string.
    /// </summary>
    /// <remarks>
    /// A test that is red today must be shown capable of being green, or it is an assertion nobody
    /// can ever satisfy. This exercises the detector directly, so SHELL-059's redness is evidence
    /// about the product rather than about the detector.
    /// </remarks>
    [Fact]
    public void The_placeholder_rule_catches_a_template_and_accepts_a_finished_label()
    {
        Assert.Equal("{0}", string.Join(",", UnresolvedPlaceholdersIn("Compress to {0}")));
        Assert.Equal("{0},{1}", string.Join(",", UnresolvedPlaceholdersIn("{0} of {1}")));
        Assert.Equal("{0}", string.Join(",", UnresolvedPlaceholdersIn("ضغط إلى {0}")));

        // What a resolved label looks like - these must all pass cleanly.
        Assert.Empty(UnresolvedPlaceholdersIn("Compress to 7Z"));
        Assert.Empty(UnresolvedPlaceholdersIn("ضغط إلى 7Z"));
        Assert.Empty(UnresolvedPlaceholdersIn("Extract here"));
        Assert.Empty(UnresolvedPlaceholdersIn(null));
        Assert.Empty(UnresolvedPlaceholdersIn(""));

        // Braces that are not placeholders must not trip it: a name, not a hole.
        Assert.Empty(UnresolvedPlaceholdersIn(@"C:\Docs\{report}.txt"));
        Assert.Empty(UnresolvedPlaceholdersIn("{}"));
    }

    /// <summary>
    /// The entries the registration would actually write, for one language.
    /// </summary>
    /// <remarks>
    /// The registration <em>surface</em>, not the string table. A verb's text may legitimately be a
    /// format template; an entry's label may not, because an entry has already chosen its format.
    /// </remarks>
    private static IReadOnlyList<ShellRegistration.ShellRegisteredAction> Entries(UiLanguage language)
    {
        var text = LocalizedStrings.For(language);
        return ShellRegistration.Entries(Report, Creatable, key => text[key]);
    }

    /// <summary>
    /// Format placeholders left in a string — <c>{0}</c>, <c>{1}</c>, and so on.
    /// </summary>
    /// <remarks>
    /// Digits only, so an escaped brace or a literal <c>{}</c> in a path-like string is not mistaken
    /// for a hole. What is being detected is a composite format string that nobody composed.
    /// </remarks>
    private static List<string> UnresolvedPlaceholdersIn(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var found = new List<string>();
        for (var i = 0; i < value.Length - 1; i++)
        {
            if (value[i] != '{')
            {
                continue;
            }

            var close = value.IndexOf('}', i + 1);
            if (close <= i + 1)
            {
                continue;
            }

            var inner = value[(i + 1)..close];
            if (inner.All(char.IsAsciiDigit))
            {
                found.Add($"{{{inner}}}");
            }
        }

        return found;
    }

    /// <summary>SHELL-027: both languages carry every shell label.</summary>
    [Fact]
    public void Both_languages_have_the_shell_menu_text()
    {
        var english = LocalizedStrings.For(UiLanguage.English);
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);

        foreach (var pair in new[]
        {
            (english.ShellMenu, arabic.ShellMenu),
            (english.ShellOpen, arabic.ShellOpen),
            (english.ShellTest, arabic.ShellTest),
            (english.ShellExtractHere, arabic.ShellExtractHere),
            (english.ShellExtractTo, arabic.ShellExtractTo),
            (english.ShellCompressTo, arabic.ShellCompressTo),
            (english.ShellCreateArchive, arabic.ShellCreateArchive),
            (english.ShellSingleMemberFormat, arabic.ShellSingleMemberFormat),
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Item1));
            Assert.True(BidiText.ContainsRightToLeft(pair.Item2), $"'{pair.Item2}' is not Arabic");
            Assert.NotEqual(pair.Item1, pair.Item2);
        }

        // "Compress to {0}" takes the format's own name, which is a technical value in both languages.
        Assert.Contains("{0}", english.ShellCompressTo);
        Assert.Contains("{0}", arabic.ShellCompressTo);
    }
}
