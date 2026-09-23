using System.Text.RegularExpressions;
using Xunit;

namespace NOBA.Archive.Architecture.Tests;

/// <summary>
/// One fact, three files: where the signing key lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> <c>dev-certificate.ps1</c> CREATES the key in a store,
/// <c>modern-menu-register.ps1</c> LOOKS for it in a store, and <c>signtool</c> SEARCHES a store
/// decided entirely by whether <c>/sm</c> is on its command line. Nothing in PowerShell, in the SDK,
/// or in any build makes those three agree.
/// </para>
/// <para>
/// <b>They already disagreed once, and it cost a VM run.</b> The key was created in
/// <c>LocalMachine\My</c>. Section 1 opened <c>LocalMachine\My</c>, found it, and printed
/// "trusted and signable". Section 5 then ran <c>signtool sign /fd SHA256 /a /sha1 &lt;thumbprint&gt;</c>
/// - no <c>/sm</c>, so signtool searched <c>CurrentUser\My</c> - and failed with "No certificates
/// were found that met all the given criteria". Every statement in that transcript was true. The
/// check simply proved something about a store the command was never pointed at.
/// </para>
/// <para>
/// <b>Why the key belongs in the user store rather than /sm belonging on the command line.</b>
/// The private key of a <c>LocalMachine</c> certificate is readable only by Administrators, and
/// <c>modern-menu-register.ps1</c> REFUSES to run elevated on purpose (plan L.4: registering as an
/// administrator registers the package for the administrator, and the surrogate would then run as
/// one). So <c>/sm</c> was never actually available - it would have moved the failure from "cannot
/// find the key" to "cannot read the key". The E2 harness that passed 3/3 had the right shape all
/// along: private key in <c>CurrentUser\My</c>, public certificate in <c>LocalMachine\TrustedPeople</c>.
/// </para>
/// <para>
/// <b>What is asserted is agreement, not a particular store.</b> A future change that adds
/// <c>/sm</c> AND moves the key to the machine store is consistent, and these tests pass. What they
/// refuse is the pair drifting apart, in either direction.
/// </para>
/// </remarks>
public sealed class SigningStoreTests
{
    private const string CertScript = "dev-certificate.ps1";
    private const string RegisterScript = "modern-menu-register.ps1";

    /// <summary>
    /// The store signtool will search, derived from its command line the way signtool decides it:
    /// <c>/sm</c> means the machine store, its absence means the current user's.
    /// </summary>
    private static string StoreSigntoolWillSearch(string signCommand) =>
        Regex.IsMatch(signCommand, @"(?<![^\s])/sm(?![^\s])") ? "LocalMachine" : "CurrentUser";

    /// <summary>The location <c>Get-SigningCertificate</c> opens.</summary>
    private static string StoreTheScriptOpensForSigning(string code)
    {
        var body = Regex.Match(
            code,
            @"function\s+Get-SigningCertificate\s*\{(.*?)\n\}",
            RegexOptions.Singleline);

        Assert.True(body.Success, "Get-SigningCertificate was not found in the register script");

        var store = Regex.Match(body.Groups[1].Value, @"X509Store\]::new\(\s*'My'\s*,\s*'(\w+)'\s*\)");
        Assert.True(store.Success, "Get-SigningCertificate does not open a 'My' store in a readable shape");

        return store.Groups[1].Value;
    }

    /// <summary>The <c>signtool sign</c> line, as written.</summary>
    private static string SignCommand(string code)
    {
        var line = Regex.Match(code, @"^.*\$signtool\s+sign\b.*$", RegexOptions.Multiline);
        Assert.True(line.Success, "no 'signtool sign' invocation found in the register script");
        return line.Value;
    }

    // ---------------------------------------------------------------------------------------------
    // The coupling
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The store the script checks is the store signtool searches. This is the whole test; the rest
    /// is controls and the two endpoints.
    /// </summary>
    [Fact]
    public void The_store_that_is_checked_is_the_store_signtool_searches()
    {
        var code = Read(RegisterScript);

        var checkedStore = StoreTheScriptOpensForSigning(code);
        var searchedStore = StoreSigntoolWillSearch(SignCommand(code));

        Assert.Equal(searchedStore, checkedStore);
    }

