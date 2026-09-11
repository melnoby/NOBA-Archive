using System.Text.Json;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// SHELL-054 — the registration surface and the action model must mean the same thing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test whose absence let D1, D2 and D3 ship past 706 green tests.</b> Everything
/// else in the DROP 08 suite exercises <c>ShellSelection</c> → <c>ShellMenuBuilder</c> →
/// <c>ArchiveActionRequest</c> → <c>ShellLauncher</c> in memory. Nothing ever asked what the
/// <em>registration</em> would actually invoke. It turns out to be something else entirely:
/// <c>shell-register.ps1</c> composes its own command string from the verb name alone, so
/// <c>Compress to</c> registers <c>noba-ar create "%1"</c> — the selected file taken as the archive
/// to create, with no inputs and no format — and <c>Extract here</c> registers
/// <c>noba-ar extract "%1"</c> with no destination.
/// </para>
/// <para>
/// <b>Comparison is over semantics, not text.</b> Two spellings that reach the same executable with
/// the same ordered arguments pass; a spelling that reaches a different place fails even if it looks
/// plausible. That is the point — the defect was invisible precisely because the registered strings
/// looked reasonable.
/// </para>
/// <para>
/// <b>Expected red.</b> The manifest carries no argument contract at all today, only a bare verb
/// name, so the comparison cannot even be made. That absence is the finding.
/// </para>
/// </remarks>
public sealed class ShellRegistrationParityTests
{
    /// <summary>
    /// A selection representative of each action, and the request the action model derives from it.
    /// </summary>
    /// <remarks>
    /// Built through <c>ShellMenuBuilder</c> rather than by hand, so the expectation is whatever the
    /// product actually decides — not whatever this test would like it to decide.
    /// </remarks>
    private static readonly Dictionary<ArchiveFormat, ArchiveCapabilities> Build = new()
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
        new(format, Build.TryGetValue(format, out var caps) ? caps : ArchiveCapabilities.None);

    private static IReadOnlyList<ArchiveFormat> Creatable =>
        [ArchiveFormat.SevenZip, ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.GZip];

    /// <summary>The archive every archive-scoped verb is measured against.</summary>
    private const string SampleArchive = @"C:\Docs\Reports\bundle.7z";

    /// <summary>The plain file every file-scoped verb is measured against.</summary>
    private const string SampleFile = @"C:\Docs\report.pdf";

