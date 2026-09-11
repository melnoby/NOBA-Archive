using System.Text.RegularExpressions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// Rules about the registration script, enforced here because nothing else can enforce them.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are structural checks, and they are honest about that.</b> The build machine has no
/// Windows registry, so nothing here proves that registration works — only that the script cannot
/// contain the shape of defect that stopped it. The behaviour is proven by running
/// <c>scripts\shell-register.ps1</c> on Windows, which now reads back every key it wrote and exits
/// non-zero if any is missing.
/// </para>
/// <para>
/// The defect being guarded was measured, not guessed. One of the two scopes NOBA registers under is
/// the class key literally named <c>*</c> — the shell's name for "any file" — and every PowerShell
/// provider cmdlet that takes <c>-Path</c> treats that character as a wildcard. On Windows 11
/// (PowerShell 5.1.26100, 801 subkeys under <c>HKCU\Software\Classes</c>) the same cmdlet on the
/// same hive measured:
/// </para>
/// <list type="table">
/// <item><term>Set-ItemProperty, path with no <c>*</c></term><description>35 ms</description></item>
/// <item><term>Set-ItemProperty, <c>*</c> at depth 3</term><description>24,693 ms</description></item>
/// <item><term>Set-ItemProperty, <c>*</c> at depth 5</term><description>840,771 ms (14 minutes)</description></item>
/// <item><term>Set-ItemProperty, <c>*</c> at depth 6</term><description>did not return</description></item>
/// </list>
/// <para>
/// It is not a deadlock. The wildcard is resolved against all 801 class keys, and the cost compounds
/// with every segment beneath it, so registration presents as a hang while making real progress
/// nobody will wait for. <c>New-Item</c> was never the problem — it measured 75 ms, because with
/// <c>-Force</c> on a missing leaf it takes the literal creation path. The guard therefore covers
/// every provider cmdlet, not the one that looked guilty.
/// </para>
/// <para>
/// The second failure mode is worse and silent: a path resolved as a pattern can land keys somewhere
/// the removal list does not name, leaving a machine with a menu entry pointing at a deleted
/// executable. That is why the script counts stray entries rather than trusting that it wrote where
/// it meant to.
/// </para>
/// </remarks>
public sealed class ShellScriptTests
{
    private const string RegisterScript = "shell-register.ps1";

    private const string GateScript = "shell-gate.ps1";