    /// <summary>
    /// The control, built from the exact pair that failed on the VM. A rule that has never been seen
    /// to fire is not a rule.
    /// </summary>
    [Fact]
    public void The_rule_catches_the_pair_that_failed_on_the_vm()
    {
        const string Broken = """
            function Get-SigningCertificate {
                $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
                $store.Open('ReadOnly')
            }
            & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $MsixPath
            """;

        Assert.Equal("LocalMachine", StoreTheScriptOpensForSigning(Broken));
        Assert.Equal("CurrentUser", StoreSigntoolWillSearch(SignCommand(Broken)));
    }

    /// <summary>
    /// The other direction: <c>/sm</c> with a machine store is a CONSISTENT pair. The rule is about
    /// agreement, so it must not fire here - otherwise it is a rule about one store that happens to
    /// be spelled as a rule about agreement.
    /// </summary>
    [Fact]
    public void The_rule_accepts_the_other_consistent_pair()
    {
        const string Machine = """
            function Get-SigningCertificate {
                $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
                $store.Open('ReadOnly')
            }
            & $signtool sign /fd SHA256 /sm /a /sha1 $cert.Thumbprint $MsixPath
            """;

        Assert.Equal(
            StoreSigntoolWillSearch(SignCommand(Machine)),
            StoreTheScriptOpensForSigning(Machine));
    }

    /// <summary>
    /// <c>/sm</c> is detected as a flag, not as a substring. A path or a comment containing those two
    /// letters must not be read as "use the machine store" - the same word-boundary defect that
    /// matched <c>rar</c> inside <c>LoadLibraryExW</c>.
    /// </summary>
    [Fact]
    public void The_flag_is_detected_as_a_flag()
    {
        Assert.Equal("CurrentUser", StoreSigntoolWillSearch(@"& $signtool sign /fd SHA256 /a C:\build\x/small.msix"));
        Assert.Equal("CurrentUser", StoreSigntoolWillSearch(@"& $signtool sign /fd SHA256 /a /smart-card-no $p"));
        Assert.Equal("LocalMachine", StoreSigntoolWillSearch(@"& $signtool sign /sm /fd SHA256 $p"));
        Assert.Equal("LocalMachine", StoreSigntoolWillSearch(@"& $signtool sign /fd SHA256 /sm"));
    }

    // ---------------------------------------------------------------------------------------------
    // The third file: where the key is created
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>dev-certificate.ps1</c> creates the key in the store the register script will open. A
    /// certificate created somewhere else is a key nobody can sign with, discovered two scripts later.
    /// </summary>
    [Fact]
    public void The_key_is_created_in_the_store_the_signing_check_opens()
    {
        var creation = Regex.Match(Read(CertScript), @"-CertStoreLocation\s+'Cert:\\(\w+)\\My'");

        Assert.True(creation.Success, "dev-certificate.ps1 does not create the key in a readable shape");
        Assert.Equal(StoreTheScriptOpensForSigning(Read(RegisterScript)), creation.Groups[1].Value);
    }

    /// <summary>
    /// And it proves the key is there by reading it back through the same API signtool's search
    /// mirrors. "New-SelfSignedCertificate returned an object" is a weaker fact than "there is a
    /// usable key in that store", and only the second one predicts whether signing works.
    /// </summary>
    [Fact]
    public void The_creation_is_verified_by_reading_the_key_back()
    {
        var code = Read(CertScript);

        Assert.Contains("X509Store]::new('My', 'CurrentUser')", code);
        Assert.Contains("HasPrivateKey", code);
        Assert.Contains("the store signtool searches", code);
    }