    /// <summary>
    /// SHELL-054: every registered entry invokes what the action model says it should.
    /// </summary>
    /// <remarks>
    /// Structure, not text. The manifest publishes each entry's argument shape as tokens, and the
    /// same shape is what <see cref="ShellLauncher.PlanFor"/> fills in — so a registration that
    /// reaches a different place fails here even when its command string looks plausible, which is
    /// exactly how D2 and D3 hid in plain sight.
    /// </remarks>
    [Fact]
    public void Every_registered_verb_matches_the_action_model()
    {
        var manifest = ReadManifest();
        var entries = ShellRegistration.Entries(Report, Creatable, ShellRegistration.InterimTextFor);
        var failures = new List<string>();

        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            var expected = ShellInvocations.For(entry.Action);
            if (expected is null)
            {
                failures.Add($"{entry.Id}: the action model defines no invocation for {entry.Action}");
                continue;
            }

            var declared = DeclaredArgumentsFor(manifest, entry.Id);
            if (declared is null)
            {
                failures.Add(
                    $"{entry.Id}: the manifest declares no argument contract, so what Windows runs "
                    + $"cannot be compared to the action model. Expected [{string.Join(", ", expected.Tokens)}]");
                continue;
            }

            var want = string.Join(" \u001f ", expected.Tokens);
            var got = string.Join(" \u001f ", declared);

            if (!string.Equals(want, got, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{entry.Id}: registration invokes [{string.Join(", ", declared)}] "
                    + $"but the action model says [{string.Join(", ", expected.Tokens)}]");
            }

            // The declared executable must match too - a right argument list on the wrong binary is
            // still the wrong action.
            var executable = DeclaredExecutableFor(manifest, entry.Id);
            if (!string.Equals(executable, expected.Executable, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{entry.Id}: registration launches '{executable}', the model says '{expected.Executable}'");
            }
        }

        Assert.True(
            failures.Count == 0,
            "the registration and the action model disagree about what a verb does "
            + "(D1/D2/D3 share this root):\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// SHELL-054c: an entry the registry cannot express directly must route through the resolver.
    /// </summary>
    /// <remarks>
    /// A classic registry command substitutes the selection and nothing else. Extract here needs a
    /// containing folder and Compress to needs a suggested name — both derived. The honest answer is
    /// not to approximate them in a registry string but to invoke something that can compute, so the
    /// manifest marks which entries those are and names the verb that resolves them.
    /// </remarks>
    [Fact]
    public void Entries_the_registry_cannot_express_are_marked_and_have_a_resolver()
    {
        var manifest = ReadManifest();

        Assert.True(
            manifest.TryGetProperty("resolverVerb", out var resolver)
            && resolver.GetString() == ShellRegistration.ResolverVerb,
            "the manifest must name the verb that resolves derived arguments");

        foreach (var entry in ShellRegistration.Entries(Report, Creatable, ShellRegistration.InterimTextFor))
        {
            var template = ShellInvocations.For(entry.Action)!;
            var expected = ShellInvocations.IsDirectlyRegistrable(template);
            var declared = DeclaredFlagFor(manifest, entry.Id, "directlyRegistrable");

            Assert.Equal(expected, declared);
        }

        // The two shapes the registry genuinely cannot spell.
        Assert.False(ShellInvocations.IsDirectlyRegistrable(ShellInvocations.For(ArchiveAction.ExtractHere)!));
        Assert.False(ShellInvocations.IsDirectlyRegistrable(ShellInvocations.For(ArchiveAction.CompressTo)!));
        Assert.True(ShellInvocations.IsDirectlyRegistrable(ShellInvocations.For(ArchiveAction.OpenInApp)!));
    }

    /// <summary>
    /// SHELL-054b: the registration may not compose command semantics of its own.
    /// </summary>
    /// <remarks>
    /// The parity check above can only compare what the manifest declares. This holds the other half:
    /// the script must not build a command line from parts, because a script that composes is a
    /// second implementation of the launch contract however well it happens to agree today.
    /// </remarks>
    [Fact]
    public void The_registration_script_composes_no_command_semantics_of_its_own()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "shell-register.ps1"));

        // The shape that produced D2 and D3: executable + verb + "%1", assembled locally.
        var composes = script.Contains("-f $exe, $Verb.verb", StringComparison.Ordinal)
            || script.Contains("-f $exe, $verb.verb", StringComparison.Ordinal);

