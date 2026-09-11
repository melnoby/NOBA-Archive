using System.Text.RegularExpressions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// Rules about the WinUI project's SOURCE, enforced here because nothing else can enforce them.
/// </summary>
/// <remarks>
/// <para>
/// <c>NOBA.Archive.App</c> is the one project that cannot be compiled outside Windows, so a defect in
/// it costs a full round trip through a human: send the drop, build on Windows, read the transcript,
/// send it back. That is the right price for a genuinely Windows-shaped problem — a XAML compiler
/// quirk, a WinUI lifecycle rule. It is a ridiculous price for a missing <c>using</c>.
/// </para>
/// <para>
/// And a missing <c>using</c> is exactly what cost one:
/// <c>MainWindow.xaml.cs</c> named <c>OperationState</c> three times without importing
/// <c>NOBA.Archive.Core.Operations</c>, and the only thing in the world that noticed was
/// <c>XamlCompiler.exe</c>. The rule below is mechanical, needs no compiler, and would have caught it
/// on the machine that wrote it.
/// </para>
/// <para>
/// The check is deliberately narrow: it only knows about types this repository declares, and it only
/// looks at positions where a name must be a type. It cannot see framework types, and it is not trying
/// to be a compiler — it is trying to make one specific, repeated, expensive mistake impossible.
/// </para>
/// </remarks>
public sealed class WinUiSourceTests
{
    /// <summary>ARC-050: every project type the WinUI sources name is imported by the file naming it.</summary>
    [Fact]
    public void Every_project_type_named_in_the_WinUI_project_is_in_scope()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var owned = DeclaredTypes(root!);
        Assert.True(owned.Count > 100, $"only {owned.Count} project types were found; the scan is broken");

        var app = Path.Combine(root!, "src", "NOBA.Archive.App");
        var offenders = new List<string>();

        foreach (var file in Sources(app))
        {
            foreach (var (type, line) in UnresolvedTypes(File.ReadAllText(file), owned))
            {
                offenders.Add($"{Path.GetFileName(file)}:{line} names '{type}' without importing it");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "the WinUI project will not compile:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// ARC-051: the rule above actually fires.
    /// </summary>
    /// <remarks>
    /// A source-scanning rule that quietly matches nothing passes forever and protects nothing. This
    /// feeds it the exact shape of the defect it exists for and requires it to object.
    /// </remarks>
    [Fact]
    public void The_scope_rule_rejects_a_type_that_is_not_imported()
    {
        var owned = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["OperationState"] = new(StringComparer.Ordinal) { "NOBA.Archive.Core.Operations" },
        };

        const string missing = """
            using Microsoft.UI.Xaml;

            namespace NOBA.Archive.App;

            internal sealed class Sample
            {
                public bool Running(object o) => o is OperationState.Running;
            }
            """;

        var found = UnresolvedTypes(missing, owned);
        Assert.Single(found);
        Assert.Equal("OperationState", found[0].Type);

        const string imported = """
            using Microsoft.UI.Xaml;
            using NOBA.Archive.Core.Operations;

            namespace NOBA.Archive.App;

            internal sealed class Sample
            {
                public bool Running(object o) => o is OperationState.Running;
            }
            """;

        Assert.Empty(UnresolvedTypes(imported, owned));
    }

    /// <summary>
    /// ARC-052: a type named only in a comment or a string is not a reference.
    /// </summary>
    /// <remarks>
    /// The first version of this rule reported three files for naming <c>FlowDirection</c>, all of
    /// which were assigning the WinUI property of that name. A check that cries wolf is a check people
    /// learn to skip, so the distinction is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void A_property_or_a_comment_is_not_a_type_reference()
    {
        var owned = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["FlowDirection"] = new(StringComparer.Ordinal) { "NOBA.Archive.Presentation.Bidi" },
        };

        const string source = """
            namespace NOBA.Archive.App;

            internal sealed class Sample
            {
                // FlowDirection is set once, on the root element.
                public void Apply(object block) => Report("FlowDirection");

                private static void Report(string what) { }
            }
            """;

        Assert.Empty(UnresolvedTypes(source, owned));
    }

