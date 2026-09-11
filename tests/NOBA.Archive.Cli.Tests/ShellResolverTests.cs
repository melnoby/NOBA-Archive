using NOBA.Archive.Cli.Commands;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Cli.Tests;

/// <summary>
/// The resolver verb — the half of the shell contract the registry cannot express.
/// </summary>
/// <remarks>
/// <para>
/// A classic registry command substitutes the selection and nothing else. "Extract here" needs the
/// folder the archive sits in and "Compress to 7Z" needs a name derived from what was selected;
/// neither is a substitution. The previous registration approximated both and got both wrong — D2 and
/// D3 — so the registration now invokes <c>noba-ar shell &lt;entry-id&gt; &lt;paths…&gt;</c> and the
/// computing happens here, through the same <see cref="ShellMenuBuilder"/> and
/// <see cref="ShellLauncher"/> the model tests exercise.
/// </para>
/// <para>
/// The other thing these hold is the mechanism's honest limit. A registry key is present or absent
/// and cannot be greyed, so an entry Windows offers may still be one the capability model withholds
/// for this particular selection — three files chosen for GZIP, an archive verb reached on something
/// that is not an archive. Those must end as a refusal naming the rule, never as an operation that
/// starts and fails.
/// </para>
/// <para>
/// <b>SHELL-066…071.</b> Allocated after SHELL-065 rather than squeezed into the SHELL-054…065 range,
/// which is spoken for. The resolver is the mechanism Phase 2 needed to close D2 and D3 without
/// hardcoding registry command strings; these are its inventory entries in
/// <c>docs/DROP08_REAUDIT.md</c> §14 and <c>docs/PRODUCT_PLAN.md</c>.
/// </para>
/// </remarks>
public sealed class ShellResolverTests
{
    private static async Task<(ExitCode Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        var code = await app.RunAsync(args, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Verb => ShellRegistration.ResolverVerb;

    /// <summary>
    /// SHELL-066 — D3, end to end: "Extract here" lands beside the archive, not in the working
    /// directory.
    /// </summary>
    /// <remarks>
    /// The whole defect in one assertion. A shell-launched process inherits a working directory the
    /// user never chose, so an extract that omits its destination does not mean "here". This runs the
    /// entry the registry actually invokes and looks for the file where the person clicked.
    /// </remarks>
    [Fact]
    public async Task Extract_here_puts_the_contents_beside_the_archive()
    {
        using var temp = new TempDirectory("shell-extract-here");

        var source = temp.Combine("payload.txt");
        await File.WriteAllTextAsync(source, "noba");

        var archive = temp.Combine("bundle.zip");
        var (created, _, createError) = await RunAsync("create", archive, source, "--format", "zip");
        Assert.Equal(ExitCode.Success, created);
        Assert.True(File.Exists(archive), createError);

        // Removed so that its reappearance is the evidence, and so the extraction is not a conflict
        // against the file it was made from.
        File.Delete(source);

        // The process working directory is elsewhere: if the destination were inherited rather than
        // carried, the payload would land there instead of beside the archive (D3).
        var (code, _, stderr) = await RunAsync(Verb, "noba.extracthere", archive);

        Assert.Equal(ExitCode.Success, code);
        Assert.True(
            File.Exists(temp.Combine("payload.txt")),
            $"Extract here did not write beside the archive (D3). stderr: {stderr}");
    }

    /// <summary>
    /// SHELL-067 — the single-member rule reaches the boundary the registry could not express.
    /// </summary>
    /// <remarks>
    /// GZIP is registered on files because a single file is a legitimate GZIP, and the classic
    /// registry has no way to say "only when one is selected". So the refusal happens here, from the
    /// capability model, with the reason attached — rather than the engine failing halfway through.
    /// </remarks>
    [Fact]
    public async Task Compressing_several_files_to_a_single_member_format_is_refused_with_its_reason()
    {
        using var temp = new TempDirectory("shell-gzip-many");

        var first = temp.Combine("a.txt");
        var second = temp.Combine("b.txt");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "b");

        var (code, _, stderr) = await RunAsync(Verb, "noba.compress.gzip", first, second);

        Assert.Equal(ExitCode.UnsupportedOperation, code);
        Assert.True(
            stderr.Contains("single file", StringComparison.OrdinalIgnoreCase),
            $"the refusal must name the single-member rule; stderr was: {stderr}");

        // Nothing was written on the way to refusing.
        Assert.False(File.Exists(temp.Combine("a.gz")));
        Assert.False(File.Exists(temp.Combine("b.gz")));
    }

    /// <summary>
    /// SHELL-068 — the same selection compresses cleanly to a format that holds many, so the refusal
    /// above is the rule and not the resolver failing at everything.
    /// </summary>
    [Fact]
    public async Task Compressing_several_files_to_a_multi_member_format_produces_one_archive()
    {
        using var temp = new TempDirectory("shell-zip-many");

        var first = temp.Combine("a.txt");
        var second = temp.Combine("b.txt");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "b");

        var (code, _, stderr) = await RunAsync(Verb, "noba.compress.zip", first, second);

        Assert.Equal(ExitCode.Success, code);

        // One archive, named for the folder the selection sits in - D4's "one request, not N".
        var produced = Directory.GetFiles(temp.Path, "*.zip");
        Assert.True(produced.Length == 1, $"expected one archive, found {produced.Length}. stderr: {stderr}");
    }

