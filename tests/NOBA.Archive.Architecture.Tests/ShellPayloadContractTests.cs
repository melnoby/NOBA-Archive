using System.Text.RegularExpressions;
using NOBA.Archive.Core.Actions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// The payload contract: what the build produces must be what the register script demands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two scripts and a C++ file all name the same files, and none of them can check the others.</b>
/// <c>build-shell-payload.ps1</c> produces a directory, <c>modern-menu-register.ps1</c> refuses one
/// that is incomplete, and <c>ClrHost.cpp</c> loads the bridge from beside the adapter. A name typed
/// differently in any of the three produces a menu that draws and activates nothing, on a machine,
/// with nothing in any log. So the names live in <see cref="ShellPackageManifest"/> and this refuses
/// to let the copies drift - the same arrangement <c>Guids.h</c> has for the CLSID.
/// </para>
/// <para>
/// <b>What this cannot prove.</b> That MSVC compiles the adapter, that the bridge publishes, or that
/// the payload works. All three need Windows. What it proves is that the pieces agree about what
/// they are called and where they go, which is the part that has silently disagreed before.
/// </para>
/// </remarks>
public sealed class ShellPayloadContractTests
{
    private const string BuildScript = "build-shell-payload.ps1";
    private const string RegisterScript = "modern-menu-register.ps1";

    /// <summary>
    /// The directory both scripts default to. A build that writes somewhere the register script does
    /// not look is a payload nobody can find.
    /// </summary>
    private const string DefaultPayload = @"artifacts\app";

    [Fact]
    public void The_build_script_and_the_register_script_name_the_same_payload_directory()
    {
        var build = Read(BuildScript);
        var register = Read(RegisterScript);

        Assert.Contains(DefaultPayload, build);
        Assert.Contains(DefaultPayload, register);
    }

    /// <summary>
    /// Both scripts must name both required DLLs, spelled as
    /// <see cref="ShellPackageManifest"/> spells them.
    /// </summary>
    [Theory]
    [InlineData(BuildScript)]
    [InlineData(RegisterScript)]
    public void The_script_names_both_required_libraries(string script)
    {
        var code = Read(script);

        Assert.Contains(ShellPackageManifest.AdapterLibrary, code);
        Assert.Contains(ShellPackageManifest.BridgeLibrary, code);
    }

    /// <summary>
    /// The manifest's COM class Path and the payload's adapter are the same file. If they were not,
    /// the package would register a class pointing at a file the payload does not contain.
    /// </summary>
    [Fact]
    public void The_manifest_points_at_the_library_the_build_produces()
    {
        var manifest = ShellPackageManifest.Generate(
            new ShellPackageIdentity("CN=NOBA-DEV-ONLY", "0.5.0.0", "NOBA Archive", "NOBA"));

        Assert.Contains(ShellPackageManifest.AdapterLibrary, manifest);
        Assert.Contains(ShellPackageManifest.AdapterLibrary, Read(BuildScript));
    }

    /// <summary>
    /// <c>ClrHost.cpp</c> resolves the bridge and its runtimeconfig relative to the adapter's own
    /// module directory. "Beside" is literal, and the build has to honour it.
    /// </summary>
    [Fact]
    public void The_adapter_loads_the_bridge_the_build_puts_beside_it()
    {
        var clrHost = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NOBA.Archive.ShellAdapter", "ClrHost.cpp"));

        Assert.Contains(ShellPackageManifest.BridgeLibrary, clrHost);
        Assert.Contains("NOBA.Archive.ShellBridge.runtimeconfig.json", clrHost);

        // And the build must verify that file exists, because hostfxr is handed it by name.
        Assert.Contains("runtimeconfig.json", Read(BuildScript));
    }

    /// <summary>
    /// The build fails when either required DLL is absent, rather than producing a directory the
    /// register script will reject later with less context.
    /// </summary>
    [Fact]
    public void The_build_fails_clearly_when_a_required_library_is_missing()
    {
        var code = Read(BuildScript);

        Assert.Contains("the payload is missing", code);
        Assert.Contains("exit 1", code);
    }

    // ---------------------------------------------------------------------------------------------
    // What the build must NOT do
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A build step that could register, sign or trust would make "I only built it" untrue, and the
    /// operator would have no way to know from the command they typed.
    /// </summary>
    [Theory]
    [InlineData("Add-AppxPackage")]
    [InlineData("Remove-AppxPackage")]
    [InlineData("New-SelfSignedCertificate")]
    [InlineData("signtool")]
    [InlineData("makeappx")]
    [InlineData("Set-ItemProperty")]
    [InlineData("New-ItemProperty")]
    [InlineData("X509Store")]
    public void The_build_script_does_not_deploy_sign_trust_or_touch_the_registry(string forbidden)
    {
        var code = StripComments(Read(BuildScript));

        Assert.False(
            code.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"{BuildScript} contains '{forbidden}'. It builds and verifies; it deploys nothing.");
    }

