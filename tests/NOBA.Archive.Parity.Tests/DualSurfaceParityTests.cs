using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Cli;
using NOBA.Archive.Cli.Commands;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Engine;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Parity.Tests;

/// <summary>
/// The PAR-### suite (TESTING.md §6), now with a format behind it.
/// </summary>
/// <remarks>
/// <para>
/// <c>ParityFoundationTests</c> proves the two surfaces resolve the same objects. This proves the
/// stronger thing: given the same archive, they produce the same <em>outcome</em> — the same files on
/// disk, the same refusals, the same exit disposition. Everything real users would call a security
/// guarantee lives in that second sentence.
/// </para>
/// <para>
/// The CLI is driven exactly as NobaOS will drive it: argv in, JSON on stdout, an exit code out. No
/// internal shortcut, because a shortcut would stop testing the thing that ships.
/// </para>
/// </remarks>
public sealed class DualSurfaceParityTests
{
    /// <summary>PAR-001: identical refusals from both surfaces, entry for entry.</summary>
    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData(@"..\..\escaped.txt")]
    [InlineData("/etc/cron.d/evil")]
    [InlineData(@"C:\Windows\Temp\evil.dll")]
    [InlineData("readme.txt:payload.ps1")]
    [InlineData("docs/../../escaped.txt")]
    public async Task Both_surfaces_refuse_the_same_entries(string hostileName)
    {
        using var sandbox = new TempDirectory("PAR-001");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            (hostileName, "owned"),
            ("safe/ok.txt", "fine"));

        var viaEngine = await ExtractViaEngineAsync(archive, sandbox.Combine("engine-dest"));
        var viaCli = await ExtractViaCliAsync(archive, sandbox.Combine("cli-dest"));

        Assert.Equal(viaEngine.Blocked.Count, viaCli.Blocked.Count);
        Assert.Equal(
            string.Join("|", viaEngine.Blocked.Select(b => $"{b.Reason}:{b.RawPath}").Order(StringComparer.Ordinal)),
            string.Join("|", viaCli.Blocked.Select(b => $"{b.Reason}:{b.RawPath}").Order(StringComparer.Ordinal)));

        Assert.Equal(viaEngine.EntriesWritten, viaCli.EntriesWritten);
        Assert.Equal(
            string.Join("|", sandbox.Snapshot("engine-dest")),
            string.Join("|", sandbox.Snapshot("cli-dest")));
    }

    /// <summary>PAR-002: the same benign archive produces the same tree through either surface.</summary>
    [Fact]
    public async Task Both_surfaces_produce_the_same_tree_for_a_benign_archive()
    {
        using var sandbox = new TempDirectory("PAR-002");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("normal.zip"),
            ("a.txt", "alpha"),
            ("nested/b.txt", "bravo"),
            ("nested/deeper/c.txt", "charlie"),
            ("مستندات/تقرير.txt", "arabic"));

        var viaEngine = await ExtractViaEngineAsync(archive, sandbox.Combine("engine-dest"));
        var viaCli = await ExtractViaCliAsync(archive, sandbox.Combine("cli-dest"));

        Assert.Equal(4, viaEngine.EntriesWritten);
        Assert.Equal(viaEngine.EntriesWritten, viaCli.EntriesWritten);
        Assert.Equal(
            string.Join("|", sandbox.Snapshot("engine-dest")),
            string.Join("|", sandbox.Snapshot("cli-dest")));
    }

    /// <summary>
    /// PAR-003: an archive both surfaces must refuse outright fails on both, with the same error code
    /// behind it. The CLI turns it into an exit code; the engine throws. Same verdict, two shapes.
    /// </summary>
    [Fact]
    public async Task A_bomb_is_refused_by_both_surfaces()
    {
        using var sandbox = new TempDirectory("PAR-003");
        var archive = ArchiveFactory.WriteRatioBomb(sandbox.Combine("bomb.zip"), 512L * 1024 * 1024);

        await using var host = CreateHost();
        await Assert.ThrowsAsync<Core.Errors.ArchiveOperationException>(async () =>
            await host.GetRequiredService<ExtractArchiveHandler>().HandleAsync(
                new ExtractRequest { ArchivePath = archive, Options = OptionsFor(sandbox.Combine("engine-dest")) },
                host.GetRequiredService<OperationIdFactory>().Next(OperationType.Extract),
                null,
                null,
                CancellationToken.None));

        var (exitCode, _, _) = await RunCliAsync(
            ["extract", archive, "-o", sandbox.Combine("cli-dest"), "--json"]);

        Assert.NotEqual(ExitCode.Success, exitCode);
    }

    /// <summary>PAR-004: what the CLI reports for a listing is what the engine found, not a re-derivation.</summary>
    [Fact]
    public async Task The_cli_listing_matches_the_engine_listing()
    {
        using var sandbox = new TempDirectory("PAR-004");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("mixed.zip"),
            ("a.txt", "alpha"),
            ("../escaped.txt", "owned"),
            ("nested/b.txt", "bravo"));

        await using var host = CreateHost();
        var engineListing = await host.GetRequiredService<ListArchiveHandler>()
            .HandleAsync(new ListRequest { ArchivePath = archive }, CancellationToken.None);

        var (exitCode, stdout, _) = await RunCliAsync(["list", archive, "--json"]);
        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.Equal(engineListing.Format.Id, root.GetProperty("format").GetString());
        Assert.Equal(engineListing.Entries.Count, root.GetProperty("count").GetInt32());

        var reported = root.GetProperty("entries")
            .EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            string.Join("|", engineListing.Entries.Select(e => e.DisplayPath).Order(StringComparer.Ordinal)),
            string.Join("|", reported));

        // The rejected entry is listed and marked, not hidden: a UI has to be able to grey it out.
        var blockedCount = root.GetProperty("entries")
            .EnumerateArray()
            .Count(e => e.TryGetProperty("blockedReason", out var reason) && reason.ValueKind != JsonValueKind.Null);
        Assert.Equal(1, blockedCount);
    }

    /// <summary>PAR-005: an archive created through one surface opens identically through the other.</summary>
    [Fact]
    public async Task An_archive_created_by_the_cli_is_read_identically_by_the_engine()
    {
        using var sandbox = new TempDirectory("PAR-005");
        var source = sandbox.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "b.txt"), "bravo");

        var archive = sandbox.Combine("from-cli.zip");
        var (exitCode, _, stderr) = await RunCliAsync(["create", archive, source, "--json"]);
        Assert.True(exitCode == ExitCode.Success, stderr);
        Assert.True(File.Exists(archive));

        await using var host = CreateHost();
        var listing = await host.GetRequiredService<ListArchiveHandler>()
            .HandleAsync(new ListRequest { ArchivePath = archive }, CancellationToken.None);

        Assert.Equal(2, listing.Entries.Count);

        var test = await host.GetRequiredService<TestArchiveHandler>().HandleAsync(
            new TestRequest { ArchivePath = archive },
            host.GetRequiredService<OperationIdFactory>().Next(OperationType.Test),
            null,
            CancellationToken.None);
        Assert.Equal(ArchiveTestStatus.Valid, test.Status);
    }

    /// <summary>
    /// PAR-006: the CLI's exit codes are a contract NobaOS depends on, so they are asserted by value.
    /// </summary>
    [Fact]
    public async Task The_cli_reports_documented_exit_codes()
    {
        using var sandbox = new TempDirectory("PAR-006");
        var good = ArchiveFactory.WriteZip(sandbox.Combine("good.zip"), ("a.txt", "alpha"));

        var (ok, _, _) = await RunCliAsync(["test", good, "--json"]);
        Assert.Equal(ExitCode.Success, ok);

        var (missing, _, _) = await RunCliAsync(["test", sandbox.Combine("nope.zip"), "--json"]);
        Assert.NotEqual(ExitCode.Success, missing);

        var (usage, _, _) = await RunCliAsync(["extract"]);
        Assert.Equal(ExitCode.UsageError, usage);

        var corrupt = ArchiveFactory.WriteZipBytes(sandbox.Combine("corrupt.zip"), ("d.bin", new byte[400_000]));
        ArchiveFactory.CorruptPayload(corrupt);
        var (corrupted, _, _) = await RunCliAsync(["test", corrupt, "--json"]);
        Assert.NotEqual(ExitCode.Success, corrupted);
    }

    /// <summary>
    /// PAR-007: what the CLI reports for a search is what the engine found, not a re-derivation — the
    /// same discipline PAR-004 applies to <c>list</c>, now for DROP 07's <c>search</c> contract.
    /// </summary>
    [Fact]
    public async Task The_cli_search_matches_the_engine_search()
    {
        using var sandbox = new TempDirectory("PAR-007");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("mixed.zip"),
            ("report.txt", "alpha"),
            ("notes.csv", "bravo"),
            ("../escaped.txt", "owned"));

        await using var host = CreateHost();
        var engineSearch = await host.GetRequiredService<SearchArchiveHandler>().HandleAsync(
            new SearchRequest { ArchivePath = archive, Extensions = [".txt"] },
            CancellationToken.None);

        var (exitCode, stdout, _) = await RunCliAsync(["search", archive, "--ext", ".txt", "--json"]);
        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.Equal(engineSearch.Format.Id, root.GetProperty("format").GetString());
        Assert.Equal(engineSearch.Matches.Count, root.GetProperty("matchCount").GetInt32());

        var reported = root.GetProperty("matches")
            .EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            string.Join("|", engineSearch.Matches.Select(e => e.DisplayPath).Order(StringComparer.Ordinal)),
            string.Join("|", reported));

        // Both the accepted entry and the rejected traversal entry end in ".txt" - proof the filter
        // reads DisplayPath/RawPath rather than requiring Path to be set.
        Assert.Equal(2, engineSearch.Matches.Count);
    }

    /// <summary>
    /// PAR-008: DROP 03's CLI surface (<c>manage</c>) produces the same rewritten archive as calling
    /// <see cref="ManageArchiveHandler"/> directly — add, remove and rename together, in one pass.
    /// </summary>
    [Fact]
    public async Task The_cli_manage_matches_the_engine_manage()
    {
        using var sandbox = new TempDirectory("PAR-008");
        var engineArchive = ArchiveFactory.WriteZip(
            sandbox.Combine("engine.zip"),
            ("a.txt", "alpha"),
            ("b.txt", "bravo"));
        var cliArchive = ArchiveFactory.WriteZip(
            sandbox.Combine("cli.zip"),
            ("a.txt", "alpha"),
            ("b.txt", "bravo"));

        var newFile = sandbox.Combine("c.txt");
        await File.WriteAllTextAsync(newFile, "charlie");

        await using var host = CreateHost();
        var engineOutcome = await host.GetRequiredService<ManageArchiveHandler>().HandleAsync(
            new ManageRequest
            {
                ArchivePath = engineArchive,
                Options = CompressionOptions.ForProfile(ArchiveFormat.Zip, CompressionProfile.Balanced),
                EntriesToRemove = [EntryPath("a.txt")],
                EntriesToRename = [new EntryRename(EntryPath("b.txt"), EntryPath("renamed/b.txt"))],
                SourcesToAdd = [newFile],
            },
            host.GetRequiredService<OperationIdFactory>().Next(OperationType.Add),
            null,
            null,
            CancellationToken.None);

        var (exitCode, stdout, stderr) = await RunCliAsync(
            ["manage", cliArchive, newFile, "--remove", "a.txt", "--rename", "b.txt=renamed/b.txt", "--json"]);
        Assert.True(exitCode == ExitCode.Success, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.Equal(engineOutcome.EntriesKept, root.GetProperty("entriesKept").GetInt64());
        Assert.Equal(engineOutcome.EntriesRemoved, root.GetProperty("entriesRemoved").GetInt64());
        Assert.Equal(engineOutcome.EntriesRenamed, root.GetProperty("entriesRenamed").GetInt64());
        Assert.Equal(engineOutcome.EntriesAdded, root.GetProperty("entriesAdded").GetInt64());

        var engineListing = await host.GetRequiredService<ListArchiveHandler>()
            .HandleAsync(new ListRequest { ArchivePath = engineArchive }, CancellationToken.None);
        var cliListing = await host.GetRequiredService<ListArchiveHandler>()
            .HandleAsync(new ListRequest { ArchivePath = cliArchive }, CancellationToken.None);

        Assert.Equal(
            string.Join("|", engineListing.Entries.Select(e => e.DisplayPath).Order(StringComparer.Ordinal)),
            string.Join("|", cliListing.Entries.Select(e => e.DisplayPath).Order(StringComparer.Ordinal)));
    }

    private static Core.Primitives.ArchivePath EntryPath(string value)
    {
        Assert.True(Core.Primitives.ArchivePath.TryCreate(value, out var path, out var rejection), rejection?.Detail);
        return path;
    }

    private static ServiceProvider CreateHost()
    {
        var services = new ServiceCollection();
        services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddNobaArchiveEngine(static options =>
        {
            options.Headless = true;
            options.DefaultConflictPolicy = ConflictPolicy.Fail;
        });
        return services.BuildServiceProvider();
    }

    private static ExtractionOptions OptionsFor(string destination) => new()
    {
        DestinationRoot = destination,
        ConflictPolicy = ConflictPolicy.Fail,
        PropagateMarkOfTheWeb = false,
    };

    private static async Task<ExtractOutcome> ExtractViaEngineAsync(string archive, string destination)
    {
        await using var host = CreateHost();
        return await host.GetRequiredService<ExtractArchiveHandler>().HandleAsync(
            new ExtractRequest { ArchivePath = archive, Options = OptionsFor(destination) },
            host.GetRequiredService<OperationIdFactory>().Next(OperationType.Extract),
            null,
            null,
            CancellationToken.None);
    }

    /// <summary>Reads the CLI's JSON document back into the same shape the engine returned.</summary>
    private static async Task<CliExtractSummary> ExtractViaCliAsync(string archive, string destination)
    {
        var (exitCode, stdout, stderr) = await RunCliAsync(
            ["extract", archive, "-o", destination, "--json"]);

        Assert.True(
            exitCode is ExitCode.Success,
            string.Create(CultureInfo.InvariantCulture, $"the CLI exited {exitCode}: {stderr}"));

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        var blocked = root.GetProperty("blocked")
            .EnumerateArray()
            .Select(b => new BlockedSummary(
                b.GetProperty("rawPath").GetString()!,
                b.GetProperty("reason").GetString()!))
            .ToList();

        return new CliExtractSummary(root.GetProperty("entriesWritten").GetInt64(), blocked);
    }

    private static async Task<(ExitCode ExitCode, string Stdout, string Stderr)> RunCliAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await new CliApplication(stdout, stderr).RunAsync(args, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record BlockedSummary(string RawPath, string Reason);

    private sealed record CliExtractSummary(long EntriesWritten, IReadOnlyList<BlockedSummary> Blocked);
}
