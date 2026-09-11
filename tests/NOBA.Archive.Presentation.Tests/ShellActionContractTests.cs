using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// DROP 08 Phase 3 — the shell surface asserted as a contract rather than case by case.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 and Phase 2 tested behaviours one at a time: RAR is never offered creation, GZIP is
/// refused for many, an archive gets extract actions. Each is true and each was written because
/// somebody thought of it. **This file is the other kind of test** — a table with every row filled
/// in, so a cell that changes has to be changed here, deliberately, in front of somebody.
/// </para>
/// <para>
/// The distinction matters because that is how D7 survived: nothing was wrong with any individual
/// assertion, and no assertion covered a single selected folder. A table has no thought-of-it
/// requirement.
/// </para>
/// <para>
/// <b>Nothing here touches a disk</b>, which is the property under test rather than a testing
/// convenience — <c>ARC-061</c> forbids it under <c>Core/Actions</c>, and menu construction runs
/// inside a held right-click.
/// </para>
/// </remarks>
public sealed class ShellActionContractTests
{
    /// <summary>The build DROP 05 produced: 7Z, ZIP, TAR and GZIP writable, RAR read-only.</summary>
    private static readonly Dictionary<ArchiveFormat, ArchiveCapabilities> RealBuild = new()
    {
        [ArchiveFormat.SevenZip] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.Tar] = ArchiveCapabilities.ReadOnlySet
            | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        [ArchiveFormat.GZip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
        [ArchiveFormat.Rar] = ArchiveCapabilities.ReadOnlySet,
    };

    private static FormatCapabilityReport Report(ArchiveFormat format) =>
        new(format, RealBuild.TryGetValue(format, out var caps) ? caps : ArchiveCapabilities.None);

    private static IReadOnlyList<ArchiveFormat> Creatable =>
        [ArchiveFormat.SevenZip, ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.GZip];

    private static IReadOnlyList<ShellMenuEntry> Menu(ShellSelection selection) =>
        ShellMenuBuilder.Build(selection, Report, Creatable);

    /// <summary>How a format appears for a selection. The three states a menu line can be in.</summary>
    private enum Cell
    {
        /// <summary>Offered and usable.</summary>
        Offered = 0,

        /// <summary>Shown, greyed, with a reason attached.</summary>
        Greyed,

        /// <summary>Not in the menu at all.</summary>
        Absent,
    }

    /// <summary>One row of the selection matrix, stated rather than computed.</summary>
    private sealed record Row(
        string Name,
        (string Path, bool IsDirectory)[] Items,
        ArchiveMemberCount Members,
        Cell SevenZip,
        Cell Zip,
        Cell Tar,
        Cell GZip,
        Cell Rar,
        bool ArchiveActions)
    {
        public ShellSelection Selection => new([.. Items.Select(i => ShellItem.For(i.Path, i.IsDirectory))]);

        public Cell For(ArchiveFormat format) =>
            format == ArchiveFormat.SevenZip ? SevenZip
            : format == ArchiveFormat.Zip ? Zip
            : format == ArchiveFormat.Tar ? Tar
            : format == ArchiveFormat.GZip ? GZip
            : Rar;
    }

