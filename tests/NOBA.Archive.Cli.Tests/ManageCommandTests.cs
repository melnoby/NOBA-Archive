using System.Text.Json;
using NOBA.Archive.Cli;
using NOBA.Archive.Cli.Commands;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Cli.Tests;

/// <summary>
/// The CLI surface for DROP 03 — Archive Management. <c>ManageArchiveHandler</c> itself (Application
/// layer) already exists, is already registered, and is already exercised end to end by
/// <c>tests/NOBA.Archive.Integration.Tests/ManageArchiveTests.cs</c> (MAN-001..010) and by the WinUI
/// app. What these tests cover is the one thing that handler never had: a command line in front of it.
/// </summary>
public sealed class ManageCommandTests
{
    private static async Task<(ExitCode Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var app = new CliApplication(stdout, stderr);

        var code = await app.RunAsync(args, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Manage_with_no_archive_is_a_usage_error()
    {
        var (code, _, stderr) = await RunAsync("manage");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("requires an archive path", stderr);
    }

    [Fact]
    public async Task Manage_with_nothing_to_do_is_a_usage_error()
    {
        using var temp = new TempDirectory("cli-manage-nothing");
        var source = temp.Combine("a.txt");
        await File.WriteAllTextAsync(source, "alpha");
        var archive = temp.Combine("out.zip");

        var (created, _, createError) = await RunAsync("create", archive, source, "--format", "zip");
        Assert.Equal(ExitCode.Success, created);
        Assert.True(File.Exists(archive), createError);

        var (code, _, stderr) = await RunAsync("manage", archive);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("at least one of", stderr);
    }

    [Fact]
    public async Task An_unparsable_remove_path_is_a_usage_error_naming_the_path()
    {
        using var temp = new TempDirectory("cli-manage-bad-remove");
        var source = temp.Combine("a.txt");
        await File.WriteAllTextAsync(source, "alpha");
        var archive = temp.Combine("out.zip");

        await RunAsync("create", archive, source, "--format", "zip");

        var (code, _, stderr) = await RunAsync("manage", archive, "--remove", "../escape.txt");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("../escape.txt", stderr);
    }

    [Fact]
    public async Task A_malformed_rename_pair_is_a_usage_error()
    {
        using var temp = new TempDirectory("cli-manage-bad-rename");
        var source = temp.Combine("a.txt");
        await File.WriteAllTextAsync(source, "alpha");
        var archive = temp.Combine("out.zip");

        await RunAsync("create", archive, source, "--format", "zip");

        var (code, _, stderr) = await RunAsync("manage", archive, "--rename", "a.txt-no-equals-sign");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("is not <from>=<to>", stderr);
    }

    /// <summary>End to end through the CLI alone: add, remove and rename in the same invocation.</summary>
    [Fact]
    public async Task Add_remove_and_rename_together_produce_the_expected_tree()
    {
        using var temp = new TempDirectory("cli-manage-roundtrip");
        var a = temp.Combine("a.txt");
        var b = temp.Combine("b.txt");
        await File.WriteAllTextAsync(a, "one");
        await File.WriteAllTextAsync(b, "two");

        var archive = temp.Combine("out.zip");
        var (created, _, createError) = await RunAsync("create", archive, a, b, "--format", "zip");
        Assert.Equal(ExitCode.Success, created);
        Assert.True(File.Exists(archive), createError);

        var c = temp.Combine("c.txt");
        await File.WriteAllTextAsync(c, "three");

        var (code, stdout, stderr) = await RunAsync(
            "manage", archive, c,
            "--remove", "a.txt",
            "--rename", "b.txt=renamed/b.txt",
            "--json");

        Assert.True(code == ExitCode.Success, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(0, root.GetProperty("entriesKept").GetInt64());
        Assert.Equal(1, root.GetProperty("entriesRemoved").GetInt64());
        Assert.Equal(1, root.GetProperty("entriesRenamed").GetInt64());
        Assert.Equal(1, root.GetProperty("entriesAdded").GetInt64());

        var (listCode, listStdout, _) = await RunAsync("list", archive, "--json");
        Assert.Equal(ExitCode.Success, listCode);

        using var listing = JsonDocument.Parse(listStdout);
        var paths = listing.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(e => e.GetProperty("path").GetString())
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["c.txt", "renamed/b.txt"], paths);
    }

    [Fact]
    public async Task Manage_reports_the_documented_exit_code_for_a_missing_archive()
    {
        using var temp = new TempDirectory("cli-manage-missing");

        var (code, _, _) = await RunAsync("manage", temp.Combine("nope.zip"), "--remove", "a.txt");

        Assert.NotEqual(ExitCode.Success, code);
        Assert.NotEqual(ExitCode.UsageError, code);
    }
}
