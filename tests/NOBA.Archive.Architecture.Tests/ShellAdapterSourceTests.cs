using System.Text.RegularExpressions;
using NOBA.Archive.Core.Actions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// SHELL-063 and SHELL-065, enforced over the native adapter's source.
/// </summary>
/// <remarks>
/// <para>
/// <b>The adapter may not know what an archive is.</b> Plan section B.2: it converts an
/// <c>IShellItemArray</c> to paths, marshals JSON, implements the COM interfaces and starts a process
/// from a name and an argument array. It may not decide whether an action is available, know a format
/// name or capability, build a command line, or compose an archive path. Its whole vocabulary is
/// <c>entryId</c>, <c>title</c>, <c>state</c>.
/// </para>
/// <para>
/// That is a rule about C++, and nothing in a C++ build enforces it - so it is enforced here, over
/// the source text, where the managed test suite can see it.
/// </para>
/// <para>
/// <b>Word boundaries, and the reason they are not optional.</b> The first version of this scan
/// matched substrings and reported three violations: <c>rar</c> inside <c>LoadLibraryExW</c>,
/// <c>tar</c> inside <c>STARTUPINFOW</c>, <c>rar</c> inside <c>Library</c>. A rule that fires on
/// <c>LoadLibraryExW</c> gets switched off within a week, and a rule that is switched off enforces
/// nothing. Both halves are controlled below: a genuine violation must be caught, and the Win32
/// spellings must not be.
/// </para>
/// </remarks>
public sealed class ShellAdapterSourceTests
{
    private const string AdapterDirectory = "NOBA.Archive.ShellAdapter";

    /// <summary>
    /// Format names, capability names, and every <c>ArchiveAction</c>. The action names are listed
    /// explicitly rather than reflected so that adding an action makes this list stale in a review
    /// rather than silently widening what the adapter may say.
    /// </summary>
    private static readonly string[] Forbidden =
    [
        "7z", "zip", "rar", "gzip", "tar", "bzip2", "xz",
        "Compress", "Extract", "CanCreate", "CanOpen", "CanTest",
        "OpenInApp", "TestArchive", "ExtractHere", "ExtractTo", "CompressTo", "CreateArchive",
    ];

    private static Regex TokenRule(string token) =>
        new(@"\b" + Regex.Escape(token) + @"\b", RegexOptions.IgnoreCase);