        Assert.False(
            composes,
            "shell-register.ps1 builds its own command line from the verb name and \"%1\". That is "
            + "how Compress to became `create \"%1\"` with no inputs and no format (D2), and how "
            + "Extract here lost its -o destination (D3). The argument list must come from "
            + "ShellLauncher through the manifest, not be assembled here.");
    }

    /// <summary>
    /// SHELL-056 — CompressTo reaches the registration as one entry per creatable format.
    /// </summary>
    /// <remarks>
    /// The structural half of D1. A format-less <c>CompressTo</c> entry is the thing that had a hole
    /// in its label and no format in its command; if such an entry cannot exist, neither can the
    /// defect. So this asserts the shape rather than the string: every registered CompressTo entry
    /// names a format, the set of formats equals what the capability model says is creatable, and the
    /// ids are distinct because the registry needs one key per visible line.
    /// </remarks>
    [Fact]
    public void Compress_reaches_the_registration_as_one_entry_per_creatable_format()
    {
        var entries = ShellRegistration.Entries(Report, Creatable, ShellRegistration.InterimTextFor);
        var compress = entries.Where(e => e.Action == ArchiveAction.CompressTo).ToList();

        Assert.NotEmpty(compress);

        foreach (var entry in compress)
        {
            Assert.True(
                entry.Format is not null,
                $"'{entry.Id}' registers CompressTo with no format. That entry is D1: its label has "
                + "nowhere to put a format name and its command has no format to pass.");
        }

        var registeredFormats = compress
            .Select(e => e.Format!.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        var expectedFormats = Creatable
            .Where(f => Report(f).CanCreate)
            .Select(static f => f.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedFormats, registeredFormats);

        // One key per visible line, so the ids cannot collide.
        Assert.Equal(compress.Count, compress.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());

        // And the same expansion reached the file Windows reads.
        var manifest = ReadManifest();
        foreach (var entry in compress)
        {
            Assert.NotNull(EntryFor(manifest, entry.Id));
        }
    }

    /// <summary>
    /// SHELL-057 — N selected inputs make one request, not N.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D4. The classic registry's default is one process per selected item, so three files chosen for
    /// "Compress to 7Z" became three single-file archives. The model was never wrong about this — a
    /// selection of three produces one request carrying three — so the fix is that the registration
    /// says so, which is <c>MultiSelectModel</c> and a whole-selection substitution.
    /// </para>
    /// <para>
    /// The two halves are asserted together on purpose: the model's single request, and the
    /// registration declaring the multi-selection behaviour that lets Windows deliver it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_selection_of_many_makes_one_request_carrying_all_of_them()
    {
        string[] three = [@"C:\Docs\a.txt", @"C:\Docs\b.txt", @"C:\Docs\c.txt"];
        var selection = ShellSelection.From(three, static _ => false);

        var line = ShellMenuBuilder
            .Build(selection, Report, Creatable)
            .Single(e => e.Action == ArchiveAction.CompressTo && e.Format == ArchiveFormat.SevenZip);

        var request = ShellMenuBuilder.RequestFor(line, selection);
        Assert.NotNull(request);
        Assert.Equal(three, request!.InputPaths);

        var plan = ShellLauncher.PlanFor(request);
        Assert.NotNull(plan);
        foreach (var path in three)
        {
            var expected = path;
            Assert.Contains(plan!.Arguments, a => string.Equals(a, expected, StringComparison.Ordinal));
        }

        // The registration side: a shape naming the whole selection must be declared as such, and a
        // shape with room for one must be declared as the one the shell hides for a multi-selection
        // rather than repeating (OPEN-1).
        var manifest = ReadManifest();
        foreach (var entry in ShellRegistration.Entries(Report, Creatable, ShellRegistration.InterimTextFor))
        {
            var expected = ShellInvocations.MultiSelectFor(entry.Template).ToString();
            var declared = EntryFor(manifest, entry.Id) is { } e
                && e.TryGetProperty("multiSelect", out var v)
                    ? v.GetString()
                    : null;

            Assert.Equal(expected, declared);
        }

        Assert.Equal(
            ShellMultiSelect.AllItemsAtOnce,
            ShellInvocations.MultiSelectFor(ShellInvocations.For(ArchiveAction.CompressTo)!));
        Assert.Equal(
            ShellMultiSelect.SingleItemOnly,
            ShellInvocations.MultiSelectFor(ShellInvocations.For(ArchiveAction.TestArchive)!));
    }

    /// <summary>
    /// SHELL-072 — the registration script honours the multi-selection contract it is handed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above holds the model and the manifest. This holds the last link: a script that reads
    /// <c>multiSelect: "AllItemsAtOnce"</c> and then writes <c>"%1"</c> and <c>MultiSelectModel =
    /// Document</c> anyway would leave D4 exactly as it was, with every earlier assertion still green.
    /// That gap was found by breaking the script and watching nothing fail.
    /// </para>
    /// <para>
    /// It reads the script's own mapping rather than a copy of it, so a rename or a reordering does
    /// not silently satisfy it — the branch has to exist and has to produce the whole selection.
    /// </para>
    /// <para>
    /// Numbered after SHELL-065 rather than as a suffix inside a range that is spoken for. The
    /// <c>b</c>-suffixed working names from Phase 1 (<c>SHELL-054b</c>, <c>SHELL-054c</c>) stay as they
    /// are; this one was new in Phase 2 and gets a real id.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_registration_script_maps_a_whole_selection_to_a_whole_selection()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "shell-register.ps1"));

        Assert.Equal("%*", BranchResultIn(script, "SelectedPaths"));
        Assert.Equal("\"%1\"", BranchResultIn(script, "ArchivePath"));
        Assert.Equal("Player", BranchResultIn(script, "AllItemsAtOnce"));
        Assert.Equal("Single", BranchResultIn(script, "SingleItemOnly"));

        // The resolver call carries the whole selection too, or Compress to would resolve one file at
        // a time and produce N archives - D4 by a different route.
        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(script, @"AllItemsAtOnce'\s*\)\s*\{\s*'%\*'"),
            "shell-register.ps1 does not hand the whole selection to the resolver for an entry that "
            + "takes all of it; Compress to would resolve one file at a time and produce N archives (D4).");
    }

    /// <summary>
    /// What the script's <c>switch</c> branch for a label returns, as written.
    /// </summary>
    private static string BranchResultIn(string script, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            script,
            $@"'{label}'\s*\{{\s*return\s*(?<value>'[^']*'|""[^""]*"")\s*\}}");

        Assert.True(match.Success, $"shell-register.ps1 has no '{label}' branch to translate.");

        // Strip the PowerShell quoting so the assertion is about the value, not the spelling.
        var value = match.Groups["value"].Value;
        return value[1..^1];
    }

    /// <summary>
    /// SHELL-058 — destinations are carried, never inherited from wherever the process started.
    /// </summary>
    /// <remarks>
    /// D3. "Extract here" means the folder the archive is in. A shell-launched process inherits a
    /// working directory the user never chose — <c>C:\Windows\system32</c> is a common one — so an
    /// extract with no destination does not mean "here", it means "somewhere else". The destination is
    /// therefore explicit in the request and has a slot in the command shape; "Extract to…" carries
    /// none, because its whole purpose is that the person picks one.
    /// </remarks>
    [Fact]
    public void Extract_here_carries_its_destination_and_extract_to_carries_none()
    {
        var selection = ShellSelection.From([SampleArchive], static _ => false);
        var menu = ShellMenuBuilder.Build(selection, Report, Creatable);

        var here = ShellMenuBuilder.RequestFor(
            menu.Single(e => e.Action == ArchiveAction.ExtractHere), selection);

        Assert.NotNull(here);
        Assert.Equal(@"C:\Docs\Reports", here!.Destination);

        var herePlan = ShellLauncher.PlanFor(here);
        Assert.NotNull(herePlan);
        Assert.Contains(herePlan!.Arguments, static a => a == @"C:\Docs\Reports");

        var to = ShellMenuBuilder.RequestFor(
            menu.Single(e => e.Action == ArchiveAction.ExtractTo), selection);

        Assert.NotNull(to);
        Assert.Null(to!.Destination);

        // The shapes, not just this one request: ExtractHere has a destination slot, ExtractTo has
        // none, and nothing anywhere leans on the working directory by omitting one.
        Assert.Contains(
            ShellInvocations.For(ArchiveAction.ExtractHere)!.Arguments,
            static a => a.Kind == ShellArgumentKind.ContainingFolder);

        Assert.DoesNotContain(
            ShellInvocations.For(ArchiveAction.ExtractTo)!.Arguments,
            static a => a.Kind is ShellArgumentKind.ContainingFolder or ShellArgumentKind.SuggestedTarget);
    }

    /// <summary>
    /// SHELL-055 — the registration offers what the capability model allows, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D5. Every verb used to be registered under <c>*</c> and <c>Directory</c>, so "Test archive" and
    /// "Extract here" appeared on a <c>.txt</c> — actions the capability model would never have
    /// offered. The registration now carries a <see cref="ShellScope"/> decided in Core against the
    /// same <see cref="FormatCapabilityReport"/> the menu uses, and this holds it to three rules.
    /// </para>
    /// <para>
    /// <b>Presence, not enabled-ness, and that distinction is honest rather than convenient.</b> The
    /// menu model can show a line greyed with a reason; a classic registry key is present or absent
    /// and has no third state. So an entry the model emits but disables may be omitted — which is what
    /// happens to "Compress to GZIP" on a folder — or offered, in which case the refusal must come
    /// from the model itself through the resolver, never from a command that runs and fails. What may
    /// never happen is the registration offering something the model does not emit at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_registration_offers_only_what_the_capability_model_emits()
    {
        var manifest = ReadManifest();
        var entries = ShellRegistration.Entries(Report, Creatable, ShellRegistration.InterimTextFor);
        var failures = new List<string>();

        (string Name, ShellSelection Selection)[] cases =
        [
            ("one archive", ShellSelection.From([SampleArchive], static _ => false)),
            ("one plain file", ShellSelection.From([SampleFile], static _ => false)),
            ("one folder", ShellSelection.From([@"C:\Docs\Project"], static _ => true)),
            ("three plain files", ShellSelection.From(
                [@"C:\Docs\a.txt", @"C:\Docs\b.txt", @"C:\Docs\c.txt"], static _ => false)),
            ("three folders", ShellSelection.From(
                [@"C:\A", @"C:\B", @"C:\C"], static _ => true)),
        ];

        foreach (var (name, selection) in cases)
        {
            var model = ShellMenuBuilder.Build(selection, Report, Creatable);
            var offered = entries.Where(e => Admits(e, selection)).ToList();

            // 1. Nothing offered that the model does not emit at all.
            foreach (var entry in offered)
            {
                if (!model.Any(m => m.Action == entry.Action
                    && (entry.Format is null || m.Format == entry.Format)))
                {
                    failures.Add(
                        $"{name}: the registration offers '{entry.Id}', which the capability model "
                        + "does not emit for this selection (D5)");
                }
            }

            // 2. Nothing the model enables is missing from the registration.
            foreach (var line in model.Where(static m => m.IsEnabled))
            {
                if (!offered.Any(e => e.Action == line.Action
                    && (e.Format is null || e.Format == line.Format)))
                {
                    failures.Add(
                        $"{name}: the model enables {line.Action}"
                        + (line.Format is { } f ? $" ({f.Id})" : string.Empty)
                        + " but no registered entry covers it");
                }
            }

            // 3. Anything offered that the model would grey must refuse through the resolver, so the
            //    refusal carries the model's reason instead of being a command that runs and fails.
            foreach (var line in model.Where(static m => !m.IsEnabled))
            {
                foreach (var entry in offered.Where(e => e.Action == line.Action
                    && (e.Format is null || e.Format == line.Format)))
                {
                    if (ShellInvocations.IsDirectlyRegistrable(entry.Template))
                    {
                        failures.Add(
                            $"{name}: '{entry.Id}' is registered as a direct command while the model "
                            + $"withholds it ({line.Blocker}); the refusal would come from the engine "
                            + "failing rather than from the capability model");
                    }
                }
            }
        }

        // 4. The manifest's registry spelling must be faithful to the scope the model decided.
        foreach (var entry in entries)
        {
            var scopes = DeclaredScopesFor(manifest, entry.Id);
            if (scopes is null)
            {
                failures.Add($"{entry.Id}: the manifest declares no scopes, so the installer must guess");
                continue;
            }

            Assert.NotEmpty(scopes);

            if (entry.Scope.HasFlag(ShellScope.Archives) && (scopes.Contains("*") || scopes.Contains("Directory")))
            {
                failures.Add(
                    $"{entry.Id}: needs an archive but is registered on every file or folder (D5)");
            }

            if (entry.Scope.HasFlag(ShellScope.Folders) != scopes.Contains("Directory"))
            {
                failures.Add($"{entry.Id}: the folder scope and the 'Directory' key disagree");
            }

            if (entry.Scope.HasFlag(ShellScope.Files) != scopes.Contains("*"))
            {
                failures.Add($"{entry.Id}: the file scope and the '*' key disagree");
            }
        }

        Assert.True(
            failures.Count == 0,
            "the registration surface and the capability model disagree (D5):\n  "
            + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Whether a registered entry's scope covers every item in a selection.
    /// </summary>
    /// <remarks>
    /// Explorer applies a class scope per item and shows a verb only where every selected item
    /// carries it, so the intersection is what a person actually sees. A verb whose shape has room
    /// for one item is registered to disappear when more are selected, which is modelled here as
    /// well — otherwise the comparison would credit the registration with offering a batch the shell
    /// would hide.
    /// </remarks>
    private static bool Admits(ShellRegistration.ShellRegisteredAction entry, ShellSelection selection)
    {
        if (selection.IsEmpty)
        {
            return false;
        }

        if (ShellInvocations.MultiSelectFor(entry.Template) == ShellMultiSelect.SingleItemOnly
            && selection.Count != 1)
        {
            return false;
        }

        foreach (var item in selection.Items)
        {
            var covered =
                (entry.Scope.HasFlag(ShellScope.Archives) && item.LooksLikeArchive)
                || (entry.Scope.HasFlag(ShellScope.Files) && !item.IsDirectory)
                || (entry.Scope.HasFlag(ShellScope.Folders) && item.IsDirectory);

            if (!covered)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string>? DeclaredScopesFor(JsonElement manifest, string entryId)
    {
        if (EntryFor(manifest, entryId) is not { } entry
            || !entry.TryGetProperty("scopes", out var scopes)
            || scopes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var declared = new List<string>();
        foreach (var scope in scopes.EnumerateArray())
        {
            if (scope.GetString() is { } value)
            {
                declared.Add(value);
            }
        }

        return declared;
    }

    /// <summary>
    /// The request the action model derives for an action, via the real menu builder.
    /// </summary>
    private static ArchiveActionRequest? RequestFor(ArchiveAction action)
    {
        var selection = action switch
        {
            ArchiveAction.OpenInApp or ArchiveAction.TestArchive
                or ArchiveAction.ExtractHere or ArchiveAction.ExtractTo
                => ShellSelection.From([SampleArchive], static _ => false),
            _ => ShellSelection.From([SampleFile], static _ => false),
        };

        foreach (var entry in ShellMenuBuilder.Build(selection, Report, Creatable))
        {
            if (entry.Action != action)
            {
                continue;
            }

            // CompressTo is per-format in the model; take the first offered so the comparison has a
            // concrete format to work with. SHELL-056 is what asserts the registration is per-format
            // too - this only needs one representative.
            if (ShellMenuBuilder.RequestFor(entry, selection) is { } request)
            {
                return request;
            }
        }

        return null;
    }

    /// <summary>
    /// The argument list the manifest declares for a verb, or null when it declares none.
    /// </summary>
    /// <remarks>
    /// Read from the generated file rather than from <c>ShellRegistration</c>, because the file is
    /// what the installer consumes. If the two ever diverge, the file is the one Windows obeys.
    /// </remarks>
    private static List<string>? DeclaredArgumentsFor(JsonElement manifest, string verbId)
    {
        if (EntryFor(manifest, verbId) is not { } verb)
        {
            return null;
        }

        // The contract this test requires: a full ordered argument list, not a bare verb name.
        if (!verb.TryGetProperty("arguments", out var arguments)
            || arguments.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var declared = new List<string>();
        foreach (var argument in arguments.EnumerateArray())
        {
            if (argument.GetString() is { } value)
            {
                declared.Add(value);
            }
        }

        return declared;
    }

    private static string? DeclaredExecutableFor(JsonElement manifest, string entryId) =>
        EntryFor(manifest, entryId) is { } e && e.TryGetProperty("executable", out var v)
            ? v.GetString()
            : null;

    private static bool DeclaredFlagFor(JsonElement manifest, string entryId, string name) =>
        EntryFor(manifest, entryId) is { } e && e.TryGetProperty(name, out var v) && v.GetBoolean();

    private static JsonElement? EntryFor(JsonElement manifest, string entryId)
    {
        if (!manifest.TryGetProperty("entries", out var entries))
        {
            return null;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("id", out var id) && id.GetString() == entryId)
            {
                return entry;
            }
        }

        return null;
    }

    private static JsonElement ReadManifest()
    {
        var path = Path.Combine(RepositoryRoot(), "scripts", "shell-verbs.json");
        Assert.True(File.Exists(path), $"{path} is missing; generate it with `noba-ar shell-manifest`");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NOBA.Archive.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("could not locate the repository root from the test binary");
        return string.Empty;
    }
}
