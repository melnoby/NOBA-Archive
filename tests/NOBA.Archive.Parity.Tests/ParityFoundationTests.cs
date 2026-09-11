using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Cli;
using NOBA.Archive.Cli.Commands;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Engine;
using Xunit;

namespace NOBA.Archive.Parity.Tests;

/// <summary>
/// The foundation of the PAR-### suite (TESTING.md). Phase 1 has no formats yet, so what is proved here
/// is the property everything later depends on: the CLI and an embedding host resolve the <em>same</em>
/// engine and reach the <em>same</em> security verdicts. Once ZIP lands in Phase 2, the same harness
/// runs each SEC-### archive through both surfaces and compares outcomes.
/// </summary>
public sealed class ParityFoundationTests
{
    /// <summary>Stands in for the desktop application / NobaOS: the same one-call registration.</summary>
    private static ServiceProvider CreateEmbeddingHostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddNobaArchiveEngine(static options =>
        {
            options.Headless = true;
            options.DefaultConflictPolicy = ConflictPolicy.Fail;
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// ADR-0009's "exactly one archive engine", made checkable: if the CLI ever grew its own path
    /// policy or guard, these types would diverge.
    /// </summary>
    [Fact]
    public async Task Both_surfaces_resolve_the_same_engine_implementations()
    {
        await using var host = CreateEmbeddingHostServices();
        await using var cli = CliApplication.CreateHostServices();

        AssertSameImplementation<IPathPolicy>(host, cli);
        AssertSameImplementation<IResourceGuard>(host, cli);
        AssertSameImplementation<IConflictResolutionService>(host, cli);
        AssertSameImplementation<IFormatDetector>(host, cli);
        AssertSameImplementation<IArchiveProviderRegistry>(host, cli);
        AssertSameImplementation<IFileSystem>(host, cli);
        AssertSameImplementation<ISecretRedactor>(host, cli);
    }

    /// <summary>
    /// The security property that matters: a hostile path is refused identically no matter which
    /// surface asked. A guarantee that held in the UI but not in the CLI would be a bug.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData("../../etc/passwd")]
    [InlineData("readme.txt:payload.ps1")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("docs/reports/q3.pdf")]
    [InlineData("مستندات/تقرير.pdf")]
    [InlineData("CON")]
    public async Task Both_surfaces_reach_the_same_path_verdict(string rawEntryPath)
    {
        var destination = Path.Combine(Path.GetTempPath(), "noba-parity");

        await using var host = CreateEmbeddingHostServices();
        await using var cli = CliApplication.CreateHostServices();

        var hostVerdict = host.GetRequiredService<IPathPolicy>()
            .Validate(rawEntryPath, destination, IllegalNamePolicy.Sanitize);
        var cliVerdict = cli.GetRequiredService<IPathPolicy>()
            .Validate(rawEntryPath, destination, IllegalNamePolicy.Sanitize);

        Assert.Equal(hostVerdict.IsAccepted, cliVerdict.IsAccepted);
        Assert.Equal(hostVerdict.Rejection?.Reason, cliVerdict.Rejection?.Reason);
        Assert.Equal(hostVerdict.Path?.FullPath, cliVerdict.Path?.FullPath);
        Assert.Equal(hostVerdict.WasSanitized, cliVerdict.WasSanitized);
    }

    /// <summary>What the CLI prints must be what the engine actually reports - not a parallel list.</summary>
    [Fact]
    public async Task The_cli_format_report_matches_the_engine_registry()
    {
        await using var host = CreateEmbeddingHostServices();
        var expected = host.GetRequiredService<IArchiveProviderRegistry>()
            .SupportedFormats
            .Select(static f => f.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await new CliApplication(stdout, stderr)
            .RunAsync(["formats", "--json"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exitCode);

        using var document = JsonDocument.Parse(stdout.ToString());
        var reported = document.RootElement.GetProperty("formats")
            .EnumerateArray()
            .Select(static e => e.GetProperty("id").GetString()!)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.Count, reported.Count);
        Assert.Equal(string.Join(",", expected), string.Join(",", reported));
    }

    /// <summary>
    /// Both surfaces are headless, and a headless host that could still ask a human would deadlock the
    /// moment NobaOS drove it.
    /// </summary>
    [Fact]
    public async Task Both_surfaces_are_configured_to_never_prompt()
    {
        await using var host = CreateEmbeddingHostServices();
        await using var cli = CliApplication.CreateHostServices();

        Assert.True(host.GetRequiredService<NobaArchiveEngineOptions>().Headless);
        Assert.True(cli.GetRequiredService<NobaArchiveEngineOptions>().Headless);
        Assert.NotNull(cli.GetService<IConflictPrompt>());
        Assert.NotNull(cli.GetService<IPasswordProvider>());
    }

    /// <summary>A headless host asking for Ask is a configuration error, caught before anything runs.</summary>
    [Fact]
    public void A_headless_host_cannot_be_configured_to_prompt()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        Assert.Throws<InvalidOperationException>(() => services.AddNobaArchiveEngine(static options =>
        {
            options.Headless = true;
            options.DefaultConflictPolicy = ConflictPolicy.Ask;
        }));
    }

    private static void AssertSameImplementation<TService>(IServiceProvider host, IServiceProvider cli)
        where TService : class
    {
        var hostType = host.GetRequiredService<TService>().GetType();
        var cliType = cli.GetRequiredService<TService>().GetType();

        Assert.Equal(hostType.FullName, cliType.FullName);
        Assert.Equal(hostType.Assembly.GetName().Name, cliType.Assembly.GetName().Name);
    }
}
