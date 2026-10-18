using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Cli.Commands;
using NOBA.Archive.Infrastructure.Logging;
using NOBA.Archive.Security;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Cli.Tests;

/// <summary>
/// SEC-024. <c>RedactionTests.cs</c> (Infrastructure.Tests) already proves
/// <see cref="RedactingLoggerProvider"/> redacts in isolation. What it cannot prove is that the real
/// application ever calls it: until this drop, <see cref="CliApplication.CreateHostServices"/> called
/// <c>services.AddLogging(...)</c> with no <c>AddRedaction</c> — the isolated test was green while
/// the actual composition root logged whatever a sink was pointed at verbatim. This drives the real
/// static factory, not a hand-built substitute, which is the only way a regression here would ever
/// be caught again.
/// </summary>
public sealed class CompositionRootRedactionTests
{
    private const string Secret = "correct-horse-battery-staple";

    [Fact]
    public async Task The_real_cli_composition_root_redacts_a_sink_it_did_not_know_about()
    {
        var sink = new RecordingLoggerProvider();

        await using var services = CliApplication.CreateHostServices(
            configureLogging: builder => builder.Services.AddSingleton<ILoggerProvider>(sink));

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("composition-root-test");

        // CreateHostServices sets the minimum level to Warning (the CLI is headless and should not
        // chatter) - Information would be filtered before it ever reached the sink, which would
        // make this pass for the wrong reason.
        logger.LogWarning("Opening archive with password={Password}", Secret);

        Assert.NotEmpty(sink.Lines);
        Assert.DoesNotContain(Secret, sink.AllText);
        Assert.Contains(SecretRedactor.Mask, sink.AllText);
    }

    /// <summary>
    /// Any <see cref="ILoggerProvider"/> a future change registers - with or without
    /// <paramref name="configureLogging"/> - still comes out wrapped, proving the wrapping happens
    /// unconditionally on the always-run path rather than only when a test supplies its own sink.
    /// </summary>
    [Fact]
    public async Task Every_registered_provider_comes_out_wrapped_regardless_of_who_registered_it()
    {
        var sinkA = new RecordingLoggerProvider();
        var sinkB = new RecordingLoggerProvider();

        await using var services = CliApplication.CreateHostServices(configureLogging: builder =>
        {
            builder.Services.AddSingleton<ILoggerProvider>(sinkA);
            builder.Services.AddSingleton<ILoggerProvider>(sinkB);
        });

        var providers = services.GetServices<ILoggerProvider>().ToList();

        Assert.NotEmpty(providers);
        Assert.All(providers, p => Assert.IsType<RedactingLoggerProvider>(p));
    }
}
