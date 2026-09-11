using System.Text.RegularExpressions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// SHELL-087: one authoritative capability derivation, consumed rather than reproduced.
/// </summary>
/// <remarks>
/// <para>
/// <b>The requirement was recorded before the code existed, at the G.1 review:</b> <i>there must be
/// ONE authoritative capability derivation, and the bridge must consume it rather than reproduce
/// it.</i> It was recorded because the G.1 harness bridge had quietly grown its own copy of the step
/// that turns the registry's supported formats into the writable ones - the same five lines already
/// present twice in <c>CliApplication</c>.
/// </para>
/// <para>
/// <b>A stated rule is not an enforced one.</b> <c>shell-register.ps1</c> drifted from
/// <c>ShellLauncher</c> and produced defects D2 and D3 while every document said the two agreed. The
/// Phase 5 shell adapter runs in a surrogate process nobody watches, so a fourth copy would surface
/// as a wrong menu on a user's machine rather than as a red test here. Hence a source rule.
/// </para>
/// <para>
/// <b>What this catches, precisely:</b> a <c>Where</c> clause over <c>CanCreate</c> anywhere in
/// <c>src/</c> except inside <see cref="NOBA.Archive.Core.Formats.CreatableFormats"/> itself. It
/// deliberately does <i>not</i> ban <c>CanCreate</c> - asking one report whether one format is
/// writable is the correct use, and <c>ShellMenuBuilder</c> and <c>ShellRegistration</c> both do it
/// on the list they are handed. Filtering a <i>collection</i> by it is the derivation, and that has
/// one home.
/// </para>
/// <para>
/// <c>harness/</c> is deliberately out of scope. It is not product code, it is expected to be
/// deleted, and a rule that fires on a scratch file teaches people to disable rules.
/// </para>
/// </remarks>
public sealed class CapabilityDerivationTests
{
    /// <summary>The one file allowed to derive the writable-format list.</summary>
    private const string TheOneHome = "CreatableFormats.cs";

    /// <summary>
    /// <c>.Where(... CanCreate)</c> - a collection being filtered by the capability, which is the
    /// derivation itself rather than a single question about a single format.
    /// </summary>
    /// <remarks>
    /// The first attempt was <c>\.Where\s*\([^)]*CanCreate</c>, and the controls below failed on the
    /// day it was written: <c>[^)]*</c> cannot cross the closing paren in <c>ReportFor(f)</c>, so it
    /// matched neither of the two shapes it was written to catch. A rule with no control would have
    /// shipped green and forbidden nothing. Bounded by <c>;</c> instead, so it stays inside one
    /// statement and cannot pair a <c>Where</c> with an unrelated <c>CanCreate</c> further down.
    /// </remarks>
    private static readonly Regex Derivation = new(
        @"\.Where\s*\([^;]{0,200}?CanCreate",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Only_one_source_file_derives_the_creatable_format_list()
    {
        var offenders = new List<string>();

        foreach (var file in ProductSources())
        {
            if (string.Equals(Path.GetFileName(file), TheOneHome, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (Derivation.IsMatch(StripComments(text)))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The writable-format list is derived in more than one place: " +
            string.Join(", ", offenders) +
            $". Call {nameof(NOBA.Archive.Core.Formats.CreatableFormats)}.From instead. " +
            "Recorded at the G.1 review: one authoritative capability derivation, consumed not reproduced.");
    }

    /// <summary>
    /// The negative control. A rule that has never been seen to fire is not a rule, and this one is
    /// a regex over source - exactly the kind that silently stops matching after a refactor.
    /// </summary>
    [Fact]
    public void The_rule_catches_a_reproduced_derivation()
    {
        const string Reproduced = """
            var creatable = registry.SupportedFormats
                .Where(f => ReportFor(f).CanCreate)
                .OrderBy(static f => f.Id, StringComparer.Ordinal)
                .ToList();
            """;

        Assert.True(Derivation.IsMatch(StripComments(Reproduced)), "the rule stopped matching a reproduced derivation");
    }

    /// <summary>
    /// The other half of the control: asking one report about one format must stay legal, or the
    /// rule would forbid the correct use and be deleted the first time it got in the way.
    /// </summary>
    [Fact]
    public void The_rule_permits_asking_one_report_about_one_format()
    {
        const string Correct = """
            var report = reportFor(format);
            if (!report.CanCreate)
            {
                continue;
            }
            """;

        Assert.False(Derivation.IsMatch(StripComments(Correct)), "the rule forbids the correct single-format use");
    }

    /// <summary>
    /// And the control that would have caught the G.1 bridge: the shape it actually had.
    /// </summary>
    [Fact]
    public void The_rule_catches_the_shape_the_G1_bridge_had()
    {
        const string BridgeShape = """
            _creatable = registry.SupportedFormats
                .Where(f => new FormatCapabilityReport(f, registry.CapabilitiesFor(f)).CanCreate)
                .OrderBy(static f => f.Id, StringComparer.Ordinal)
                .ToList();
            """;

        Assert.True(Derivation.IsMatch(StripComments(BridgeShape)), "the rule would not have caught the G.1 bridge");
    }

    /// <summary>
    /// Comments describe the rule; they must not trip it. Every document in this repository quotes
    /// the offending five lines, and so does <c>CreatableFormats</c>'s own summary.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", string.Empty, RegexOptions.Multiline);
        source = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static IEnumerable<string> ProductSources()
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var src = Path.Combine(root!, "src");
        Assert.True(Directory.Exists(src), $"{src} is missing");

        return Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(static p =>
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static p => p, StringComparer.Ordinal);
    }

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
