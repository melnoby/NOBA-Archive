using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// ARCHITECTURE.md §3.1, enforced rather than described. A violation fails the build.
/// </summary>
public sealed class DependencyDirectionTests
{
    private const string Core = "NOBA.Archive.Core";
    private const string Security = "NOBA.Archive.Security";
    private const string ApplicationLayer = "NOBA.Archive.Application";
    private const string Infrastructure = "NOBA.Archive.Infrastructure";
    private const string FormatsLayer = "NOBA.Archive.Formats";
    private const string WindowsLayer = "NOBA.Archive.Windows";
    private const string SevenZipBackend = "NOBA.Archive.Backends.SevenZip";
    private const string Engine = "NOBA.Archive.Engine";
    private const string Cli = "NOBA.Archive.Cli";
    private const string Presentation = "NOBA.Archive.Presentation";
    private const string ShellBridge = "NOBA.Archive.ShellBridge";

    /// <summary>Assembly -> the NOBA assemblies it is allowed to reference.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [Core] = [],
        [Security] = [Core],
        [WindowsLayer] = [Core],
        [ApplicationLayer] = [Core, Security],
        [Infrastructure] = [Core, Security, WindowsLayer],
        [FormatsLayer] = [Core, Security],
        // Core alone, and deliberately so. The native backend talks to 7z.dll through raw function
        // pointers; if it could reference Infrastructure it could open a file, and ADR-0005 would
        // become a convention instead of a structural property. This line is the enforcement.
        [SevenZipBackend] = [Core],
        // The engine is the composition root, and the one place allowed to reference a platform
        // assembly: it resolves IExtractionPlatform itself so an embedding host cannot silently
        // downgrade handle verification or Mark of the Web by forgetting to register them. Nothing
        // below it may reference NOBA.Archive.Windows, which is what keeps the direction meaningful.
        [Engine] = [Core, Security, ApplicationLayer, Infrastructure, FormatsLayer, WindowsLayer, SevenZipBackend],
        [Cli] = [Core, Security, ApplicationLayer, Infrastructure, FormatsLayer, WindowsLayer, SevenZipBackend, Engine],

        // Core and Application, and nothing else. The presentation layer may ask the engine to do
        // things and may read what it is capable of; it may not reach Security, Infrastructure or a
        // backend, because from there it could validate a path, write a file, or decide a policy - and
        // every one of those decisions belongs somewhere a screen cannot reach.
        [Presentation] = [Core, ApplicationLayer],

