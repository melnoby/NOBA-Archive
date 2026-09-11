using System.Text.RegularExpressions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// The <c>\uXXXX</c> escape path in the native adapter's JSON reader, guarded from the managed side.
/// </summary>
/// <remarks>
/// <para>
/// <b>What went wrong.</b> <c>Json.cpp</c> read the four hex digits of a <c>\uXXXX</c> escape with
/// <c>std::sscanf(p + 2, "%4x", &amp;code)</c>. MSVC deprecates <c>sscanf</c> (C4996) and this project
/// treats warnings as errors, so the VM build stopped there. The warning was the smaller half:
/// <c>"%4x"</c> also accepts leading whitespace, a sign, an <c>0x</c> prefix and FEWER than four
/// digits, while the caller advanced the cursor by four characters regardless - so <c>\u9"</c>
/// consumed its own closing quote and desynchronised the parse.
/// </para>
/// <para>
/// <b>Why the real test is a C++ file and this one is a source scan.</b>
/// <c>tests/NOBA.Archive.ShellAdapter.Tests/json_hex_tests.cpp</c> executes
/// <c>noba::ParseHex4</c> over every case, and runs on any machine because <c>Json.h</c> is
/// header-only and touches no Windows API. What it cannot see is whether <c>Json.cpp</c> still uses
/// it, or whether a deprecated scan crept back into another file - and those are exactly the
/// regressions that would next be found by a build on a Windows VM rather than here. So the
/// behaviour is tested in C++, the wiring is tested here, and neither is asked to do the other's job.
/// </para>
/// </remarks>
public sealed class ShellAdapterJsonEscapeTests
{
    /// <summary>
    /// The scanning family MSVC deprecates, plus the unbounded copies. Word-bounded, because
    /// <c>Json.cpp</c> legitimately uses <c>snprintf</c> and a substring rule would fire on it - the
    /// same defect as a rule that matched <c>rar</c> inside <c>LoadLibraryExW</c>.
    /// </summary>
    private static readonly string[] Deprecated =
    [
        "scanf", "sscanf", "swscanf", "fscanf", "wscanf",
        "sscanf_s", "swscanf_s",
        "strcpy", "wcscpy", "strcat", "wcscat", "sprintf", "swprintf", "gets", "getws",
    ];

    private static Regex TokenRule(string token) => new(@"\b" + Regex.Escape(token) + @"\b");