    /// <summary>The gate is a separate decision, run by a person who chose to run it.</summary>
    [Fact]
    public void The_build_script_does_not_run_the_gate()
    {
        var code = StripComments(Read(BuildScript));

        Assert.False(
            code.Contains("modern-menu-gate.ps1", StringComparison.OrdinalIgnoreCase),
            "the build must not run the gate");
    }

    /// <summary>
    /// The classic surface is not Phase 5's business, and this script has no reason to name its
    /// registration script at all.
    /// </summary>
    [Fact]
    public void The_build_script_does_not_touch_the_classic_registration_script()
    {
        Assert.False(
            Read(BuildScript).Contains("shell-register.ps1", StringComparison.Ordinal),
            "shell-register.ps1 is surface B and stays untouched");
    }

    // ---------------------------------------------------------------------------------------------
    // Determinism and the toolchain
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Without <c>/Brepro</c> every build of identical sources produces different bytes, and "did the
    /// payload change?" stops being answerable by hash - which is the only question the reported
    /// hashes exist to answer.
    /// </summary>
    [Fact]
    public void The_native_project_builds_without_a_timestamp()
    {
        var vcxproj = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "NOBA.Archive.ShellAdapter", "NOBA.Archive.ShellAdapter.vcxproj"));

        // Once for the compiler, once for the linker: the object files and the PE header each carry
        // their own timestamp.
        //
        // Counted inside <AdditionalOptions> rather than across the file. The first version counted
        // raw occurrences and found four - because the comments EXPLAINING /Brepro contain the word.
        // A rule that counts its own documentation is the same defect as a rule that matches
        // LoadLibraryExW, and this project has now produced it three times.
        var passed = Regex.Matches(vcxproj, @"<AdditionalOptions>([^<]*)</AdditionalOptions>")
            .Select(m => m.Groups[1].Value)
            .Count(v => v.Contains("/Brepro", StringComparison.Ordinal));

        Assert.Equal(2, passed);
    }

    /// <summary>The managed half of the same property.</summary>
    [Fact]
    public void The_managed_build_is_deterministic()
    {
        Assert.Contains("ContinuousIntegrationBuild=true", Read(BuildScript));
    }

    /// <summary>
    /// The build must not need nuget.org. The VM's verified toolchain is four components, and network
    /// access to a package feed is not one of them.
    /// </summary>
    [Fact]
    public void The_build_needs_no_package_feed()
    {
        var code = Read(BuildScript);

        Assert.Contains("NobaOfflineBuild=true", code);
    }

    /// <summary>
    /// MSVC is located through <c>vswhere</c> with the same component id the toolchain verification
    /// asked for. A plain PowerShell prompt is not a developer prompt and <c>cl.exe</c> is not on its
    /// PATH, so a build that assumed otherwise would fail for a reason that reads as missing tooling.
    /// </summary>
    [Fact]
    public void The_build_locates_msvc_the_way_the_verification_did()
    {
        var code = Read(BuildScript);

        Assert.Contains("vswhere.exe", code);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", code);
        Assert.Contains("vcvars64.bat", code);
    }

    /// <summary>
    /// A toolchain difference is recorded, never fatal. A patch bump is not a reason to refuse to
    /// build, but it IS a variable in anything measured afterwards.
    /// </summary>
    [Fact]
    public void A_toolchain_difference_is_reported_rather_than_fatal()
    {
        var code = Read(BuildScript);

        Assert.Contains("14.44.35207", code);
        Assert.Contains("10.0.26100.0", code);
        Assert.Contains("Recorded, not fatal", code);
    }

    /// <summary>
    /// Windows PowerShell 5.1 decodes a BOM-less file as the machine's ANSI codepage; bytes
    /// 0x91-0x94 are quote characters there and silently open strings (DELTA section 13).
    /// </summary>
    [Fact]
    public void The_build_script_survives_windows_powershell_5_1()
    {
        var bytes = File.ReadAllBytes(ScriptPath(BuildScript));

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var nonAscii = bytes.Count(b => b > 0x7F);

        Assert.True(
            hasBom || nonAscii == 0,
            $"{BuildScript} has {nonAscii} non-ASCII byte(s) and no BOM; 5.1 will not parse it.");
    }

    /// <summary>
    /// The reason the command-line host is in the payload at all, recorded where somebody changing
    /// the build will read it: <c>Invoke</c> resolves the launch plan's executable relative to the
    /// adapter's directory.
    /// </summary>
    [Fact]
    public void The_build_publishes_the_host_that_invoke_starts()
    {
        var code = Read(BuildScript);

        Assert.Contains(ShellPackageManifest.CommandLineHost, code);
    }

    private static string Read(string script) => File.ReadAllText(ScriptPath(script));

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"<#.*?#>", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*#.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static string ScriptPath(string name)
    {
        var path = Path.Combine(RepoRoot(), "scripts", name);
        Assert.True(File.Exists(path), $"{path} is missing");
        return path;
    }

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