    // ---------------------------------------------------------------------------------------------
    // What must NOT move
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Trust is a different statement from signing and stays machine-wide. Moving the private key
    /// must not drag the trust anchor into the user store - that is 0x800B0109, already paid for
    /// once (DELTA section 10).
    /// </summary>
    [Fact]
    public void Trust_stays_in_the_machine_store()
    {
        var cert = Read(CertScript);

        Assert.Contains("X509Store]::new('TrustedPeople', 'LocalMachine')", cert);
        Assert.Contains("0x800B0109", cert);

        // And the register script still refuses to proceed when the machine store has no trust.
        var register = Read(RegisterScript);
        Assert.Contains("Get-CertCount -StoreName 'TrustedPeople' -Location 'LocalMachine'", register);
    }

    /// <summary>
    /// Only the PUBLIC certificate is trusted. A private key in <c>TrustedPeople</c> would put a
    /// signing key in a store whose job is to say who may be trusted - not the same statement.
    /// </summary>
    [Fact]
    public void Only_the_public_certificate_is_trusted()
    {
        var code = Read(CertScript);

        // Created: the public half is taken from RawData, which carries no key.
        Assert.Contains("X509Certificate2]::new($cert.RawData)", code);

        // Trusted: what is added is the certificate read from the exported file, and that file is
        // refused if it turns out to carry a key.
        Assert.Contains("$trusted.Add($incoming)", code);
        Assert.Contains("if ($incoming.HasPrivateKey)", code);
    }