    /// <summary>
    /// The complete selection matrix. Thirteen rows, five formats, every cell written down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RAR is <see cref="Cell.Absent"/> in every row and never greyed: a format that can never be
    /// created has nothing to explain, and its absence is <c>CanCreate</c> rather than anything about
    /// the selection. That is why its column does not move down the table.
    /// </para>
    /// <para>
    /// <c>single GZIP</c> and <c>single RAR</c> offer every creatable format, including compressing an
    /// archive into another archive. OPEN-2 and OPEN-3: an archive is an archive for reading and an
    /// ordinary file for compressing, and the decision is the capability model in both directions —
    /// no archive-specific case anywhere.
    /// </para>
    /// </remarks>
    private static readonly Row[] Matrix =
    [
        new("single file", [(@"C:\D\a.txt", false)],
            ArchiveMemberCount.ExactlyOne,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Offered, Cell.Absent, ArchiveActions: false),

        new("multiple files", [(@"C:\D\a.txt", false), (@"C:\D\b.txt", false), (@"C:\D\c.txt", false)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),

        // The D7 row. One click, many members.
        new("single folder", [(@"C:\D\Project", true)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),

        new("multiple folders", [(@"C:\D\P1", true), (@"C:\D\P2", true)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),

        new("file + folder", [(@"C:\D\a.txt", false), (@"C:\D\P1", true)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),

        new("single 7Z", [(@"C:\D\bundle.7z", false)],
            ArchiveMemberCount.ExactlyOne,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Offered, Cell.Absent, ArchiveActions: true),

        new("single ZIP", [(@"C:\D\bundle.zip", false)],
            ArchiveMemberCount.ExactlyOne,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Offered, Cell.Absent, ArchiveActions: true),

        new("single TAR", [(@"C:\D\bundle.tar", false)],
            ArchiveMemberCount.ExactlyOne,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Offered, Cell.Absent, ArchiveActions: true),

        new("single GZIP", [(@"C:\D\bundle.gz", false)],
            ArchiveMemberCount.ExactlyOne,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Offered, Cell.Absent, ArchiveActions: true),

        new("single RAR", [(@"C:\D\bundle.rar", false)],
            ArchiveMemberCount.ExactlyOne,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Offered, Cell.Absent, ArchiveActions: true),

        new("multiple archives", [(@"C:\D\a.zip", false), (@"C:\D\b.7z", false)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),

        new("archive + normal file", [(@"C:\D\a.zip", false), (@"C:\D\notes.txt", false)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),

        new("mixed archive formats",
            [(@"C:\D\a.zip", false), (@"C:\D\b.rar", false), (@"C:\D\c.7z", false)],
            ArchiveMemberCount.Many,
            Cell.Offered, Cell.Offered, Cell.Offered, Cell.Greyed, Cell.Absent, ArchiveActions: false),
    ];

    private static readonly ArchiveFormat[] Columns =
    [
        ArchiveFormat.SevenZip, ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.GZip, ArchiveFormat.Rar,
    ];

    private static readonly ArchiveAction[] SingleArchiveActions =
    [
        ArchiveAction.OpenInApp, ArchiveAction.TestArchive,
        ArchiveAction.ExtractHere, ArchiveAction.ExtractTo,
    ];

    /// <summary>
    /// SHELL-060 — the selection matrix, every cell.
    /// </summary>
    /// <remarks>
    /// Sixty-five compression cells and thirteen member counts, each stated in
    /// <see cref="Matrix"/> and compared against what <see cref="ShellMenuBuilder"/> actually
    /// produces. Failures are collected rather than thrown one at a time, so a change that moves
    /// several cells is reported as a shape rather than as whichever cell happened to be first.
    /// </remarks>
    [Fact]
    public void The_selection_matrix_holds_in_every_cell()
    {
        var failures = new List<string>();

        foreach (var row in Matrix)
        {
            var selection = row.Selection;
            var menu = Menu(selection);

            if (selection.MemberCount != row.Members)
            {
                failures.Add(
                    $"{row.Name}: member count is {selection.MemberCount}, the contract says {row.Members}");
            }

            foreach (var format in Columns)
            {
                var line = menu.SingleOrDefault(m => m.Action == ArchiveAction.CompressTo && m.Format == format);

                var actual = line is null
                    ? Cell.Absent
                    : line.IsEnabled ? Cell.Offered : Cell.Greyed;

                if (actual != row.For(format))
                {
                    failures.Add(
                        $"{row.Name} / Compress to {format.DisplayName}: {actual}, "
                        + $"the contract says {row.For(format)}");
                }

                // A greyed line without a reason is a menu teaching somebody the product is
                // arbitrary. If it is shown and disabled, it says why.
                if (actual == Cell.Greyed && line!.Blocker == ShellBlocker.None)
                {
                    failures.Add($"{row.Name} / Compress to {format.DisplayName}: greyed with no reason");
                }
            }

            var offeredArchiveActions = menu.Any(m => SingleArchiveActions.Contains(m.Action));
            if (offeredArchiveActions != row.ArchiveActions)
            {
                failures.Add(
                    $"{row.Name}: archive actions {(offeredArchiveActions ? "offered" : "absent")}, "
                    + $"the contract says {(row.ArchiveActions ? "offered" : "absent")}");
            }

            // Create archive… is the escape hatch from every refusal above, so it must be present
            // wherever anything at all can be written.
            if (!menu.Any(m => m.Action == ArchiveAction.CreateArchive))
            {
                failures.Add($"{row.Name}: 'Create archive…' is missing, so a refusal has no way out");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"the selection matrix has moved ({failures.Count} cell(s)):\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// SHELL-061 — several archives offer no single-archive action, and that is a decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OPEN-1, stated rather than emergent. Open, Test, Extract here and Extract to each act on one
    /// archive; the engine has no batch request shape for them. Selecting five archives and choosing
    /// "Extract here" could only mean launching five operations behind one click, and a menu entry
    /// that silently did that — or that silently handled only the first — would be worse than its
    /// absence.
    /// </para>
    /// <para>
    /// <b>The withholding must come from the model, not from filtering.</b> OPEN-3 requires the
    /// complete selection to reach the shared contract, so this also asserts the mechanism: the
    /// actions are absent because <see cref="ShellSelection.SingleArchive"/> is null, and every path
    /// is still carried through to the request that <em>is</em> offered.
    /// </para>
    /// </remarks>
    [Fact]
    public void Several_archives_offer_no_single_archive_action()
    {
        (string Name, string[] Paths)[] selections =
        [
            ("two archives", [@"C:\D\a.zip", @"C:\D\b.7z"]),
            ("five archives", [@"C:\D\a.zip", @"C:\D\b.7z", @"C:\D\c.tar", @"C:\D\d.gz", @"C:\D\e.rar"]),
            ("archive + normal file", [@"C:\D\a.zip", @"C:\D\notes.txt"]),
            ("mixed archive formats", [@"C:\D\a.zip", @"C:\D\b.rar", @"C:\D\c.7z"]),
        ];

        foreach (var (name, paths) in selections)
        {
            var selection = ShellSelection.From(paths, static _ => false);

            // The mechanism: no single archive, therefore no single-archive action.
            Assert.Null(selection.SingleArchive);

            var menu = Menu(selection);
            foreach (var action in SingleArchiveActions)
            {
                Assert.DoesNotContain(menu, m => m.Action == action);
            }

            // And nothing was filtered on the way (OPEN-3): the complete selection reaches the
            // request, in order, archives included.
            var compress = menu.Single(m => m.Action == ArchiveAction.CompressTo && m.Format == ArchiveFormat.Zip);
            var request = ShellMenuBuilder.RequestFor(compress, selection);

            Assert.NotNull(request);
            Assert.Equal(paths, request!.InputPaths);

            _ = name;
        }

        // The control: one archive still offers all four, so the rule above is about the count and
        // not about the actions having quietly disappeared.
        var one = ShellSelection.From([@"C:\D\a.zip"], static _ => false);
        foreach (var action in SingleArchiveActions)
        {
            Assert.Contains(Menu(one), m => m.Action == action);
        }
    }

    /// <summary>
    /// SHELL-074 — every action, with its valid selection, its invalid ones, and what it produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-action counterpart to SHELL-060's per-selection table. Each action states the
    /// selection it accepts and the selections it must refuse, and the assertion runs both
    /// directions — an action offered where it should not be fails just as loudly as one missing
    /// where it should be.
    /// </para>
    /// <para>
    /// <b>Refusal here means "no request", not "a request that fails later".</b> The check goes
    /// through <see cref="ShellMenuBuilder.RequestFor"/>, so an action that is somehow reachable but
    /// cannot produce a well-formed request is caught in the same pass.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_action_accepts_what_it_should_and_refuses_what_it_should_not()
    {
        var archive = ShellSelection.From([@"C:\D\Reports\bundle.7z"], static _ => false);
        var plain = ShellSelection.From([@"C:\D\notes.txt"], static _ => false);
        var folder = ShellSelection.From([@"C:\D\Project"], static _ => true);
        var manyFiles = ShellSelection.From([@"C:\D\a.txt", @"C:\D\b.txt"], static _ => false);
        var manyArchives = ShellSelection.From([@"C:\D\a.zip", @"C:\D\b.7z"], static _ => false);
        var empty = ShellSelection.From([], static _ => false);

        (string Name, ArchiveAction Action, ArchiveFormat? Format,
            ShellSelection[] Accepts, ShellSelection[] Refuses)[] contracts =
        [
            ("Open", ArchiveAction.OpenInApp, null,
                [archive], [plain, folder, manyFiles, manyArchives, empty]),

            ("Test", ArchiveAction.TestArchive, null,
                [archive], [plain, folder, manyFiles, manyArchives, empty]),

            ("Extract here", ArchiveAction.ExtractHere, null,
                [archive], [plain, folder, manyFiles, manyArchives, empty]),

            ("Extract to…", ArchiveAction.ExtractTo, null,
                [archive], [plain, folder, manyFiles, manyArchives, empty]),

            ("Compress to 7Z", ArchiveAction.CompressTo, ArchiveFormat.SevenZip,
                [archive, plain, folder, manyFiles, manyArchives], [empty]),

            ("Compress to ZIP", ArchiveAction.CompressTo, ArchiveFormat.Zip,
                [archive, plain, folder, manyFiles, manyArchives], [empty]),

            ("Compress to TAR", ArchiveAction.CompressTo, ArchiveFormat.Tar,
                [archive, plain, folder, manyFiles, manyArchives], [empty]),

            // The single-member rule, per selection rather than per action.
            ("Compress to GZIP", ArchiveAction.CompressTo, ArchiveFormat.GZip,
                [archive, plain], [folder, manyFiles, manyArchives, empty]),

            // Never offered: RAR cannot be created, in any selection.
            ("Compress to RAR", ArchiveAction.CompressTo, ArchiveFormat.Rar,
                [], [archive, plain, folder, manyFiles, manyArchives, empty]),

            ("Create archive…", ArchiveAction.CreateArchive, null,
                [archive, plain, folder, manyFiles, manyArchives], [empty]),
        ];

        var failures = new List<string>();

        foreach (var (name, action, format, accepts, refuses) in contracts)
        {
            foreach (var selection in accepts)
            {
                var line = Menu(selection)
                    .SingleOrDefault(m => m.Action == action && (format is null || m.Format == format));

                if (line is null || !line.IsEnabled)
                {
                    failures.Add($"{name}: withheld for a selection it must accept ({Describe(selection)})");
                    continue;
                }

                if (ShellMenuBuilder.RequestFor(line, selection) is null)
                {
                    failures.Add($"{name}: offered for {Describe(selection)} but produces no request");
                }
            }

            foreach (var selection in refuses)
            {
                var line = Menu(selection)
                    .SingleOrDefault(m => m.Action == action && (format is null || m.Format == format));

                if (line is { IsEnabled: true })
                {
                    failures.Add($"{name}: offered for a selection it must refuse ({Describe(selection)})");
                }

                // Whether it is greyed or absent is the mechanism's business; producing a request is
                // not, and never is.
                if (line is not null && ShellMenuBuilder.RequestFor(line, selection) is not null && !line.IsEnabled)
                {
                    failures.Add($"{name}: disabled for {Describe(selection)} but still produced a request");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"the per-action contract has moved ({failures.Count}):\n  " + string.Join("\n  ", failures));
    }

    private static string Describe(ShellSelection selection) =>
        selection.IsEmpty
            ? "nothing selected"
            : $"{selection.Count} item(s), {selection.MemberCount}";

    /// <summary>
    /// SHELL-075 — every destination is carried, and none is inherited from the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shell-launched process inherits a working directory the person never chose —
    /// <c>C:\Windows\system32</c> is a common one. So "here" cannot mean "wherever this started", and
    /// an action that omits a destination it needs is not neutral, it is wrong somewhere the user
    /// cannot see. That was D3.
    /// </para>
    /// <para>
    /// The three destination shapes, each asserted for what it must be <em>and</em> what it must not:
    /// <c>Extract here</c> carries the archive's folder; <c>Extract to…</c> carries none, because the
    /// person supplies it in the existing dialog; a quick compress carries a target derived from the
    /// selection by a rule with no randomness and no clock in it.
    /// </para>
    /// <para>
    /// <b>Every argument is absolute.</b> The structural half: no plan for any action may contain a
    /// relative path, because a relative path is exactly the CWD fallback wearing different clothes.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_action_inherits_its_destination_from_the_working_directory()
    {
        var archive = ShellSelection.From([@"C:\D\Reports\bundle.7z"], static _ => false);

        // Extract here: the archive's own folder, explicitly.
        var here = ShellMenuBuilder.RequestFor(
            Menu(archive).Single(m => m.Action == ArchiveAction.ExtractHere), archive);
        Assert.Equal(@"C:\D\Reports", here!.Destination);

        // Extract to…: no destination, deliberately.
        var to = ShellMenuBuilder.RequestFor(
            Menu(archive).Single(m => m.Action == ArchiveAction.ExtractTo), archive);
        Assert.Null(to!.Destination);

        // Compress: a deterministic target, derived from the selection.
        var onePdf = ShellSelection.From([@"C:\D\report.pdf"], static _ => false);
        var oneFolder = ShellSelection.From([@"C:\D\Project.Alpha"], static _ => true);
        var several = ShellSelection.From([@"C:\D\Sales\a.txt", @"C:\D\Sales\b.txt"], static _ => false);

        // Collected rather than thrown one at a time: a naming rule that has moved usually moves
        // several cases, and the first failure alone would misdescribe the shape of the problem.
        var targets = new List<string>();

        void Expect(string expected, ShellSelection selection, ArchiveFormat format, string why)
        {
            var actual = CompressTarget(selection, format);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                targets.Add($"{why}: expected '{expected}', got '{actual}'");
            }
        }

        Expect(@"C:\D\report.7z", onePdf, ArchiveFormat.SevenZip,
            "one file keeps its base name and takes the format's extension");
        Expect(@"C:\D\report.zip", onePdf, ArchiveFormat.Zip,
            "the same file, a different format");

        // A folder keeps all of its name: "Project.Alpha" is not "Project" with an extension. Two
        // folders in one parent that differ only after a dot must not propose the same archive.
        Expect(@"C:\D\Project.Alpha.zip", oneFolder, ArchiveFormat.Zip,
            "a folder name is not a file name with an extension");

        // Several items take the name of the folder they are in, which is what a person expects to
        // find afterwards.
        Expect(@"C:\D\Sales\Sales.zip", several, ArchiveFormat.Zip,
            "several items are named for their containing folder");

        // Deterministic: the same selection asked twice gives the same answer. Nothing consults a
        // clock, a counter or the disk, so this cannot become "report (2).7z" without somebody
        // deciding it should.
        Assert.Equal(
            CompressTarget(onePdf, ArchiveFormat.SevenZip),
            CompressTarget(onePdf, ArchiveFormat.SevenZip));

        // And the structural rule, across every action the menu can produce.
        foreach (var selection in Matrix.Select(static r => r.Selection))
        {
            foreach (var line in Menu(selection).Where(static m => m.IsEnabled))
            {
                if (ShellMenuBuilder.RequestFor(line, selection) is not { } request
                    || ShellLauncher.PlanFor(request) is not { } plan)
                {
                    continue;
                }

                foreach (var argument in plan.Arguments)
                {
                    // Switches are not paths; everything else that looks like one must be rooted.
                    if (argument.StartsWith('-') || !argument.Contains('\\', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (argument.Length <= 2 || argument[1] != ':')
                    {
                        targets.Add(
                            $"{line.Action} passes '{argument}', which is relative — a relative path "
                            + "is the working-directory fallback in disguise (D3)");
                    }
                }
            }
        }

        Assert.True(
            targets.Count == 0,
            $"a destination is not what the contract says ({targets.Count}):\n  "
            + string.Join("\n  ", targets));
    }

    private static string? CompressTarget(ShellSelection selection, ArchiveFormat format)
    {
        var line = Menu(selection).Single(m => m.Action == ArchiveAction.CompressTo && m.Format == format);
        var request = ShellMenuBuilder.RequestFor(line, selection);

        return request is null
            ? null
            : ShellLauncher.SuggestedArchivePath(request, request.Destination!, format.PrimaryExtension);
    }

    /// <summary>
    /// SHELL-078 — directory identity survives the whole path, and nothing looks it up twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression for SHELL-075. That defect was not a naming bug — it was
    /// <c>ArchiveActionRequest</c> dropping the one fact Explorer had supplied, and
    /// <c>SuggestedArchivePath</c> reconstructing it from the input count. So this asserts the fact
    /// travelling rather than the name coming out right: the request carries the same items the
    /// selection held, and the name follows from them.
    /// </para>
    /// <para>
    /// <b>The collision is the point.</b> <c>Project.Alpha</c> and <c>Project.Beta</c> in one parent
    /// used to propose the same <c>Project.zip</c>, so the second was refused as already existing —
    /// naming a file the person never asked for, about a folder they never compressed.
    /// </para>
    /// <para>
    /// <b>No second lookup.</b> The items come from the selection and are compared by reference-equal
    /// value, so a caller that re-derived <c>IsDirectory</c> from anywhere — the disk, the extension,
    /// the count — would have to produce different items to pass, and could not produce the same ones
    /// by accident.
    /// </para>
    /// </remarks>
    [Fact]
    public void Directory_identity_travels_from_the_selection_to_the_suggested_name()
    {
        // F — the request carries the selection's own items, folder flags intact.
        var mixed = ShellSelection.From([@"C:\D\a.txt", @"C:\D\Project"], p => p.EndsWith("Project", StringComparison.Ordinal));
        var line = Menu(mixed).Single(m => m.Action == ArchiveAction.CompressTo && m.Format == ArchiveFormat.Zip);
        var request = ShellMenuBuilder.RequestFor(line, mixed);

        Assert.NotNull(request);
        Assert.Equal(mixed.Items, request!.Inputs);
        Assert.False(request.Inputs[0].IsDirectory);
        Assert.True(request.Inputs[1].IsDirectory);

        // Create archive… carries it too — the Create screen is where it is needed next.
        var create = ShellMenuBuilder.RequestFor(
            Menu(mixed).Single(m => m.Action == ArchiveAction.CreateArchive), mixed);
        Assert.Equal(mixed.Items, create!.Inputs);

        // A, B and C — two folders differing only after a dot propose two different archives.
        var alpha = Folder(@"C:\D\Project.Alpha");
        var beta = Folder(@"C:\D\Project.Beta");

        Assert.Equal(@"C:\D\Project.Alpha.zip", Target(alpha, ArchiveFormat.Zip));
        Assert.Equal(@"C:\D\Project.Beta.zip", Target(beta, ArchiveFormat.Zip));
        Assert.NotEqual(Target(alpha, ArchiveFormat.Zip), Target(beta, ArchiveFormat.Zip));

        // …and the same folder name as a FILE still loses its extension, so the rule is reading the
        // flag rather than having been switched off (D).
        var file = ShellSelection.From([@"C:\D\Project.Alpha"], static _ => false);
        Assert.Equal(@"C:\D\Project.zip", Target(file, ArchiveFormat.Zip));

        // E — several inputs are unchanged: named for the folder they sit in, nothing stripped.
        var several = ShellSelection.From([@"C:\D\Sales\a.txt", @"C:\D\Sales\b.txt"], static _ => false);
        Assert.Equal(@"C:\D\Sales\Sales.zip", Target(several, ArchiveFormat.Zip));

        var severalFolders = ShellSelection.From([@"C:\D\Q1.Reports\A", @"C:\D\Q1.Reports\B"], static _ => true);
        Assert.Equal(@"C:\D\Q1.Reports\Q1.Reports.zip", Target(severalFolders, ArchiveFormat.Zip));
    }

    /// <summary>
    /// SHELL-078b — the application-hosted path cannot read a flag it was never given.
    /// </summary>
    /// <remarks>
    /// <c>ShellCommandLine.Parse</c> builds a request from <c>argv</c>, which carries paths and
    /// nothing else, so it records <c>IsDirectory: false</c> — it cannot know, and ARC-061 forbids it
    /// finding out. That is safe only while no application-hosted action consults the flag, which is
    /// true because none of their command shapes contains a <see cref="ShellArgumentKind.SuggestedTarget"/>
    /// slot — the only thing that reads it. Asserted rather than left as a comment, because a comment
    /// is exactly what SuggestedArchivePath already had.
    /// </remarks>
    [Fact]
    public void No_application_hosted_action_consults_the_directory_flag()
    {
        foreach (var verb in ShellRegistration.Verbs.Where(static v => v.Host == ShellHost.Application))
        {
            var template = ShellInvocations.For(verb.Action);
            Assert.NotNull(template);

            Assert.DoesNotContain(
                template!.Arguments,
                static a => a.Kind == ShellArgumentKind.SuggestedTarget);
        }

        // And the parser does hand back well-formed items, so nothing downstream sees a null path.
        var parsed = ShellCommandLine.Parse(["--create", @"C:\D\a.txt", @"C:\D\b.txt"]);
        Assert.NotNull(parsed);
        Assert.Equal([@"C:\D\a.txt", @"C:\D\b.txt"], parsed!.InputPaths);
    }

    private static ShellSelection Folder(string path) =>
        ShellSelection.From([path], static _ => true);

    private static string? Target(ShellSelection selection, ArchiveFormat format) =>
        CompressTarget(selection, format);

    /// <summary>
    /// SHELL-076 — the four Compress actions are four different actions, all the way to the plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four entries share one command <em>shape</em> — that is the point of there being one table
    /// — so a test comparing shapes would find them identical and prove nothing. What must differ is
    /// what fills the shape: the format id passed to the engine, and the extension of the archive it
    /// writes. If those collapsed, every Compress line would produce the same archive and the menu
    /// would be four labels over one action.
    /// </para>
    /// <para>
    /// This is the assertion that would have caught D1 from the other end: a format-less CompressTo
    /// cannot produce a distinct plan because it has no format to put in one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Each_compress_format_reaches_execution_as_itself()
    {
        var selection = ShellSelection.From([@"C:\D\report.pdf"], static _ => false);
        var plans = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var format in Creatable)
        {
            var line = Menu(selection).Single(m => m.Action == ArchiveAction.CompressTo && m.Format == format);
            var request = ShellMenuBuilder.RequestFor(line, selection);

            Assert.NotNull(request);
            Assert.Equal(format, request!.Format);

            var plan = ShellLauncher.PlanFor(request);
            Assert.NotNull(plan);

            // The format travels as an explicit argument, not as an inference from the file name.
            var switchAt = plan!.Arguments.ToList().IndexOf("--format");
            Assert.True(switchAt >= 0, $"{format.Id}: the plan carries no --format switch");
            Assert.Equal(format.Id, plan.Arguments[switchAt + 1]);

            // And the archive it writes is named for that format.
            var target = ShellLauncher.SuggestedArchivePath(request, request.Destination!, format.PrimaryExtension);
            Assert.EndsWith(format.PrimaryExtension, target);

            plans[format.Id] = plan.Arguments;
        }

        // Four formats, four distinct argument lists. A collapse here means the menu is four labels
        // over one action.
        var distinct = plans.Values
            .Select(static a => string.Join('\u001f', a))
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.Equal(Creatable.Count, distinct);

        // RAR is not among them, in any selection, because it cannot be created (ADR-0004).
        Assert.DoesNotContain(plans.Keys, k => k == ArchiveFormat.Rar.Id);
    }

    /// <summary>
    /// SHELL-077 — a format no provider claims is advertised nowhere and produces nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SHELL-011 asserts BZIP2 and XZ never appear in the menu even when a caller offers them. This
    /// holds the half after that: if one somehow reached the request layer anyway, it must not become
    /// a launch. An unavailable format that silently produced a plan would be a menu the engine
    /// cannot honour, arriving by a different door.
    /// </para>
    /// <para>
    /// The refusal is the capability model's, not a name check — <c>Report</c> returns
    /// <see cref="ArchiveCapabilities.None"/> for anything no provider claims, and that is what
    /// withholds the entry. A provider appearing for BZIP2 tomorrow changes this test's outcome
    /// without changing this test.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_format_no_provider_claims_is_offered_nowhere_and_launches_nothing()
    {
        ArchiveFormat[] unclaimed = [ArchiveFormat.BZip2, ArchiveFormat.Xz];

        foreach (var format in unclaimed)
        {
            Assert.False(Report(format).IsAvailable);
            Assert.False(Report(format).CanCreate);

            // Offered to the builder deliberately: it filters by capability rather than trusting the
            // caller's list.
            foreach (var row in Matrix)
            {
                var menu = ShellMenuBuilder.Build(
                    row.Selection, Report, [.. Creatable, format]);

                Assert.DoesNotContain(menu, m => m.Format == format);
            }
        }

        // And RAR, which a provider does claim but only for reading.
        Assert.True(Report(ArchiveFormat.Rar).IsAvailable);
        Assert.True(Report(ArchiveFormat.Rar).IsReadOnly);

        foreach (var row in Matrix)
        {
            var menu = ShellMenuBuilder.Build(row.Selection, Report, [.. Creatable, ArchiveFormat.Rar]);

            Assert.DoesNotContain(
                menu,
                m => m.Action == ArchiveAction.CompressTo && m.Format == ArchiveFormat.Rar);
        }
    }
}