    /// <summary>
    /// Simple names that Microsoft.UI.Xaml and its neighbours also declare.
    /// </summary>
    /// <remarks>
    /// Curated rather than derived: WinUI's metadata is not on the machine that runs this test, and a
    /// list that pretends otherwise would be worse than one that admits what it knows. Only names this
    /// repository actually declares can ever collide, so the list stays short by construction.
    /// </remarks>
    private static readonly string[] WinUiNames =
    [
        "FlowDirection", "Style", "Application", "Window", "Grid", "Button", "Border", "TextBlock",
        "Thickness", "Visibility", "Color", "Colors", "Frame", "Page", "Point", "Size", "Rect", "Path",
        "Binding", "Orientation", "Brush", "Control", "Panel", "ContentDialog", "ProgressBar",
        "CheckBox", "RadioButton", "TextBox", "ListView", "ScrollViewer", "Setter", "ResourceDictionary",
    ];

    /// <summary>
    /// ARC-053: no WinUI source names a type that is ambiguous between our namespaces and the framework's.
    /// </summary>
    /// <remarks>
    /// <c>OperationRow</c> assigned <c>FlowDirection.LeftToRight</c> while importing both
    /// <c>Microsoft.UI.Xaml</c> and <c>NOBA.Archive.Presentation.Bidi</c>, each of which declares a
    /// <c>FlowDirection</c>. The compiler will not choose between them, and the only compiler that
    /// sees this project runs on the other machine — so a missing namespace prefix cost a full round
    /// trip, one drop after a missing <c>using</c> cost the previous one.
    /// </remarks>
    [Fact]
    public void No_WinUI_source_names_an_ambiguous_type()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var owned = DeclaredTypes(root!);
        var app = Path.Combine(root!, "src", "NOBA.Archive.App");
        var offenders = new List<string>();

        foreach (var file in Sources(app))
        {
            foreach (var (type, line) in AmbiguousTypes(File.ReadAllText(file), owned))
            {
                offenders.Add($"{Path.GetFileName(file)}:{line} names '{type}' bare");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "the WinUI project will not compile - qualify these:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>ARC-054: the ambiguity rule fires, and a qualified use satisfies it.</summary>
    [Fact]
    public void The_ambiguity_rule_accepts_a_qualified_use_and_rejects_a_bare_one()
    {
        var owned = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["FlowDirection"] = new(StringComparer.Ordinal) { "NOBA.Archive.Presentation.Bidi" },
        };

        const string bare = """
            using Microsoft.UI.Xaml;
            using NOBA.Archive.Presentation.Bidi;

            namespace NOBA.Archive.App.Views;

            internal sealed class Sample
            {
                public void Apply(TextBlock block) => block.FlowDirection = FlowDirection.LeftToRight;
            }
            """;

        var found = AmbiguousTypes(bare, owned);
        Assert.Single(found);
        Assert.Equal("FlowDirection", found[0].Type);

        const string qualified = """
            using Microsoft.UI.Xaml;
            using NOBA.Archive.Presentation.Bidi;

            namespace NOBA.Archive.App.Views;

            internal sealed class Sample
            {
                public void Apply(TextBlock block) =>
                    block.FlowDirection = Microsoft.UI.Xaml.FlowDirection.LeftToRight;
            }
            """;

        Assert.Empty(AmbiguousTypes(qualified, owned));
    }

    /// <summary>
    /// Names that are in scope from both a namespace of ours and a WinUI one, used unqualified.
    /// </summary>
    /// <remarks>
    /// A qualified use needs no entry here and gets none: every pattern in
    /// <see cref="FirstTypePosition"/> refuses a name preceded by a dot.
    /// </remarks>
    private static List<(string Type, int Line)> AmbiguousTypes(
        string source, Dictionary<string, SortedSet<string>> owned)
    {
        var clean = StripNoise(source);
        var visible = VisibleNamespaces(clean);
        var found = new List<(string, int)>();

        if (!visible.Any(static ns => ns.StartsWith("Microsoft.UI", StringComparison.Ordinal)))
        {
            return found;
        }

        var aliased = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(clean, @"^\s*(?:global\s+)?using\s+(\w+)\s*=", RegexOptions.Multiline))
        {
            aliased.Add(m.Groups[1].Value);
        }

        var body = Regex.Replace(clean, @"^\s*(?:global\s+)?using\s+[^\n]*;", string.Empty, RegexOptions.Multiline);

        foreach (var name in WinUiNames.Where(owned.ContainsKey).OrderBy(static n => n, StringComparer.Ordinal))
        {
            if (aliased.Contains(name) || !owned[name].Overlaps(visible))
            {
                continue;
            }

            var line = FirstTypePosition(body, name);
            if (line > 0)
            {
                found.Add((name, line));
            }
        }

        return found;
    }