    [Fact]
    public void The_adapter_never_names_a_format_an_action_or_a_capability()
    {
        var offenders = new List<string>();

        foreach (var file in AdapterSources())
        {
            var code = StripComments(File.ReadAllText(file));
            var hits = Forbidden.Where(t => TokenRule(t).IsMatch(code)).ToList();
            if (hits.Count > 0)
            {
                offenders.Add($"{Path.GetFileName(file)}: {string.Join(", ", hits)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "SHELL-063: the native adapter must not know a format, an action or a capability. " +
            string.Join(" | ", offenders));
    }

    /// <summary>The control. A rule never seen to fire is not a rule.</summary>
    [Fact]
    public void The_rule_catches_an_adapter_that_learned_a_format()
    {
        const string Offending = """
            if (entryId == L"noba.compress.zip") {
                return ECS_ENABLED;
            }
            """;

        var hits = Forbidden.Where(t => TokenRule(t).IsMatch(StripComments(Offending))).ToList();

        Assert.Contains(hits, h => string.Equals(h, "zip", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the control, and the one that keeps the rule alive: the Win32 spellings the
    /// adapter legitimately uses must not trip it.
    /// </summary>
    [Fact]
    public void The_rule_does_not_fire_on_the_windows_api_the_adapter_must_use()
    {
        const string Legitimate = """
            HMODULE m = LoadLibraryExW(L"hostfxr.dll", nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            STARTUPINFOW startup = {};
            std::wstring full = Library() + Startup();
            """;

        var hits = Forbidden.Where(t => TokenRule(t).IsMatch(StripComments(Legitimate))).ToList();

        Assert.True(hits.Count == 0, $"the rule fires on legitimate Win32: {string.Join(", ", hits)}");
    }

    /// <summary>
    /// A comment may say what the file is for. The adapter's headers explain the boundary, and the
    /// boundary cannot be explained without naming what is on the other side of it.
    /// </summary>
    [Fact]
    public void The_rule_ignores_comments()
    {
        const string Commented = """
            // The bridge decides whether to offer a compress action; this never does.
            /* No format name - not zip, not 7z - may reach this file. */
            return state;
            """;

        var hits = Forbidden.Where(t => TokenRule(t).IsMatch(StripComments(Commented))).ToList();

        Assert.True(hits.Count == 0, $"the rule fired on a comment: {string.Join(", ", hits)}");
    }

    // ---------------------------------------------------------------------------------------------
    // SHELL-065: identity has one home
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The CLSID in <c>Guids.h</c> is a copy of <see cref="ShellRegistration.ModernHandlerClsid"/>,
    /// and is allowed to exist only because this test refuses to let the two drift.
    /// </summary>
    /// <remarks>
    /// A CLSID typed twice and mistyped once registers cleanly and never activates. The symptom is a
    /// menu that silently does not appear, on somebody else's machine, with nothing in any log.
    /// </remarks>
    [Fact]
    public void The_adapters_clsid_is_the_one_shell_registration_publishes()
    {
        var header = File.ReadAllText(Path.Combine(AdapterRoot(), "Guids.h"));

        var braced = Regex.Match(header, @"NOBA_MODERN_HANDLER_CLSID\s+L""(\{[0-9A-Fa-f-]{36}\})""");
        Assert.True(braced.Success, "Guids.h does not publish NOBA_MODERN_HANDLER_CLSID in braced form");
        Assert.Equal(ShellRegistration.ModernHandlerClsid, braced.Groups[1].Value);

        // And the DEFINE_GUID form has to agree with the text form, or the DLL registers under one
        // value and answers to another.
        var parts = Regex.Match(
            header,
            @"DEFINE_GUID\(CLSID_NobaExplorerCommand,\s*0x([0-9a-fA-F]{8}),\s*0x([0-9a-fA-F]{4}),\s*0x([0-9a-fA-F]{4}),\s*" +
            string.Join(@",\s*", Enumerable.Repeat(@"0x([0-9a-fA-F]{2})", 8)) + @"\)");
        Assert.True(parts.Success, "Guids.h does not contain a well-formed DEFINE_GUID");

        var rebuilt =
            "{" + parts.Groups[1].Value.ToUpperInvariant() +
            "-" + parts.Groups[2].Value.ToUpperInvariant() +
            "-" + parts.Groups[3].Value.ToUpperInvariant() +
            "-" + parts.Groups[4].Value.ToUpperInvariant() + parts.Groups[5].Value.ToUpperInvariant() +
            "-" + string.Concat(Enumerable.Range(6, 6).Select(i => parts.Groups[i].Value.ToUpperInvariant())) +
            "}";

        Assert.Equal(ShellRegistration.ModernHandlerClsid.ToUpperInvariant(), rebuilt);
    }

    /// <summary>
    /// The harness probe's CLSID must never become the product's. A machine that once ran the E2
    /// harness would otherwise be able to activate a stub that does nothing on invoke.
    /// </summary>
    [Fact]
    public void The_product_clsid_is_not_the_e2_harness_probe_clsid()
    {
        const string HarnessProbe = "{17019B66-1E78-459B-92AE-63A440E7C5EA}";

        Assert.NotEqual(HarnessProbe, ShellRegistration.ModernHandlerClsid.ToUpperInvariant());
    }

    /// <summary>
    /// The adapter registers through the package, never itself. A <c>DllRegisterServer</c> would give
    /// a machine two ways to acquire the handler and one way to remove it.
    /// </summary>
    [Fact]
    public void The_adapter_does_not_self_register()
    {
        foreach (var file in AdapterSources())
        {
            var code = StripComments(File.ReadAllText(file));
            Assert.False(
                code.Contains("DllRegisterServer", StringComparison.Ordinal) ||
                code.Contains("DllUnregisterServer", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} self-registers; ADR-0006 registers through the package.");
        }
    }

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static string AdapterRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", AdapterDirectory);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail($"src/{AdapterDirectory} not found above the test output directory");
        return string.Empty;
    }

    private static IEnumerable<string> AdapterSources() =>
        Directory
            .EnumerateFiles(AdapterRoot(), "*.*", SearchOption.AllDirectories)
            .Where(static p => p.EndsWith(".cpp", StringComparison.Ordinal) ||
                               p.EndsWith(".h", StringComparison.Ordinal))
            .OrderBy(static p => p, StringComparer.Ordinal);
}
