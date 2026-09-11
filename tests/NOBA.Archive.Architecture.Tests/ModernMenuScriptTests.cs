using System.Text.RegularExpressions;
using NOBA.Archive.Core.Actions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// Rules about the surface-A scripts, enforced here because nothing else can enforce them.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are structural checks, and they are honest about that.</b> The build machine has no
/// certificate store, no Appx subsystem and no Explorer, so nothing here proves that registration
/// works - only that the scripts cannot contain the shapes of defect that already stopped it. Four
/// E2 attempts and two VM runs produced this list; every rule below names the failure it descends
/// from.
/// </para>
/// <para>
/// <b>The encoding rule moves here on purpose.</b> It was learned in the Phase 5 harness
/// (DELTA section 13) and lives there in PowerShell - but the harness is expected to be deleted, and
/// the rule is not. A BOM-less file with non-ASCII bytes is decoded by Windows PowerShell 5.1 as the
/// machine's ANSI codepage, where bytes 0x91-0x94 are quote characters that silently open strings.
/// <c>run-e2.ps1</c> reached a VM byte-identical to source and would not parse.
/// </para>
/// </remarks>
public sealed class ModernMenuScriptTests
{
    private static readonly string[] SurfaceAScripts =
    [
        "dev-certificate.ps1",
        "modern-menu-register.ps1",
        "modern-menu-gate.ps1",
    ];

    // ---------------------------------------------------------------------------------------------
    // Encoding - DELTA section 13
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Pure ASCII, or a UTF-8 BOM. Pure ASCII decodes to the same characters under UTF-8 and under
    /// every ANSI codepage - no ANSI codepage reassigns 0x00-0x7F and no DBCS lead byte is below
    /// 0x81 - so it removes the failure class rather than avoiding it.
    /// </summary>
    [Theory]
    [InlineData("dev-certificate.ps1")]
    [InlineData("modern-menu-register.ps1")]
    [InlineData("modern-menu-gate.ps1")]
    public void The_script_survives_windows_powershell_5_1(string name)
    {
        var bytes = File.ReadAllBytes(ScriptPath(name));

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var nonAscii = bytes.Count(b => b > 0x7F);
        var quoteCapable = bytes.Count(b => b is 0x91 or 0x92 or 0x93 or 0x94);

        Assert.True(
            hasBom || nonAscii == 0,
            $"{name} is UTF-8 without a BOM and has {nonAscii} non-ASCII byte(s), " +
            $"{quoteCapable} of which become a PowerShell quote character under an ANSI codepage. " +
            "Windows PowerShell 5.1 will not parse it. Make it pure ASCII, or add a BOM.");
    }