    /// <summary>
    /// SHELL-032: the registration script never hands a registry path to a globbing parameter.
    /// </summary>
    [Fact]
    public void The_registration_script_uses_no_globbing_registry_paths()
    {
        var script = ReadScript(RegisterScript);

        var offenders = GlobbedRegistryPathsIn(script);

        Assert.True(
            offenders.Count == 0,
            "a registry path is being resolved as a pattern; use the literal Microsoft.Win32 API:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>SHELL-033: the rule fires on the shape that broke, and accepts the shape that fixed it.</summary>
    [Fact]
    public void The_globbing_rule_catches_a_provider_path_and_accepts_the_literal_api()
    {
        const string broken = """
            $rootKey = 'HKCU:\Software\Classes'
            $menu = "$rootKey\$scope\shell\$($spec.menuId)"
            New-Item -Path $menu -Force | Out-Null
            Set-ItemProperty -Path $menu -Name 'MUIVerb' -Value $spec.menuTitle
            """;

        const string fixedUp = """
            $menu = "Software\Classes\$Scope\shell\$($spec.menuId)"
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($menu)
            $key.SetValue('MUIVerb', $spec.menuTitle, [Microsoft.Win32.RegistryValueKind]::String)
            """;

        Assert.NotEmpty(GlobbedRegistryPathsIn(broken));
        Assert.Empty(GlobbedRegistryPathsIn(fixedUp));
    }

    /// <summary>
    /// SHELL-034: registering verifies, so it cannot report success on a partial write.
    /// </summary>
    /// <remarks>
    /// The script stopped after its banner once, and the transcript could not distinguish "blocked"
    /// from "finished quietly". A write path that reads itself back cannot produce that ambiguity
    /// again: either every key is present and it says so, or it names what is missing and exits
    /// non-zero.
    /// </remarks>
    [Fact]
    public void Registering_reads_back_what_it_wrote()
    {
        var script = ReadScript(RegisterScript);

        Assert.Contains("OpenSubKey", script);
        Assert.Contains("REGISTRATION INCOMPLETE", script);
        Assert.Contains("REMOVAL INCOMPLETE", script);

        // Both directions must end non-zero when they did not achieve what they claim.
        Assert.True(
            Regex.Count(script, @"exit 1", RegexOptions.None, TimeSpan.FromSeconds(2)) >= 3,
            "the script must exit non-zero on an incomplete install and an incomplete removal");
    }

    /// <summary>
    /// SHELL-035: install and uninstall read the same generated manifest.
    /// </summary>
    /// <remarks>
    /// Neither direction may carry its own list. That is the whole reason the manifest is generated
    /// from <c>ShellRegistration</c> rather than typed into a script — a verb added in code appears
    /// in both directions or in neither.
    /// </remarks>
    [Fact]
    public void Both_directions_read_the_generated_manifest()
    {
        var script = ReadScript(RegisterScript);

        Assert.Contains("shell-verbs.json", script);
        Assert.Contains("ConvertFrom-Json", script);

        // The removal branch must derive its keys from the manifest-backed helpers, not from
        // literals of its own.
        var removeBranch = Between(script, "if ($Remove)", "# Where the executables are");
        Assert.Contains("Get-MenuKey", removeBranch);
        Assert.DoesNotContain("Software\\Classes\\*", removeBranch);
    }

    /// <summary>
    /// SHELL-036: registration is idempotent by construction.
    /// </summary>
    /// <remarks>
    /// <c>CreateSubKey</c> returns an existing key rather than replacing it, and every value is set
    /// by name — so a second run rewrites the same values in place and cannot produce a duplicate
    /// menu. Removal tolerates absence for the same reason: an uninstaller that fails because the
    /// machine was already clean is one people learn to skip.
    /// </remarks>
    [Fact]
    public void Registration_and_removal_are_both_idempotent()
    {
        var script = ReadScript(RegisterScript);

        Assert.Contains("CreateSubKey", script);

        // DeleteSubKeyTree's second argument is throwOnMissingSubKey; it must be false.
        Assert.Contains("DeleteSubKeyTree($SubKey, $false)", script);

        // Nothing may delete-then-recreate: that is how a menu flickers out from under a user who
        // happens to have Explorer open, and it is not needed when the write is in-place.
        var installBranch = Between(script, "# Where the executables are", "# Read back what was written");
        Assert.DoesNotContain("Remove-NobaKey", installBranch);
    }

    /// <summary>
    /// SHELL-037: no command interpreter is introduced by the registration path.
    /// </summary>
    [Fact]
    public void Registration_never_routes_through_a_command_interpreter()
    {
        var script = ReadScript(RegisterScript);
        var stripped = StripComments(script);

        foreach (var interpreter in new[] { "cmd.exe", "cmd /c", "powershell.exe", "Invoke-Expression", "iex " })
        {
            Assert.DoesNotContain(interpreter.ToLowerInvariant(), stripped.ToLowerInvariant());
        }
    }

    /// <summary>
    /// SHELL-079: the Windows gate reads the registry through the literal API too.
    /// </summary>
    /// <remarks>
    /// The gate walks the same class keys the registration writes, <c>*</c> among them, so it can
    /// reintroduce the wildcard defect in exactly the way SHELL-032 forbids for the registration. A
    /// gate that takes fourteen minutes to read one key is a gate nobody finishes — which is the
    /// state the previous one was in.
    /// </remarks>
    [Fact]
    public void The_windows_gate_uses_no_globbing_registry_paths()
    {
        var script = ReadScript(GateScript);
        var offenders = GlobbedRegistryPathsIn(script);

        Assert.True(
            offenders.Count == 0,
            "the gate resolves a registry path as a pattern; use the literal Microsoft.Win32 API:\n  "
            + string.Join("\n  ", offenders));

        Assert.Contains("Microsoft.Win32.Registry", script);
    }

    /// <summary>
    /// SHELL-080: the gate runs what the registry says, not what the manifest says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole value of a runtime gate is that it can disagree with the thing it is testing. A gate
    /// that rebuilt the command line from <c>shell-verbs.json</c> and then ran that would agree with
    /// the manifest by construction and could never catch a registration that wrote something else —
    /// which is precisely the class of defect D2 and D3 were.
    /// </para>
    /// <para>
    /// So the command string must be READ from the key it was written to, and the substitution the
    /// gate performs must be the shell's documented one. Both are asserted structurally here; whether
    /// Explorer performs the same substitution is SHELL-L8 and no test on this machine can settle it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_windows_gate_executes_the_registered_command_rather_than_rebuilding_one()
    {
        var script = ReadScript(GateScript);
        var stripped = StripComments(script);

        // It reads the command out of the key that registration wrote.
        Assert.Contains(@"shell\$EntryId\command", stripped);
        Assert.Contains("Get-NobaValue", stripped);

        // And it substitutes the selection the way the shell documents, rather than inventing a form.
        Assert.Contains("%*", stripped);
        Assert.Contains("ConvertTo-NobaInvocation", stripped);

        // No command interpreter anywhere on the execution path: the gate builds an argv array.
        foreach (var interpreter in new[] { "cmd.exe", "cmd /c", "Invoke-Expression", "iex " })
        {
            Assert.DoesNotContain(interpreter.ToLowerInvariant(), stripped.ToLowerInvariant());
        }
    }

    /// <summary>
    /// SHELL-081: the gate keeps "not implemented" separate from "nobody looked".
    /// </summary>
    /// <remarks>
    /// The four gates collapse into one the moment a run can report PASS while the modern menu was
    /// never built and a manual step was skipped. So the script must carry both verdicts and must
    /// refuse to call a run with unanswered steps a pass.
    /// </remarks>
    [Fact]
    public void The_windows_gate_never_reports_a_pass_it_did_not_earn()
    {
        var script = ReadScript(GateScript);

        Assert.Contains("NOT IMPLEMENTED", script);
        Assert.Contains("NOT RUN", script);

        // Surface A is named as unimplemented rather than quietly absent.
        Assert.Contains("SHELL-W1", script);
        Assert.Contains("SHELL-W2", script);

        // The verdict requires zero failures AND zero unanswered steps.
        Assert.Contains("$failed -eq 0 -and $notrun -eq 0", script);

        // And the four gates are reported apart.
        foreach (var gate in new[]
        {
            "MODEL VERIFIED", "REGISTRATION VERIFIED",
            "WINDOWS CLASSIC RUNTIME VERIFIED", "WINDOWS 11 MODERN MENU VERIFIED",
        })
        {
            Assert.Contains(gate, script);
        }
    }

    /// <summary>
    /// Registry paths handed to a parameter that performs wildcard resolution.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: it looks for provider cmdlets used with a registry drive, which is the
    /// only way this script could reintroduce the defect. Comments are stripped first so the file
    /// may keep explaining what it does not do.
    /// </remarks>
    private static List<string> GlobbedRegistryPathsIn(string script)
    {
        string[] providerCmdlets =
        [
            "New-Item", "Set-ItemProperty", "Get-ItemProperty", "New-ItemProperty",
            "Remove-Item", "Remove-ItemProperty", "Test-Path", "Get-Item", "Get-ChildItem",
        ];

        var found = new List<string>();
        var lines = StripComments(script).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("HK", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var cmdlet in providerCmdlets)
            {
                if (line.Contains(cmdlet, StringComparison.Ordinal))
                {
                    found.Add($"line {i + 1}: {cmdlet} on a registry path -- {line.Trim()}");
                }
            }
        }

        // A variable holding a registry drive path is the same defect one indirection away.
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"=\s*['""]HK(CU|LM|CR|U|CC):", RegexOptions.None, TimeSpan.FromSeconds(2)))
            {
                found.Add($"line {i + 1}: a registry drive path is stored for provider use -- {lines[i].Trim()}");
            }
        }

        return found;
    }

    private static string StripComments(string script)
    {
        var lines = script.Split('\n');
        var kept = new List<string>(lines.Length);

        var inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw;

            if (inBlock)
            {
                var close = line.IndexOf("#>", StringComparison.Ordinal);
                if (close < 0)
                {
                    kept.Add(string.Empty);
                    continue;
                }

                line = line[(close + 2)..];
                inBlock = false;
            }

            var open = line.IndexOf("<#", StringComparison.Ordinal);
            if (open >= 0)
            {
                inBlock = true;
                line = line[..open];
            }

            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static string Between(string script, string start, string end)
    {
        var from = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is not in the script; the test is reading the wrong file");

        var to = script.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' does not follow '{start}'; the script's shape changed");

        return script[from..to];
    }

    private static string ReadScript(string name)
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var path = Path.Combine(root!, "scripts", name);
        Assert.True(File.Exists(path), $"{path} is missing");

        return File.ReadAllText(path);
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