    /// <summary>Strips PowerShell comments so a rule cannot fire on its own explanation.</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"<#.*?#>", string.Empty, RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*#.*$", string.Empty, RegexOptions.Multiline);
        return source;
    }

    // ---------------------------------------------------------------------------------------------
    // Two accounts: creating a key and granting trust are different decisions
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The create step refuses to run elevated, and the trust step requires it.
    /// </summary>
    /// <remarks>
    /// <c>CurrentUser\My</c> is per-user, and elevation on a machine whose signing user is not an
    /// administrator switches accounts. That happened: one elevated run created the key under
    /// <c>nob11\Administrator</c> while the user who signs is <c>NobaTester</c>. Every line printed
    /// ok - the key existed, the trust existed - and signtool, which searches the CALLING user's
    /// store, would still have found nothing. A script that elevates the whole job moves the key to
    /// whoever answered the UAC prompt.
    /// </remarks>
    [Fact]
    public void Creating_a_key_refuses_elevation_and_trusting_requires_it()
    {
        var code = Read(CertScript);

        Assert.Contains("must NOT run elevated", code);
        Assert.Contains("$Trust", code);

        // The trust branch is the one that demands Administrator. Delimited by the CREATE banner
        // rather than by the next comment line: the branch OPENS with a banner of its own, so a
        // lazy match to the first "# =" stops on the header and reads an empty body.
        var trustBranch = Regex.Match(
            code,
            @"if \(\$Trust\) \{(.*?)# CREATE - the signing user",
            RegexOptions.Singleline);

        Assert.True(trustBranch.Success, "the -Trust branch was not found");
        Assert.Contains("this needs Administrator", trustBranch.Groups[1].Value);

        // ...and the create path is the one that refuses it, so the two cannot be satisfied by the
        // same run whatever the operator does.
        var createPath = code[(code.IndexOf("# CREATE - the signing user", StringComparison.Ordinal))..];
        Assert.Contains("if (Test-Elevated) {", createPath);
    }

    /// <summary>
    /// The two runs are two accounts, so the public certificate travels between them as a file - and
    /// only the public one. A <c>.pfx</c> here would leave a signing key on disk.
    /// </summary>
    [Fact]
    public void Only_the_public_certificate_travels_between_the_two_runs()
    {
        var code = Read(CertScript);

        Assert.Contains("X509ContentType]::Cert", code);
        Assert.Contains(".cer", code);

        // Comments stripped first. The comment EXPLAINING why there is no .pfx contains ".pfx", and
        // the first version of this rule failed on it - the same defect as counting /Brepro inside
        // its own documentation, and as matching "rar" inside LoadLibraryExW.
        var executable = StripComments(code);
        Assert.False(
            executable.Contains(".pfx", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("X509ContentType]::Pfx", StringComparison.Ordinal),
            "the private key must never be exported");

        // And the trust step refuses a file that carries one, rather than trusting whatever it is given.
        Assert.Contains("carries a PRIVATE KEY", code);
    }

    // ---------------------------------------------------------------------------------------------
    // Hardening after the first VM cycles: no second key by accident, no trust by subject alone
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Running the create step twice on the same account refuses instead of minting a second key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This happened on the test VM: two DEV-ONLY keys in NobaTester's <c>CurrentUser\My</c>. Nothing
    /// failed - signtool picked one, <c>-Trust</c> had trusted the other, and the 0x800B0109 that
    /// followed named neither. A store with two keys of the same subject has no "the key".
    /// </para>
    /// <para>
    /// Asserted on the guard, not on its message: the refusal must sit BEFORE
    /// <c>New-SelfSignedCertificate</c> and must <c>exit 1</c>, so deleting the exit while keeping the
    /// text fails here. Comments are stripped first so the explanation cannot satisfy the rule.
    /// </para>
    /// </remarks>
    [Fact]
    public void Creating_a_second_key_is_refused_unless_forced()
    {
        var code = Read(CertScript);
        Assert.Contains("[switch]$Force", code);

        var executable = StripComments(code);
        var creation = executable.IndexOf("New-SelfSignedCertificate", StringComparison.Ordinal);
        Assert.True(creation > 0, "New-SelfSignedCertificate was not found");

        // The count is taken from the store signtool will search, through the same helper the
        // cleanup uses (so an unreadable store stays -1, never "none").
        var guard = Regex.Match(
            executable,
            @"Get-CertCount -StoreName 'My' -Location 'CurrentUser'.*?" +
            @"if \(\$existingKeys -gt 0 -and -not \$Force\) \{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        Assert.True(guard.Success, "the duplicate-key guard was not found");
        Assert.True(guard.Index < creation, "the duplicate-key guard must run before the key is created");
        Assert.Contains("exit 1", guard.Groups["body"].Value);
    }

    /// <summary>
    /// <c>-Trust</c> binds trust to a thumbprint, not to a subject name and a file path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CN=NOBA-DEV-ONLY</c> is a string anyone can self-sign, and <c>artifacts\dev-certificate\</c>
    /// is a build-output folder inside a checkout. A trust step that accepts "a readable certificate
    /// with the right subject at that path" makes whoever can write there a sideload publisher for
    /// every account on the machine. The thumbprint the create step printed is the only thing the
    /// administrator can carry across that is not forgeable by writing a file.
    /// </para>
    /// <para>
    /// Both refusals - no thumbprint given, wrong thumbprint given - must sit inside the trust branch
    /// and BEFORE <c>$trusted.Add($incoming)</c>, and each must <c>exit 1</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Trust_is_bound_to_the_thumbprint_the_create_step_printed()
    {
        var code = Read(CertScript);
        Assert.Contains("[string]$Thumbprint", code);

        var executable = StripComments(code);
        var trustBranch = Regex.Match(
            executable,
            @"if \(\$Trust\) \{(?<before>.*?)\$trusted\.Add\(\$incoming\)",
            RegexOptions.Singleline);
        Assert.True(trustBranch.Success, "the -Trust branch (up to the Add) was not found");
        var before = trustBranch.Groups["before"].Value;

        var missing = Regex.Match(
            before,
            @"if \(\[string\]::IsNullOrWhiteSpace\(\$Thumbprint\)\) \{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        Assert.True(missing.Success, "-Trust must refuse when no -Thumbprint is given");
        Assert.Contains("exit 1", missing.Groups["body"].Value);

        var mismatch = Regex.Match(
            before,
            @"if \(\$expectedThumbprint -ne \$incomingThumbprint\) \{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        Assert.True(mismatch.Success, "-Trust must refuse a certificate whose thumbprint differs");
        Assert.Contains("exit 1", mismatch.Groups["body"].Value);

        // Both sides are normalised the same way, so a thumbprint pasted with spaces or colons from
        // certmgr compares equal to the store's bare hex.
        Assert.Contains("$expectedThumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()", before);
        Assert.Contains("$incomingThumbprint = ($incoming.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()", before);

        // The create step hands the operator the exact command, thumbprint included, and the help
        // shows the same shape - the requirement is not a surprise found on the elevated side.
        Assert.Contains("-Trust -Thumbprint $($cert.Thumbprint)", code);
        Assert.Contains(@".\scripts\dev-certificate.ps1 -Trust -Thumbprint <thumbprint>", code);
    }

    /// <summary>
    /// Both halves verify by THUMBPRINT, not by count. "A certificate with this subject is trusted"
    /// and "the certificate that will sign is trusted" are different statements, and only the second
    /// one predicts whether <c>Add-AppxPackage</c> succeeds - it fails 0x800B0109 for a signature
    /// that signtool produced happily.
    /// </summary>
    [Fact]
    public void The_trusted_certificate_is_matched_by_thumbprint_not_by_count()
    {
        Assert.Contains("$_.Thumbprint -eq $incoming.Thumbprint", Read(CertScript));

        var register = Read(RegisterScript);
        Assert.Contains("$_.Thumbprint -eq $cert.Thumbprint", register);
        Assert.Contains("is NOT the one that is trusted", register);
    }

    /// <summary>
    /// A removal that cannot open a machine store must not call it clean - but an empty store it can
    /// still READ is clean, and failing an unelevated cleanup over one would teach the operator to
    /// elevate everything, which is how the key ended up in the wrong account.
    /// </summary>
    [Fact]
    public void Removal_separates_cannot_write_from_cannot_look()
    {
        var code = Read(CertScript);

        Assert.Contains("nothing to remove (read-only view)", code);
        Assert.Contains("not removed - rerun elevated", code);
        Assert.Contains("state UNKNOWN", code);
    }

    /// <summary>
    /// Every store reading names the identity it was taken under. "CurrentUser\My" is a different
    /// store for each account, so a transcript without the owner cannot be compared with another.
    /// </summary>
    [Fact]
    public void Store_readings_name_the_account_they_were_taken_under()
    {
        var code = Read(CertScript);

        Assert.Contains("WindowsIdentity]::GetCurrent().Name", code);
        Assert.Contains("running as:", code);
    }

    /// <summary>
    /// Cleanup keeps looking in <c>LocalMachine\My</c> even though nothing writes there any more.
    /// Machines that ran the earlier version have a DEV-ONLY key in it, and a cleanup that stops
    /// looking at a store the moment it stops writing to it is how residue outlives its tool.
    /// </summary>
    [Fact]
    public void Cleanup_still_looks_where_the_old_key_was_created()
    {
        var code = Read(CertScript);

        var storeTable = Regex.Match(code, @"\$Stores\s*=\s*@\((.*?)\n\)", RegexOptions.Singleline);
        Assert.True(storeTable.Success, "the store table was not found");

        var table = storeTable.Groups[1].Value;
        Assert.Contains("'TrustedPeople'; Location = 'LocalMachine'", table);
        Assert.Contains("'My';            Location = 'LocalMachine'", table);
        Assert.Contains("'My';            Location = 'CurrentUser'", table);
    }

    /// <summary>
    /// The elevation boundary is what makes the user store the only workable one, so it is asserted
    /// here too rather than only next to the sideloading rules: a future <c>/sm</c> would need
    /// elevation, and this line says elevation is not available.
    /// </summary>
    [Fact]
    public void The_register_script_still_refuses_elevation()
    {
        Assert.Contains("must NOT run elevated", Read(RegisterScript));
    }

    /// <summary>
    /// Windows PowerShell 5.1 decodes a BOM-less file as the machine's ANSI codepage, where bytes
    /// 0x91-0x94 are quote characters that silently open strings (DELTA section 13).
    /// </summary>
    [Theory]
    [InlineData(CertScript)]
    [InlineData(RegisterScript)]
    public void The_edited_scripts_survive_windows_powershell_5_1(string name)
    {
        var bytes = File.ReadAllBytes(ScriptPath(name));

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var nonAscii = bytes.Count(b => b > 0x7F);

        Assert.True(hasBom || nonAscii == 0, $"{name} has {nonAscii} non-ASCII byte(s) and no BOM.");
    }

    private static string Read(string script) => File.ReadAllText(ScriptPath(script));

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