    /// <summary>
    /// SHELL-069 — an archive verb reached on something that is not an archive refuses rather than
    /// tries.
    /// </summary>
    [Fact]
    public async Task An_archive_verb_on_a_plain_file_is_refused()
    {
        using var temp = new TempDirectory("shell-not-an-archive");

        var plain = temp.Combine("notes.txt");
        await File.WriteAllTextAsync(plain, "not an archive");

        var (code, _, stderr) = await RunAsync(Verb, "noba.test", plain);

        Assert.Equal(ExitCode.UnsupportedOperation, code);
        Assert.True(
            stderr.Contains("not available", StringComparison.OrdinalIgnoreCase),
            $"the refusal must say the action is unavailable; stderr was: {stderr}");
    }

    /// <summary>
    /// SHELL-070 — an entry id this build does not register is a usage error, not a silent no-op.
    /// </summary>
    [Fact]
    public async Task An_unknown_entry_id_is_a_usage_error()
    {
        var (code, _, stderr) = await RunAsync(Verb, "noba.does.not.exist", @"C:\Docs\a.txt");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.True(
            stderr.Contains("not a registered shell entry", StringComparison.OrdinalIgnoreCase),
            $"an unknown entry id must say so; stderr was: {stderr}");
    }

    /// <summary>
    /// SHELL-073 — a selection that did not survive the shell's substitution fails safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registration writes <c>%*</c> for entries that take the whole selection, and what Explorer
    /// actually delivers there has not been observed on Windows from this environment (SHELL-L8). So
    /// the question this holds is not "does <c>%*</c> work" but "what happens when it does not": one
    /// argument holding every path run together, or no arguments at all.
    /// </para>
    /// <para>
    /// Both must end with nothing written. The refusal comes from the engine's own path validation
    /// rather than from a second existence check here — one authority decides whether an input is
    /// usable, and the resolver's <c>Directory.Exists</c> call classifies items, it does not vet them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_selection_that_did_not_survive_substitution_writes_nothing()
    {
        using var temp = new TempDirectory("shell-bad-selection");

        var first = temp.Combine("a.txt");
        var second = temp.Combine("b.txt");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "b");

        // A %* that arrived as one argument instead of several.
        var (joined, _, _) = await RunAsync(Verb, "noba.compress.zip", $"{first} {second}");
        Assert.NotEqual(ExitCode.Success, joined);

        // A %* that expanded to nothing at all.
        var (empty, _, _) = await RunAsync(Verb, "noba.compress.zip");
        Assert.Equal(ExitCode.UsageError, empty);

        // A path that is simply not there.
        var (missing, _, _) = await RunAsync(Verb, "noba.compress.zip", temp.Combine("gone.txt"));
        Assert.NotEqual(ExitCode.Success, missing);

        // The point of the test: no archive, and no partial file, from any of the three.
        Assert.Empty(Directory.GetFiles(temp.Path, "*.zip"));
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length);
    }

    /// <summary>
    /// SHELL-071 — the verb needs an entry and a selection; neither is inferred.
    /// </summary>
    [Fact]
    public async Task The_resolver_requires_an_entry_and_a_selection()
    {
        var (noArgs, _, _) = await RunAsync(Verb);
        Assert.Equal(ExitCode.UsageError, noArgs);

        var (noPaths, _, stderr) = await RunAsync(Verb, "noba.extracthere");
        Assert.Equal(ExitCode.UsageError, noPaths);
        Assert.True(
            stderr.Contains("entry id", StringComparison.OrdinalIgnoreCase),
            $"the usage message must name what is missing; stderr was: {stderr}");
    }
}