    /// <summary>
    /// No adapter source calls a deprecated scan or copy. Suppressing C4996 is forbidden, so the
    /// only way this stays true is that no such call exists.
    /// </summary>
    [Fact]
    public void No_adapter_source_calls_a_deprecated_crt_function()
    {
        var offenders = new List<string>();

        foreach (var file in AdapterSources())
        {
            var code = StripComments(File.ReadAllText(file));
            var hits = Deprecated.Where(t => TokenRule(t).IsMatch(code)).ToList();
            if (hits.Count > 0)
            {
                offenders.Add($"{Path.GetFileName(file)}: {string.Join(", ", hits)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "C4996 is not suppressed in this project, so a deprecated call is a build failure on " +
            "Windows and nothing at all here. " + string.Join(" | ", offenders));
    }

    /// <summary>The control: the rule must catch the exact line that failed the VM build.</summary>
    [Fact]
    public void The_rule_catches_the_call_that_failed_the_vm_build()
    {
        const string Offending = """
            unsigned code = 0;
            if (std::sscanf(p + 2, "%4x", &code) != 1) {
                return nullptr;
            }
            """;

        var hits = Deprecated.Where(t => TokenRule(t).IsMatch(StripComments(Offending))).ToList();

        Assert.True(hits.Count > 0, "the rule does not catch std::sscanf");
    }

    /// <summary>
    /// The other half of the control. <c>AppendJsonString</c> escapes control characters with
    /// <c>std::snprintf</c>, which is bounded, is not deprecated, and must not trip the rule.
    /// </summary>
    [Fact]
    public void The_rule_does_not_fire_on_the_bounded_calls_the_adapter_uses()
    {
        const string Legitimate = """
            char buffer[7];
            std::snprintf(buffer, sizeof(buffer), "\\u%04x", static_cast<unsigned char>(c));
            """;

        var hits = Deprecated.Where(t => TokenRule(t).IsMatch(StripComments(Legitimate))).ToList();

        Assert.True(hits.Count == 0, $"the rule fires on legitimate code: {string.Join(", ", hits)}");
    }

    /// <summary>
    /// A comment may say what the code no longer does. <c>Json.h</c> records the measured
    /// <c>sscanf</c> behaviour, and that record is the reason the replacement is not questioned again.
    /// </summary>
    [Fact]
    public void The_rule_ignores_the_comment_that_explains_the_replacement()
    {
        var header = File.ReadAllText(Path.Combine(AdapterRoot(), "Json.h"));

        // The word is in the file...
        Assert.Contains("sscanf", header);

        // ...and only in its comments.
        var code = StripComments(header);
        Assert.False(
            TokenRule("sscanf").IsMatch(code),
            "Json.h calls sscanf outside a comment");
    }

    // ---------------------------------------------------------------------------------------------
    // The replacement is actually used, at both sites
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Json.cpp</c> has two <c>\u</c> sites: the read path that keeps the character, and the skip
    /// path that discards it. Both must validate.
    /// </summary>
    /// <remarks>
    /// The skip path did no validation at all before this change. A skip that accepts what a read
    /// rejects makes <c>SkipValue</c> and <c>ReadString</c> disagree about which documents are well
    /// formed, and the disagreement surfaces as a menu built from half a response.
    /// </remarks>
    [Fact]
    public void Both_escape_sites_validate_before_the_cursor_moves()
    {
        var code = StripComments(File.ReadAllText(Path.Combine(AdapterRoot(), "Json.cpp")));

        var calls = Regex.Count(code, @"ParseHex4\s*\(");
        Assert.Equal(2, calls);

        // Both are guarded: the refusal is the point, not the value.
        var guarded = Regex.Count(code, @"if\s*\(\s*!\s*ParseHex4\s*\(");
        Assert.Equal(2, guarded);

        // And each guard refuses rather than continuing with a half-read escape.
        foreach (Match match in Regex.Matches(code, @"if\s*\(\s*!\s*ParseHex4\s*\([^)]*\)\s*\)\s*\{([^}]*)\}"))
        {
            Assert.Contains("return nullptr;", match.Groups[1].Value);
        }
    }

    /// <summary>
    /// <c>ParseHex4</c> stays header-only and free of <c>&lt;windows.h&gt;</c>. That is not a style
    /// preference: it is what lets the C++ regression test include one header and run on a build
    /// machine that is not Windows, which is where this defect would have been caught the first time.
    /// </summary>
    [Fact]
    public void The_parser_can_be_tested_without_windows()
    {
        var header = File.ReadAllText(Path.Combine(AdapterRoot(), "Json.h"));

        Assert.Contains("inline bool ParseHex4", header);
        Assert.False(
            header.Contains("windows.h", StringComparison.OrdinalIgnoreCase),
            "Json.h must not include windows.h; the C++ test includes it alone");
    }

    // ---------------------------------------------------------------------------------------------
    // The C++ test exists and covers what was asked for
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The behavioural test is a file in the repository, not a command somebody ran once. This
    /// checks it is present, includes the header under test, and carries both halves of the case:
    /// a valid four-digit escape and malformed, non-hex input.
    /// </summary>
    [Fact]
    public void The_cpp_regression_test_covers_a_valid_escape_and_malformed_input()
    {
        var path = Path.Combine(
            RepoRoot(), "tests", "NOBA.Archive.ShellAdapter.Tests", "json_hex_tests.cpp");

        Assert.True(File.Exists(path), $"{path} is missing");

        var test = File.ReadAllText(path);

        Assert.Contains("NOBA.Archive.ShellAdapter/Json.h", test);
        Assert.Contains("ParseHex4", test);

        // A valid escape, in both cases, because JSON allows either.
        Assert.Contains("\"00e9\"", test);
        Assert.Contains("\"00E9\"", test);

        // Malformed and non-hex.
        Assert.Contains("\"00g9\"", test);

        // The inputs the old call accepted, each named so a reader can see what changed.
        Assert.Contains("\" 123\"", test);
        Assert.Contains("\"-123\"", test);
        Assert.Contains("\"0x12\"", test);

        // The short escape that consumed its own closing quote.
        Assert.Contains("12\\\"x", test);

        // A non-zero exit is what makes it a test rather than a demonstration.
        Assert.Contains("return g_failures == 0 ? 0 : 1;", test);
    }

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static IEnumerable<string> AdapterSources() =>
        Directory
            .EnumerateFiles(AdapterRoot(), "*.*", SearchOption.AllDirectories)
            .Where(static p => p.EndsWith(".cpp", StringComparison.Ordinal) ||
                               p.EndsWith(".h", StringComparison.Ordinal))
            .OrderBy(static p => p, StringComparer.Ordinal);

    private static string AdapterRoot() =>
        Path.Combine(RepoRoot(), "src", "NOBA.Archive.ShellAdapter");

    private static string RepoRoot()
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

        Assert.Fail("repository root not found above the test output directory");
        return string.Empty;
    }
}