        // A composition root, like the CLI, plus the string table. The modern shell surface has to
        // resolve labels at draw time (plan section E.1), and the labels live in the presentation
        // layer - so this is the one host besides the app that may reach it. It may NOT be referenced
        // by anything: it is an edge, and something depending on it would be depending on a shell.
        [ShellBridge] =
            [Core, Security, ApplicationLayer, Infrastructure, FormatsLayer, WindowsLayer, SevenZipBackend, Engine, Presentation],
    };

    [Theory]
    [InlineData(Core)]
    [InlineData(Security)]
    [InlineData(WindowsLayer)]
    [InlineData(ApplicationLayer)]
    [InlineData(Infrastructure)]
    [InlineData(FormatsLayer)]
    [InlineData(SevenZipBackend)]
    [InlineData(Engine)]
    [InlineData(Cli)]
    [InlineData(Presentation)]
    [InlineData(ShellBridge)]
    public void Only_references_the_layers_it_is_allowed_to(string assemblyName)
    {
        var probe = AssemblyProbe.Load(assemblyName);
        var allowed = Allowed[assemblyName];

        var violations = probe.AssemblyReferences
            .Where(static r => r.StartsWith("NOBA.Archive", StringComparison.Ordinal))
            .Where(r => !allowed.Contains(r, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} references {string.Join(", ", violations)}, which the layering forbids.");
    }

    /// <summary>
    /// The other half of <c>ShellBridge</c>'s own doc comment above: "it may NOT be referenced by
    /// anything". <see cref="Only_references_the_layers_it_is_allowed_to"/> only ever checks what
    /// <c>ShellBridge</c> itself references out, never whether something else references it in -
    /// true today by inspection (nothing in <c>src/</c> references it except its own test project),
    /// not by this test, until now. Found during DROP 13's audit as an unenforced invariant, not a
    /// live violation.
    /// </summary>
    [Fact]
    public void Nothing_references_shell_bridge_back()
    {
        var violations = Allowed.Keys
            .Where(name => !string.Equals(name, ShellBridge, StringComparison.Ordinal))
            .Where(name => AssemblyProbe.Load(name).AssemblyReferences.Contains(ShellBridge, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{string.Join(", ", violations)} reference {ShellBridge}, which may be referenced by nothing.");
    }

    /// <summary>
    /// Every product project is covered by the table above.
    /// </summary>
    /// <remarks>
    /// <b>The rule that a rule must cover everything.</b> <c>Allowed</c> is a hand-maintained
    /// dictionary and the theory is a hand-maintained list of <c>InlineData</c>; a project added
    /// without touching either is simply unpoliced, and nothing goes red. That is not hypothetical -
    /// <c>NOBA.Archive.ShellBridge</c> was added in Phase 5 and would have escaped the layering
    /// entirely, in the one assembly that loads into a surrogate process next to Explorer.
    /// </remarks>
    [Fact]
    public void Every_product_project_appears_in_the_layering_table()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NOBA.Archive.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);

        var projects = Directory
            .EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static n => n is not null)
            .Select(static n => n!)
            // The WinUI app is not buildable on every machine that runs these tests, so it is not in
            // the table and says so here rather than being silently absent.
            .Where(static n => !string.Equals(n, "NOBA.Archive.App", StringComparison.Ordinal))
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        var missing = projects.Where(p => !Allowed.ContainsKey(p)).ToList();

        Assert.True(
            missing.Count == 0,
            "These projects exist in src/ but no layering rule covers them: " +
            string.Join(", ", missing) +
            ". Add them to Allowed and to the theory's InlineData.");
    }

    /// <summary>Core is the leaf: BCL only, no third-party assembly, no NOBA assembly.</summary>
    [Fact]
    public void Core_depends_on_nothing_but_the_bcl()
    {
        var probe = AssemblyProbe.Load(Core);

        var foreign = probe.AssemblyReferences
            .Where(static r => !r.StartsWith("System", StringComparison.Ordinal) &&
                               !string.Equals(r, "netstandard", StringComparison.Ordinal) &&
                               !string.Equals(r, "mscorlib", StringComparison.Ordinal))
            .ToList();

        Assert.True(foreign.Count == 0, $"Core must depend on the BCL only, but references {string.Join(", ", foreign)}.");
    }

    /// <summary>
    /// The Application layer states its needs as interfaces and lets the composition root supply them;
    /// referencing Infrastructure directly would make the use cases untestable and un-embeddable.
    /// </summary>
    [Fact]
    public void Application_does_not_reference_infrastructure()
    {
        var probe = AssemblyProbe.Load(ApplicationLayer);

        Assert.DoesNotContain(Infrastructure, string.Join(",", probe.AssemblyReferences));
    }

    /// <summary>
    /// ADR-0002 and ARCHITECTURE.md §3.1: an archive library appears in exactly one project. No such
    /// library is referenced yet, so today this test guards Phase 2 rather than reporting on Phase 1.
    /// </summary>
    [Theory]
    [InlineData(Core)]
    [InlineData(Security)]
    [InlineData(ApplicationLayer)]
    [InlineData(Infrastructure)]
    [InlineData(Engine)]
    [InlineData(Cli)]
    [InlineData(WindowsLayer)]
    public void No_archive_library_outside_the_formats_project(string assemblyName)
    {
        string[] archiveLibraries = ["SharpCompress", "SharpSevenZip", "SevenZipSharp", "System.IO.Compression"];
        var probe = AssemblyProbe.Load(assemblyName);

        var violations = probe.AssemblyReferences
            .Where(r => archiveLibraries.Contains(r, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} references the archive library/libraries {string.Join(", ", violations)}. " +
            "Only NOBA.Archive.Formats may.");
    }
}