    /// <summary>
    /// The shapes a format decision takes when it leaks into a screen.
    /// </summary>
    /// <remarks>
    /// Naming a format constant, testing a capability flag, or comparing against a format's name or
    /// extension. Each of them is the UI deciding for itself what a format can do, which is the thing
    /// the capability contract exists to prevent.
    /// </remarks>
    private static readonly (string What, Regex Pattern)[] FormatDecisions =
    [
        ("names a format constant", new Regex(@"\bArchiveFormat\s*\.\s*[A-Z]\w*", RegexOptions.Compiled)),
        ("tests a capability flag", new Regex(@"\bArchiveCapabilities\s*\.\s*\w+", RegexOptions.Compiled)),
        ("compares against a format name",
            new Regex(@"""\.?(?:7z|zip|rar|tar|gz|gzip|bz2|bzip2|xz)""", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>
    /// ARC-055: the WinUI project contains no format capability decisions of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every "can this format do X" question belongs to the provider registry, reaches the view models
    /// as <c>FormatCapabilityReport</c>, and arrives at a screen as a boolean that is already true or
    /// false. The failure this prevents is the one every archive tool eventually has: a
    /// <c>if (format == Rar) hideButton()</c> in one screen, a different one in the next, and a third
    /// screen that was never told — so adding a format means auditing the whole UI, and removing one
    /// means missing a case.
    /// </para>
    /// <para>
    /// The rule is worth stating even though the project satisfies it today: it locks in a property that
    /// is easy to hold now and expensive to recover once lost, and the next drop adds screens.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_WinUI_project_makes_no_format_decisions()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var app = Path.Combine(root!, "src", "NOBA.Archive.App");
        var files = Sources(app).Concat(Markup(app)).ToList();
        Assert.True(files.Count > 5, $"only {files.Count} app files were scanned; the scan is broken");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var (what, line, text) in FormatDecisionsIn(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}:{line} {what} ({text})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a screen is deciding what a format can do; ask the capability report instead:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>ARC-056: the format rule fires on each shape, and leaves an honest screen alone.</summary>
    /// <remarks>
    /// Three positives rather than one, because the three patterns are independent: a rule that only
    /// ever matched the first would still pass this test if it were written with a single assertion.
    /// </remarks>
    [Fact]
    public void The_format_rule_rejects_each_shape_and_accepts_an_honest_screen()
    {
        const string constant = "if (Model.Options.Format == ArchiveFormat.Rar) { CreateButton.IsEnabled = false; }";
        const string flag = "SolidBox.IsEnabled = capabilities.Has(ArchiveCapabilities.SolidCompression);";
        const string literal = "if (Path.GetExtension(name) == \".rar\") { return; }";

        foreach (var offender in new[] { constant, flag, literal })
        {
            Assert.NotEmpty(FormatDecisionsIn(offender));
        }

        // What the screen is supposed to look like: everything already decided by the model.
        const string honest = """
            // ArchiveFormat.Rar is deliberately not mentioned here; the report decides.
            CreateButton.IsEnabled = Model.CanCreate;
            SolidBox.IsEnabled = Model.Options.Solid.IsAvailable;
            FormatBox.Items.Add(format.Id.ToUpperInvariant());
            """;

        Assert.Empty(FormatDecisionsIn(honest));
    }

    /// <summary>
    /// Every format decision in one file.
    /// </summary>
    /// <remarks>
    /// Comments are stripped and string literals are not: a format named in prose is a note, and a
    /// format named in a literal is very often a comparison. The false-positive risk that buys is a
    /// literal used for something innocent, which is a conversation worth having anyway.
    /// </remarks>
    private static List<(string What, int Line, string Text)> FormatDecisionsIn(string source)
    {
        var body = StripComments(source);
        var found = new List<(string, int, string)>();

        foreach (var (what, pattern) in FormatDecisions)
        {
            foreach (Match m in pattern.Matches(body))
            {
                found.Add((what, body.AsSpan(0, m.Index).Count('\n') + 1, m.Value));
            }
        }

        return found;
    }

    /// <summary>Comments out, literals kept — the opposite half of <see cref="StripNoise"/>.</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"<!--.*?-->", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        // Not preceded by a colon, so the scheme in an xmlns URI is not read as the start of a comment
        // and the rest of that line survives to be scanned.
        return Regex.Replace(source, @"(?<!:)//[^\n]*", " ");
    }

    private static IEnumerable<string> Markup(string folder) =>
        Directory.EnumerateFiles(folder, "*.xaml", SearchOption.AllDirectories)
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static f => f, StringComparer.Ordinal);

    /// <summary>
    /// ARC-057: the extraction the window starts propagates the archive's Mark of the Web.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source rule rather than a behavioural one, and only because there is nowhere else to put it.
    /// <c>EngineWorkspace</c> lives in <c>NOBA.Archive.App</c>, the one project that cannot be compiled
    /// or referenced from a test on the build machine, so the property it must set cannot be asserted by
    /// running anything. What can be asserted is that the assignment is present in the file.
    /// </para>
    /// <para>
    /// It is worth a rule because the failure was invisible in every other direction.
    /// <c>PropagateMarkOfTheWeb</c> defaults to true, so the option said the files were protected;
    /// propagation also needs <c>SourceArchivePath</c>, and a null one turns it off silently. Every
    /// extraction started from the window therefore produced files with no Zone.Identifier - no warning
    /// from SmartScreen, no Protected View in Office - while the tests that cover propagation passed,
    /// because they set the path themselves. That is THREAT_MODEL.md T7 reaching the only surface an
    /// ordinary user touches.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_window_extraction_propagates_the_mark_of_the_web()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var workspace = Path.Combine(root!, "src", "NOBA.Archive.App", "Services", "EngineWorkspace.cs");
        Assert.True(File.Exists(workspace), $"{workspace} is missing; this rule is scanning nothing");

        var source = StripComments(File.ReadAllText(workspace));

        var options = Regex.Match(
            source,
            @"new\s+ExtractionOptions\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        Assert.True(options.Success, "no ExtractionOptions literal was found in EngineWorkspace");

        var body = options.Groups["body"].Value;
        Assert.True(
            Regex.IsMatch(body, @"\bSourceArchivePath\s*=\s*\w"),
            "the window's extraction does not set SourceArchivePath, so Mark of the Web is silently "
            + "not propagated (THREAT_MODEL.md T7):\n" + body.Trim());

        Assert.True(
            Regex.IsMatch(body, @"\bConflictPolicy\s*=\s*\w"),
            "the window's extraction does not pass a conflict policy, so the engine default decides "
            + "what happens to a file that is already there:\n" + body.Trim());
    }

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static f => !f.EndsWith(".g.cs", StringComparison.Ordinal))
            .OrderBy(static f => f, StringComparer.Ordinal);

