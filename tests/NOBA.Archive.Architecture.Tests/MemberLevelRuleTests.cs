using NOBA.Archive.Core.Abstractions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// Rules that live in method bodies rather than in the reference list, so they need metadata
/// inspection rather than <c>Assembly.GetReferencedAssemblies()</c> (ARCHITECTURE.md §3.1).
/// </summary>
public sealed class MemberLevelRuleTests
{
    private static readonly string[] EngineAssemblies =
    [
        "NOBA.Archive.Core",
        "NOBA.Archive.Security",
        "NOBA.Archive.Application",
        "NOBA.Archive.Formats",
        "NOBA.Archive.Engine",
    ];

    /// <summary>
    /// <c>System.IO.File</c> and <c>Directory</c> belong to Infrastructure alone, so error translation,
    /// cancellation, and path security have exactly one home (ARCHITECTURE.md §8.1).
    /// </summary>
    [Theory]
    [InlineData("NOBA.Archive.Core")]
    [InlineData("NOBA.Archive.Security")]
    [InlineData("NOBA.Archive.Application")]
    [InlineData("NOBA.Archive.Formats")]
    [InlineData("NOBA.Archive.Engine")]
    [InlineData("NOBA.Archive.Windows")]
    public void Does_not_touch_the_filesystem_directly(string assemblyName)
    {
        string[] banned = ["System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo"];
        var probe = AssemblyProbe.Load(assemblyName);

        var violations = probe.TypeReferences.Where(t => banned.Contains(t, StringComparer.Ordinal)).Distinct().ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} uses {string.Join(", ", violations)}. Only NOBA.Archive.Infrastructure may.");
    }

    /// <summary>The engine is embeddable: writing to the console is the host's business, not its own.</summary>
    [Theory]
    [InlineData("NOBA.Archive.Core")]
    [InlineData("NOBA.Archive.Security")]
    [InlineData("NOBA.Archive.Application")]
    [InlineData("NOBA.Archive.Infrastructure")]
    [InlineData("NOBA.Archive.Formats")]
    [InlineData("NOBA.Archive.Engine")]
    [InlineData("NOBA.Archive.Windows")]
    public void Does_not_write_to_the_console(string assemblyName)
    {
        var probe = AssemblyProbe.Load(assemblyName);

        Assert.False(
            probe.TypeReferences.Contains("System.Console", StringComparer.Ordinal),
            $"{assemblyName} uses System.Console. Only the CLI host may.");
    }

    /// <summary>
    /// Blocking on a task is how a UI freezes and how a deadlock reaches production.
    /// </summary>
    /// <remarks>
    /// A generic instantiation such as <c>Task&lt;int&gt;.Result</c> is recorded as a TypeSpecification
    /// and is not visible to this check; the non-generic <c>Task.Wait</c>, <c>Task.Result</c> and the
    /// awaiter forms are. The gap is documented rather than hidden.
    /// </remarks>
    [Theory]
    [InlineData("NOBA.Archive.Core")]
    [InlineData("NOBA.Archive.Security")]
    [InlineData("NOBA.Archive.Application")]
    [InlineData("NOBA.Archive.Infrastructure")]
    [InlineData("NOBA.Archive.Formats")]
    [InlineData("NOBA.Archive.Engine")]
    [InlineData("NOBA.Archive.Cli")]
    public void Does_not_block_on_a_task(string assemblyName)
    {
        string[] blockingParents =
        [
            "Task", "Task`1", "ValueTask", "ValueTask`1",
            "TaskAwaiter", "TaskAwaiter`1", "ConfiguredTaskAwaitable", "ConfiguredTaskAwaitable`1",
        ];
        string[] blockingMembers = ["get_Result", "GetResult", "Wait", "WaitAll", "WaitAny"];

        var probe = AssemblyProbe.Load(assemblyName);

        var violations = probe.MemberReferences
            .Where(m => blockingParents.Contains(m.TypeName, StringComparer.Ordinal))
            .Where(m => blockingMembers.Contains(m.MemberName, StringComparer.Ordinal))
            .Select(static m => $"{m.FullTypeName}.{m.MemberName}")
            .Distinct()
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} blocks on a task via {string.Join(", ", violations)}.");
    }

    /// <summary>
    /// ADR-0009: the CLI is a host, not a second engine. If a type in it ever implements one of these,
    /// the "exactly one archive engine" guarantee has quietly stopped being true.
    /// </summary>
    [Fact]
    public void The_cli_implements_no_engine_interface()
    {
        Type[] engineInterfaces =
        [
            typeof(IArchiveProvider),
            typeof(IArchiveSession),
            typeof(IArchiveBuilder),
            typeof(IExtractionWriter),
            typeof(IPathPolicy),
            typeof(IConflictResolutionService),
            typeof(IResourceGuard),
            typeof(IFileSystem),
        ];

        var cli = typeof(NOBA.Archive.Cli.Commands.CliApplication).Assembly;

        var violations = cli.GetTypes()
            .Where(static t => t.IsClass && !t.IsAbstract)
            .Where(t => engineInterfaces.Any(i => i.IsAssignableFrom(t)))
            .Select(static t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"The CLI implements engine interfaces in {string.Join(", ", violations)}. " +
            "It must host the engine, not reimplement it.");
    }

    /// <summary>
    /// ADR-0009: <c>PATH</c> is an optional installer convenience, never a discovery mechanism. If the
    /// engine or the CLI ever read an environment variable, integration would quietly start depending on
    /// machine state a deployment may not have.
    /// </summary>
    [Theory]
    [InlineData("NOBA.Archive.Core")]
    [InlineData("NOBA.Archive.Security")]
    [InlineData("NOBA.Archive.Application")]
    [InlineData("NOBA.Archive.Infrastructure")]
    [InlineData("NOBA.Archive.Formats")]
    [InlineData("NOBA.Archive.Engine")]
    [InlineData("NOBA.Archive.Cli")]
    public void Does_not_read_environment_variables(string assemblyName)
    {
        string[] banned = ["GetEnvironmentVariable", "GetEnvironmentVariables", "ExpandEnvironmentVariables"];
        var probe = AssemblyProbe.Load(assemblyName);

        var violations = probe.MemberReferences
            .Where(static m => m.FullTypeName is "System.Environment")
            .Where(m => banned.Contains(m.MemberName, StringComparer.Ordinal))
            .Select(static m => m.MemberName)
            .Distinct()
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} reads the environment via {string.Join(", ", violations)}. " +
            "Installation discovery goes through InstallationContract, not PATH or any other variable.");
    }

    /// <summary>The discovery contract must stay a single definition both the installer and hosts share.</summary>
    [Fact]
    public void The_installation_contract_never_mentions_path()
    {
        var order = string.Join(" ", NOBA.Archive.Engine.InstallationContract.DocumentedResolutionOrder);

        Assert.Contains("InstallPath", order);
        Assert.Contains("CliPath", order);
        Assert.Contains("never fall back to a PATH lookup", order);
    }

    /// <summary>
    /// THREAT_MODEL.md §6's acceptance gate names "nothing outside the extraction writer writes to
    /// the destination" as a checklist item that nothing in the repository actually enforced — it was
    /// true by inspection, never by a test that would fail the build the day it stopped being true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrower than <see cref="Does_not_touch_the_filesystem_directly"/> above: that rule bans
    /// <em>any</em> reference to <c>File</c>/<c>Directory</c> outright, which is why it cannot cover
    /// the CLI or the native backend — both legitimately call read-only members (<c>File.Exists</c>,
    /// <c>File.ReadAllText</c>) and would fail that rule for reasons that have nothing to do with
    /// writing. This rule instead names the mutating members themselves, so it can police every
    /// assembly except <c>NOBA.Archive.Infrastructure</c> — including the two the blanket rule has to
    /// leave out — without forbidding the reads those two already do.
    /// </para>
    /// <para>
    /// <c>FileStream</c>'s own write members (<c>Write</c>, <c>WriteByte</c>, <c>SetLength</c>) are not
    /// named here: a constructed <c>FileStream</c> only ever reaches another assembly as a <c>Stream</c>
    /// returned from <see cref="IFileSystem.OpenRead"/> or <see cref="IFileSystem.CreateNew"/>, and
    /// <c>Stream.Write</c>/<c>WriteAsync</c> are inherited members no metadata scan can attribute to a
    /// specific concrete type — banning them would also ban writing to an in-memory buffer. What this
    /// rule can see, and does ban, is every member that names its target by a path string rather than
    /// a <c>Stream</c> the caller already holds, which is exactly the shape a write that bypassed
    /// <see cref="IFileSystem"/> would take.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("NOBA.Archive.Core")]
    [InlineData("NOBA.Archive.Security")]
    [InlineData("NOBA.Archive.Application")]
    [InlineData("NOBA.Archive.Formats")]
    [InlineData("NOBA.Archive.Engine")]
    [InlineData("NOBA.Archive.Windows")]
    [InlineData("NOBA.Archive.Cli")]
    [InlineData("NOBA.Archive.Backends.SevenZip")]
    public void Nothing_outside_infrastructure_writes_to_the_filesystem_by_path(string assemblyName)
    {
        string[] mutatingTypes = ["File", "Directory", "FileInfo", "DirectoryInfo"];
        string[] mutatingMembers =
        [
            "Create", "CreateText", "CreateDirectory", "CreateSubdirectory",
            "OpenWrite", "AppendText",
            "WriteAllText", "WriteAllLines", "WriteAllBytes", "WriteAllTextAsync", "WriteAllLinesAsync", "WriteAllBytesAsync",
            "AppendAllText", "AppendAllLines", "AppendAllTextAsync", "AppendAllLinesAsync",
            "Copy", "CopyTo", "Move", "MoveTo", "Replace",
            "Delete",
            "SetAttributes", "SetCreationTime", "SetCreationTimeUtc",
            "SetLastWriteTime", "SetLastWriteTimeUtc", "SetLastAccessTime", "SetLastAccessTimeUtc",
            "Encrypt", "Decrypt",
        ];

        var probe = AssemblyProbe.Load(assemblyName);

        var violations = probe.MemberReferences
            .Where(m => mutatingTypes.Contains(m.TypeName, StringComparer.Ordinal))
            .Where(m => m.TypeNamespace is "System.IO")
            .Where(m => mutatingMembers.Contains(m.MemberName, StringComparer.Ordinal))
            .Select(static m => $"{m.FullTypeName}.{m.MemberName}")
            .Distinct()
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} writes to the filesystem by path via {string.Join(", ", violations)}. " +
            "Only NOBA.Archive.Infrastructure may; route writes through IFileSystem instead.");
    }

    /// <summary>Every engine assembly must be usable from a host with no UI at all.</summary>
    [Fact]
    public void No_engine_assembly_depends_on_a_ui_framework()
    {
        string[] uiAssemblies = ["Microsoft.WindowsAppSDK", "Microsoft.UI.Xaml", "WindowsBase", "PresentationFramework"];

        foreach (var name in EngineAssemblies)
        {
            var probe = AssemblyProbe.Load(name);
            var violations = probe.AssemblyReferences.Where(r => uiAssemblies.Contains(r, StringComparer.Ordinal)).ToList();

            Assert.True(violations.Count == 0, $"{name} references the UI assembly/assemblies {string.Join(", ", violations)}.");
        }
    }
}