    // ---------------------------------------------------------------------------------------------
    // Certificate handling - DELTA sections 10 and 11.3
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Remove-Item</c> on the <c>Cert:</c> provider failed to remove a LocalMachine certificate
    /// twice - once inside a script and once by hand from an elevated prompt - leaving a DEV-ONLY
    /// certificate trusted machine-wide. Every store operation goes through <c>X509Store</c>.
    /// </summary>
    [Fact]
    public void No_surface_a_script_removes_a_certificate_through_the_cert_provider()
    {
        var offenders = new List<string>();

        foreach (var name in SurfaceAScripts)
        {
            var code = StripComments(File.ReadAllText(ScriptPath(name)));
            if (Regex.IsMatch(code, @"Remove-Item[^\r\n]*Cert:", RegexOptions.IgnoreCase))
            {
                offenders.Add(name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Remove-Item on the Cert: provider does not reliably remove a LocalMachine certificate; " +
            $"use X509Store. Offenders: {string.Join(", ", offenders)}");
    }

    /// <summary>The control: the rule must fire on the shape it exists to forbid.</summary>
    [Fact]
    public void The_cert_provider_rule_catches_the_shape_that_failed()
    {
        const string Offending = @"Remove-Item -Path 'Cert:\LocalMachine\TrustedPeople\ABC123' -Force";

        Assert.True(
            Regex.IsMatch(StripComments(Offending), @"Remove-Item[^\r\n]*Cert:", RegexOptions.IgnoreCase),
            "the rule stopped matching the shape it exists to forbid");
    }

    /// <summary>
    /// MSIX deployment reads <c>LocalMachine\TrustedPeople</c>. A certificate trusted only for the
    /// current user is refused with <c>0x800B0109</c>, an error naming neither the store nor the
    /// certificate - and plan section L.6 originally recommended the user store.
    /// </summary>
    [Fact]
    public void The_certificate_script_trusts_in_the_machine_store()
    {
        var code = StripComments(File.ReadAllText(ScriptPath("dev-certificate.ps1")));

        Assert.Contains("TrustedPeople", code);
        Assert.Contains("LocalMachine", code);
        Assert.Contains("X509Store", code);
    }

    /// <summary>
    /// A store that cannot be read is UNKNOWN. Reporting it as zero is how a cleanup announces
    /// success over a certificate it never saw.
    /// </summary>
    [Fact]
    public void An_unreadable_store_is_not_reported_as_empty()
    {
        var code = File.ReadAllText(ScriptPath("dev-certificate.ps1"));

        Assert.Contains("return -1", code);
        Assert.Contains("UNKNOWN", code);
    }

    // ---------------------------------------------------------------------------------------------
    // The sideloading gate - DELTA section 12
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The E2 harness printed the sideloading state and deployed anyway; deployment failed
    /// <c>0x80073CFF</c>. A fact that is printed but not wired to a decision is decoration.
    /// </summary>
    [Fact]
    public void The_register_script_refuses_before_it_creates_anything()
    {
        var code = File.ReadAllText(ScriptPath("modern-menu-register.ps1"));

        var refusal = code.IndexOf("REFUSING - BLOCKED BY ENVIRONMENT", StringComparison.Ordinal);
        var makeappx = code.IndexOf("makeappx pack", StringComparison.Ordinal);
        var register = code.IndexOf("Add-AppxPackage -Path", StringComparison.Ordinal);

        Assert.True(refusal >= 0, "the register script has no sideloading refusal");
        Assert.True(register >= 0, "the register script never registers anything");
        Assert.True(
            refusal < register,
            "the sideloading gate must come BEFORE registration, or it is a note rather than a gate");
        Assert.True(
            makeappx < 0 || refusal < makeappx,
            "the gate must also come before packing - nothing is created before the refusal");
    }

    /// <summary>
    /// NOBA never modifies enterprise AppX policy (plan section L.13). It is a product decision, not
    /// a technical obstacle to route around, and a build script is not the place to overrule an
    /// organisation's setting.
    /// </summary>
    [Fact]
    public void No_surface_a_script_writes_a_sideloading_or_policy_key()
    {
        var offenders = new List<string>();

        foreach (var name in SurfaceAScripts)
        {
            var code = StripComments(File.ReadAllText(ScriptPath(name)));
            foreach (var write in new[] { "Set-ItemProperty", "New-ItemProperty", "reg add", "reg.exe add" })
            {
                if (code.Contains(write, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{name}: {write}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Sideloading, Developer Mode and Group Policy are machine security settings and are read, " +
            $"never written. Offenders: {string.Join(", ", offenders)}");
    }

    // ---------------------------------------------------------------------------------------------
    // Identity and elevation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// SHELL-065. The register script generates the manifest with the publisher it read FROM the
    /// certificate, so the two cannot disagree - <c>Add-AppxPackage</c> compares them character for
    /// character and fails with a signature error naming neither.
    /// </summary>
    [Fact]
    public void The_register_script_generates_the_manifest_rather_than_editing_one()
    {
        var code = StripComments(File.ReadAllText(ScriptPath("modern-menu-register.ps1")));

        Assert.Contains("appx-manifest", code);
        Assert.Contains("--publisher", code);
        Assert.Contains("$cert.Subject", code);
    }

    /// <summary>
    /// Registering as administrator registers for the administrator, and the surrogate would then run
    /// as one - which is the boundary plan section L.4 exists to keep and E2 criterion 3 measured.
    /// </summary>
    [Fact]
    public void The_register_script_refuses_to_run_elevated()
    {
        var code = File.ReadAllText(ScriptPath("modern-menu-register.ps1"));

        Assert.Contains("must NOT run elevated", code);
    }

    /// <summary>
    /// The subject is literally <c>CN=NOBA-DEV-ONLY</c> so it cannot be mistaken for a real signing
    /// certificate in a store, and the banner prints on every run.
    /// </summary>
    [Fact]
    public void The_certificate_is_unmistakably_development_only()
    {
        var code = File.ReadAllText(ScriptPath("dev-certificate.ps1"));

        Assert.Contains("CN=NOBA-DEV-ONLY", code);
        Assert.Contains("NOT A SHIPPING CERTIFICATE", code);
    }

    /// <summary>
    /// Phase 6 owns procurement. A green gate must not be quotable as production signing readiness,
    /// so the gate says so in its own summary rather than in a document nobody reads beside it.
    /// </summary>
    [Fact]
    public void The_gate_disclaims_production_signing_readiness_in_its_own_output()
    {
        var code = File.ReadAllText(ScriptPath("modern-menu-gate.ps1"));

        Assert.Contains("WHAT THIS GATE DOES NOT CLAIM", code);
        Assert.Contains("production signing readiness", code);
    }

    /// <summary>
    /// "Not run" is work somebody still owes; "not implemented" is a surface that does not exist. A
    /// gate that collapsed them would let a skip read like something that could have passed.
    /// </summary>
    [Fact]
    public void The_gate_keeps_not_run_distinct_from_not_implemented()
    {
        var code = File.ReadAllText(ScriptPath("modern-menu-gate.ps1"));

        Assert.Contains("NOT RUN", code);
        Assert.Contains("NOT IMPLEMENTED", code);
        Assert.Contains("A NOT RUN is not a pass", code);
    }

    /// <summary>
    /// The gate reads the CLSID from the generated manifest. A gate carrying its own copy of the
    /// identity could pass while the product registered a different one.
    /// </summary>
    [Fact]
    public void The_gate_does_not_carry_its_own_copy_of_the_clsid()
    {
        var code = File.ReadAllText(ScriptPath("modern-menu-gate.ps1"));
        var bare = ShellRegistration.ModernHandlerClsid.Trim('{', '}');

        Assert.False(
            code.Contains(bare, StringComparison.OrdinalIgnoreCase),
            "the gate carries its own copy of the CLSID; it must read it from the generated manifest");
        Assert.Contains("AppxManifest.xml", code);
    }

    /// <summary>
    /// The existing classic-surface script is out of scope and must stay untouched - it is surface B,
    /// it works, and Phase 5 has no business editing it.
    /// </summary>
    [Fact]
    public void The_classic_registration_script_is_not_a_surface_a_script()
    {
        Assert.DoesNotContain(SurfaceAScripts, s => s == "shell-register.ps1");
    }

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"<#.*?#>", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*#.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    private static string ScriptPath(string name)
    {
        var root = RepositoryRoot();
        Assert.NotNull(root);

        var path = Path.Combine(root!, "scripts", name);
        Assert.True(File.Exists(path), $"{path} is missing");

        return path;
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