    /// <summary>Type name -> the namespaces this repository declares it in.</summary>
    private static Dictionary<string, SortedSet<string>> DeclaredTypes(string root)
    {
        var owned = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var declaration = new Regex(
            @"\b(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*"
            + @"(?:class|record|struct|interface|enum|delegate)\s+(\w+)",
            RegexOptions.Compiled);

        foreach (var file in Sources(Path.Combine(root, "src")))
        {
            var source = StripNoise(File.ReadAllText(file));
            var ns = Regex.Match(source, @"\bnamespace\s+([\w.]+)");
            if (!ns.Success)
            {
                continue;
            }

            foreach (Match m in declaration.Matches(source))
            {
                if (!owned.TryGetValue(m.Groups[1].Value, out var namespaces))
                {
                    namespaces = new SortedSet<string>(StringComparer.Ordinal);
                    owned[m.Groups[1].Value] = namespaces;
                }

                namespaces.Add(ns.Groups[1].Value);
            }
        }

        return owned;
    }

    private static List<(string Type, int Line)> UnresolvedTypes(
        string source, Dictionary<string, SortedSet<string>> owned)
    {
        var clean = StripNoise(source);
        var visible = VisibleNamespaces(clean);
        var shadowed = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(clean, @"^\s*(?:global\s+)?using\s+(\w+)\s*=", RegexOptions.Multiline))
        {
            shadowed.Add(m.Groups[1].Value);
        }

