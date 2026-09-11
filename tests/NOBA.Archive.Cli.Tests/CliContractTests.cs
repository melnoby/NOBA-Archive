using System.Text.Json;
using NOBA.Archive.Cli;
using NOBA.Archive.Cli.Commands;
using NOBA.Archive.Core.Errors;
using Xunit;

namespace NOBA.Archive.Cli.Tests;

/// <summary>
/// The CLI's contract with NobaOS: stable exit codes, a clean stdout in JSON mode, and a password
/// channel that never runs through argv.
/// </summary>
public sealed class CliContractTests
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
    public async Task Help_succeeds_and_documents_usage()
    {
        var (code, stdout, _) = await RunAsync("help");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Usage:", stdout);
        Assert.Contains("noba-ar <command>", stdout);
    }

    [Fact]
    public async Task No_arguments_shows_help_rather_than_failing()
    {
        var (code, stdout, _) = await RunAsync();

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("Usage:", stdout);
    }

    [Fact]
    public async Task Unknown_command_is_a_usage_error()
    {
        var (code, _, stderr) = await RunAsync("frobnicate");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("Unknown command", stderr);
    }

    [Fact]
    public async Task Version_reports_the_product_and_runtime()
    {
        var (code, stdout, _) = await RunAsync("version");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("NOBA Archive", stdout);
    }

    /// <summary>In JSON mode stdout carries exactly one document and nothing else, so a pipe stays clean.</summary>
    [Fact]
    public async Task Json_mode_writes_only_json_to_stdout()
    {
        var (code, stdout, stderr) = await RunAsync("formats", "--json");

        Assert.Equal(ExitCode.Success, code);

        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.TryGetProperty("formats", out var formats));
        Assert.Equal(JsonValueKind.Array, formats.ValueKind);

        // Human-facing narration must not contaminate the machine channel.
        Assert.DoesNotContain("format(s)", stdout);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public async Task Version_json_is_machine_readable()
    {
        var (_, stdout, _) = await RunAsync("version", "--json");

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("NOBA Archive", document.RootElement.GetProperty("product").GetString());
        Assert.NotNull(document.RootElement.GetProperty("version").GetString());
    }

    /// <summary>
    /// CLI-026: the version output names the source drop it was built from.
    /// </summary>
    /// <remarks>
    /// Not decoration. A validation transcript that reports PASS on every phase is indistinguishable
    /// from one produced by a source tree that predates the change under test — which has happened
    /// twice, both times costing a round trip and nearly costing a false conclusion. This is the only
    /// line in the output that answers "which source was this".
    /// </remarks>
    [Fact]
    public async Task Version_names_the_source_drop()
    {
        var (_, stdout, _) = await RunAsync("version");
        Assert.Contains("Source drop:", stdout);

        var (_, json, _) = await RunAsync("version", "--json");
        using var document = JsonDocument.Parse(json);
        var drop = document.RootElement.GetProperty("sourceDrop").GetString();
        Assert.False(string.IsNullOrWhiteSpace(drop), "the source drop must not be blank");
    }

    /// <summary>
    /// Phase 1 registers no providers, so the honest answer is an empty list. A build that invented
    /// entries here would be claiming support it does not have.
    /// </summary>
    [Fact]
    public async Task Formats_reports_only_what_this_build_actually_supports()
    {
        var (_, stdout, _) = await RunAsync("formats", "--json");

        using var document = JsonDocument.Parse(stdout);
        var formats = document.RootElement.GetProperty("formats").EnumerateArray().ToList();

        // The set, not the count, and stated as the rule rather than as a list of allowed strings.
        // This test failed the moment Phase 3 wired up the native backend and again when DROP 05 added
        // TAR and GZIP, which is exactly what it is for - but enumerating the permitted answers meant
        // editing it each time. The rule is the durable thing:
        //
        //   managed providers need nothing, so they are always present;
        //   native ones need 7z.dll, so they may legitimately be absent;
        //   nothing else may appear at all.
        var ids = formats.Select(f => f.GetProperty("id").GetString()).Order(StringComparer.Ordinal).ToList();
        var joined = string.Join(",", ids);

        string[] alwaysAvailable = ["gzip", "tar", "zip"];
        string[] needsNativeLibrary = ["7z", "rar"];

        foreach (var managed in alwaysAvailable)
        {
            Assert.True(
                ids.Contains(managed),
                $"'{managed}' has no native dependency and must always be reported. Got '{joined}'.");
        }

        foreach (var id in ids)
        {
            Assert.True(
                alwaysAvailable.Contains(id) || needsNativeLibrary.Contains(id),
                $"'{id}' is advertised by no known provider. Got '{joined}'.");
        }

        // And the native pair is all-or-nothing: they come from one backend.
        Assert.Equal(
            ids.Contains("7z"),
            ids.Contains("rar"));

        var zip = formats.Single(f => f.GetProperty("id").GetString() == "zip").GetProperty("capabilities")
            .EnumerateArray()
            .Select(c => c.GetString())
            .ToList();

        Assert.Contains(zip, c => string.Equals(c, "Extract", StringComparison.OrdinalIgnoreCase));

        // The managed backend cannot encrypt, so the CLI must not advertise that it can.
        Assert.DoesNotContain(zip, c => string.Equals(c, "Encrypt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Help_documents_the_password_channels_and_rules_out_argv()
    {
        var (_, stdout, _) = await RunAsync("help");

        Assert.Contains("--password-stdin", stdout);
        Assert.Contains("never accepted as a command-line argument", stdout);
    }

    /// <summary>
    /// A password must not be expressible as an option value: on Windows another process running as the
    /// same user can read a process's command line.
    /// </summary>
    [Fact]
    public void The_parser_does_not_accept_a_password_as_an_option_value()
    {
        Assert.True(CommandLineParser.TryParse(["extract", "--password", "hunter2"], out var parsed, out _));

        Assert.NotNull(parsed);
        Assert.Null(parsed.Option("password"));
        Assert.Contains("hunter2", string.Join(",", parsed.Positional));
    }

    [Fact]
    public void The_parser_reports_a_missing_value_instead_of_guessing()
    {
        var ok = CommandLineParser.TryParse(["extract", "archive.zip", "-o"], out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("requires a value", error);
    }

    [Theory]
    [InlineData("--json", true)]
    [InlineData("--help", true)]
    public void The_parser_recognises_flags(string flag, bool expected)
    {
        Assert.True(CommandLineParser.TryParse(["formats", flag], out var parsed, out _));

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.HasFlag(flag.TrimStart('-')));
    }

    [Fact]
    public void The_parser_supports_equals_syntax()
    {
        Assert.True(CommandLineParser.TryParse(["extract", "--conflict=rename"], out var parsed, out _));

        Assert.NotNull(parsed);
        Assert.Equal("rename", parsed.Option("conflict"));
    }
}

/// <summary>Exit codes are a versioned contract; every error must land on a defined one.</summary>
public sealed class ExitCodeContractTests
{
    [Theory]
    [InlineData(ArchiveErrorCode.None, ExitCode.Success)]
    [InlineData(ArchiveErrorCode.PasswordRequired, ExitCode.PasswordRequired)]
    [InlineData(ArchiveErrorCode.WrongPassword, ExitCode.PasswordRequired)]
    [InlineData(ArchiveErrorCode.CorruptData, ExitCode.CorruptArchive)]
    [InlineData(ArchiveErrorCode.UnsupportedOperation, ExitCode.UnsupportedOperation)]
    [InlineData(ArchiveErrorCode.PathRejected, ExitCode.BlockedBySecurityPolicy)]
    [InlineData(ArchiveErrorCode.BombSuspected, ExitCode.BlockedBySecurityPolicy)]
    [InlineData(ArchiveErrorCode.DiskFull, ExitCode.InsufficientDiskSpace)]
    [InlineData(ArchiveErrorCode.Cancelled, ExitCode.Cancelled)]
    [InlineData(ArchiveErrorCode.IoFailure, ExitCode.IoFailure)]
    [InlineData(ArchiveErrorCode.BackendFailure, ExitCode.BackendFailure)]
    public void Maps_each_error_to_its_documented_exit_code(ArchiveErrorCode error, ExitCode expected)
    {
        Assert.Equal(expected, ExitCodeMapper.FromError(error));
    }

    [Fact]
    public void Every_error_code_has_a_mapping()
    {
        foreach (var code in Enum.GetValues<ArchiveErrorCode>())
        {
            var exitCode = ExitCodeMapper.FromError(code);

            Assert.True(
                Enum.IsDefined(exitCode),
                $"{code} maps to an undefined exit code.");
            if (code != ArchiveErrorCode.None)
            {
                Assert.NotEqual(ExitCode.Success, exitCode);
            }
        }
    }
}