        var body = Regex.Replace(clean, @"^\s*(?:global\s+)?using\s+[^\n]*;", string.Empty, RegexOptions.Multiline);
        var found = new List<(string, int)>();

        foreach (var (name, namespaces) in owned.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (shadowed.Contains(name) || namespaces.Overlaps(visible))
            {
                continue;
            }

            var line = FirstTypePosition(body, name);
            if (line > 0)
            {
                found.Add((name, line));
            }
        }

        return found;
    }

    /// <summary>
    /// The line where <paramref name="name"/> is used as a type, or 0 if it never is.
    /// </summary>
    /// <remarks>
    /// <c>block.FlowDirection = ...</c> names a property; <c>OperationState.Running</c> names a type.
    /// Only the second needs an import.
    /// </remarks>
    private static int FirstTypePosition(string body, string name)
    {
        var n = Regex.Escape(name);
        string[] positions =
        [
            $@"(?<![\w.]){n}\s*\.\s*[A-Z]\w*",   // static member access
            $@"\bnew\s+{n}\b",                    // construction
            $@"\b(?:is|as)\s+{n}\b",              // pattern or conversion
            $@"[<,]\s*{n}\s*[>,]",                // generic argument
            $@"\(\s*{n}\s*\)\s*\w",               // cast
            $@"(?<![\w.]){n}\??\s+\w+\s*[=;,)]",  // declaration or parameter
            $@":\s*{n}\b",                        // base type or constraint
        ];

        var earliest = int.MaxValue;
        foreach (var pattern in positions)
        {
            var m = Regex.Match(body, pattern);
            if (m.Success)
            {
                earliest = Math.Min(earliest, m.Index);
            }
        }

        return earliest == int.MaxValue ? 0 : body.AsSpan(0, earliest).Count('\n') + 1;
    }

    /// <summary>
    /// Namespaces a file can resolve simple type names from.
    /// </summary>
    /// <remarks>
    /// Its usings, plus its own namespace and every namespace enclosing it. A file in
    /// <c>NOBA.Archive.App</c> sees <c>NOBA.Archive</c> and <c>NOBA</c>, and never a child namespace
    /// such as <c>NOBA.Archive.Core.Operations</c>. That asymmetry is the entire defect this catches.
    /// </remarks>
    private static HashSet<string> VisibleNamespaces(string source)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(
                     source, @"^\s*(?:global\s+)?using\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Multiline))
        {
            visible.Add(m.Groups[1].Value);
        }

        var own = Regex.Match(source, @"\bnamespace\s+([\w.]+)");
        if (own.Success)
        {
            var parts = own.Groups[1].Value.Split('.');
            for (var i = 1; i <= parts.Length; i++)
            {
                visible.Add(string.Join('.', parts[..i]));
            }
        }

        return visible;
    }

    /// <summary>
    /// ARC-058: the drop path moves paths, and does not touch the disk to do it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dropping a folder must cost the same whether it holds three files or thirty thousand. The way
    /// that stops being true is a well-meant line in the drop handler that expands a folder "so the
    /// list shows what will be archived" — which runs on the UI thread, against whatever the folder
    /// happens to be, including a disconnected network share.
    /// </para>
    /// <para>
    /// The engine already walks directories, on its own thread, under ResourceGuard. So the rule is
    /// simply that the WinUI project never does: no enumeration, no copying, no reading. A path is a
    /// string until the operation starts.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_WinUI_project_does_not_walk_or_copy_the_filesystem()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var app = Path.Combine(root!, "src", "NOBA.Archive.App");
        var files = Sources(app).ToList();
        Assert.True(files.Count > 5, $"only {files.Count} app sources were scanned; the scan is broken");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            foreach (var (call, line) in FilesystemWalksIn(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}:{line} calls {call}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "the UI is reading the filesystem; the engine does that:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>ARC-059: the walk rule fires on a real enumeration and ignores a path string.</summary>
    [Fact]
    public void The_walk_rule_catches_an_enumeration_and_allows_a_path()
    {
        const string walks = """
            internal sealed class Sample
            {
                public void Add(string folder) => Model.AddInputs(Directory.EnumerateFiles(folder));
            }
            """;

        const string clean = """
            internal sealed class Sample
            {
                public void Add(string folder) => Model.AddInputs([folder]);
            }
            """;

        Assert.NotEmpty(FilesystemWalksIn(walks));
        Assert.Empty(FilesystemWalksIn(clean));
    }

    /// <summary>
    /// ARC-060: the shell action model owns no engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this exists to prevent is the one every file utility eventually ships: a context
    /// menu that "just" compresses, with its own call into a provider because going through the queue
    /// seemed like ceremony for a one-click action. What it actually loses is ResourceGuard, the path
    /// policy, the conflict contract, pause and cancel, and any record that the work happened.
    /// </para>
    /// <para>
    /// <c>Core</c> has no project references, so this is structurally true today. Naming it makes it
    /// stay true: the way it would break is somebody adding the reference and a call in the same
    /// commit, and a compile error is a weaker argument than a test that says why.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_shell_action_model_names_no_engine_type()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var actions = Path.Combine(root!, "src", "NOBA.Archive.Core", "Actions");
        var files = Sources(actions).ToList();
        Assert.True(files.Count >= 4, $"only {files.Count} action sources were scanned; the scan is broken");

        string[] engineTypes =
        [
            "CreateArchiveHandler", "ExtractArchiveHandler", "TestArchiveHandler",
            "IArchiveProvider", "IArchiveSession", "IArchiveBuilder",
            "OperationQueue", "IOperationQueue", "IArchiveWorkspace", "ResourceGuard",
        ];

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = StripNoise(File.ReadAllText(file));
            foreach (var type in engineTypes)
            {
                if (text.Contains(type, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} names {type}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "the shell layer is reaching for the engine; it must produce a request instead:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// ARC-061: building a context menu never reads the filesystem.
    /// </summary>
    /// <remarks>
    /// This code runs while a right-click is held open. A single enumeration of the selected folder
    /// turns a menu into a hang, and on a disconnected network share into a hang with a timeout on the
    /// end of it — in Explorer's process, not ours.
    /// </remarks>
    [Fact]
    public void The_shell_action_model_does_not_walk_the_filesystem()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var actions = Path.Combine(root!, "src", "NOBA.Archive.Core", "Actions");
        var offenders = new List<string>();

        foreach (var file in Sources(actions))
        {
            foreach (var (call, line) in FilesystemWalksIn(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}:{line} calls {call}");
            }

            // Existence probes are fine in a screen and wrong here: the menu is drawn before the user
            // has chosen anything, and one stat per selected item is one too many.
            var text = StripNoise(File.ReadAllText(file));
            foreach (var probe in new[] { "Directory.Exists", "File.Exists", "Path.Exists", "FileInfo", "DirectoryInfo" })
            {
                if (text.Contains(probe, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} calls {probe} during menu construction");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "menu construction is touching the disk:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Filesystem calls no screen has a reason to make.
    /// </summary>
    /// <remarks>
    /// <c>Path.Exists</c> and the <c>Path</c> helpers are absent from this list on purpose: asking
    /// whether a dropped item is still there is a question about one path, answered in constant time,
    /// and refusing it would push that check somewhere less visible.
    /// </remarks>
    private static List<(string Call, int Line)> FilesystemWalksIn(string source)
    {
        string[] banned =
        [
            "Directory.GetFiles", "Directory.EnumerateFiles",
            "Directory.GetDirectories", "Directory.EnumerateDirectories",
            "Directory.GetFileSystemEntries", "Directory.EnumerateFileSystemEntries",
            "File.Copy", "File.Move", "File.ReadAllBytes", "File.ReadAllText",
            "File.WriteAllBytes", "File.WriteAllText", "File.OpenRead",
        ];

        var found = new List<(string Call, int Line)>();
        var lines = StripNoise(source).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var call in banned)
            {
                if (lines[i].Contains(call, StringComparison.Ordinal))
                {
                    found.Add((call, i + 1));
                }
            }
        }

        return found;
    }

    /// <summary>Comments and string literals out, so a type named in prose is not read as a reference.</summary>
    private static string StripNoise(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", " ");
        source = Regex.Replace(source, @"@""(?:[^""]|"""")*""", @"""""");
        source = Regex.Replace(source, @"""(?:\\.|[^""\\])*""", @"""""");
        return source;
    }

    /// <summary>Walks up from the test binary to the folder holding the solution.</summary>
    private static string? RepositoryRoot()
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

        return null;
    }
}
