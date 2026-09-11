using Microsoft.Extensions.DependencyInjection;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Engine;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The desktop host's startup contract: <c>DHC-001…004</c>.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a real failure, and a humiliating one: the WinUI app compiled, launched, and
/// vanished before drawing a single pixel. <c>AddNobaArchiveEngine</c> registers
/// <see cref="IPasswordProvider"/> and <see cref="IConflictPrompt"/> only when the host is headless —
/// a deliberate refusal, since no library can invent a way to ask a person a question — and the desktop
/// app never supplied its half. The container threw inside <c>OnLaunched</c>, the exception was
/// unhandled, and the process exited with no window and no message.
/// </para>
/// <para>
/// Four hundred and thirty-five tests were green at the time. Every one of them composed the engine the
/// way the CLI composes it, with <c>Headless = true</c>, so the interactive graph had never once been
/// built. That is the gap these tests close: the failure is now a red test on Linux in milliseconds
/// rather than a window that does not appear on Windows.
/// </para>
/// </remarks>
public sealed class DesktopHostCompositionTests
{
    /// <summary>
    /// The services a desktop workspace resolves. Mirrors <c>EngineWorkspace</c>'s constructor, which
    /// lives in the WinUI project and therefore cannot be referenced from a test that runs anywhere.
    /// </summary>
    private static readonly Type[] DesktopWorkspaceDependencies =
    [
        typeof(IArchiveProviderRegistry),
        typeof(ListArchiveHandler),
        typeof(ExtractArchiveHandler),
        typeof(TestArchiveHandler),
        typeof(OperationIdFactory),
    ];

    /// <summary>
    /// DHC-001: an interactive host that supplies nothing cannot resolve the graph, and says why.
    /// </summary>
    /// <remarks>
    /// Asserting the failure, not just the fix. If a later change made the engine quietly register a
    /// default password provider for interactive hosts, this test would fail — and it should, because
    /// a default answer to "what is the password" is a security decision taken by the wrong component.
    /// </remarks>
    [Fact]
    public void An_interactive_host_that_supplies_nothing_fails_on_the_password_provider()
    {
        var provider = Compose(headless: false);

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ListArchiveHandler>());

        Assert.True(
            error.Message.Contains(nameof(IPasswordProvider), StringComparison.Ordinal),
            error.Message);
    }

    /// <summary>DHC-002: the conflict prompt is a second, separate obligation.</summary>
    /// <remarks>
    /// Worth its own test because fixing only the password provider moves the failure one line and
    /// looks, from a transcript, exactly like no progress at all.
    /// </remarks>
    [Fact]
    public void An_interactive_host_must_also_supply_the_conflict_prompt()
    {
        var provider = Compose(headless: false, passwords: new StubPasswords());

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ExtractArchiveHandler>());

        Assert.True(
            error.Message.Contains(nameof(IConflictPrompt), StringComparison.Ordinal),
            error.Message);
    }

    /// <summary>DHC-003: with both supplied, everything the desktop shell needs resolves.</summary>
    /// <remarks>
    /// This is the regression guard. It is the assertion that would have turned a silent process exit
    /// into a build failure.
    /// </remarks>
    [Fact]
    public void An_interactive_host_that_supplies_both_resolves_the_whole_desktop_graph()
    {
        var provider = Compose(headless: false, passwords: new StubPasswords(), conflicts: new StubPrompt());

        foreach (var dependency in DesktopWorkspaceDependencies)
        {
            Assert.NotNull(provider.GetRequiredService(dependency));
        }
    }

    /// <summary>DHC-004: the headless graph still needs nothing from its host.</summary>
    /// <remarks>
    /// The CLI and NobaOS rely on this. A fix to the desktop path that made host services mandatory
    /// everywhere would break both, silently, in the same way.
    /// </remarks>
    [Fact]
    public void A_headless_host_still_composes_with_no_host_services()
    {
        var provider = Compose(headless: true);

        foreach (var dependency in DesktopWorkspaceDependencies)
        {
            Assert.NotNull(provider.GetRequiredService(dependency));
        }

        Assert.NotNull(provider.GetRequiredService<IPasswordProvider>());
        Assert.NotNull(provider.GetRequiredService<IConflictPrompt>());
    }

    private static ServiceProvider Compose(
        bool headless,
        IPasswordProvider? passwords = null,
        IConflictPrompt? conflicts = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNobaArchiveEngine(options =>
        {
            options.Headless = headless;

            // A headless host may not choose Ask; the options object rejects that combination itself.
            options.DefaultConflictPolicy = headless ? ConflictPolicy.Skip : ConflictPolicy.Ask;
        });

        if (passwords is not null)
        {
            services.AddSingleton(passwords);
        }

        if (conflicts is not null)
        {
            services.AddSingleton(conflicts);
        }

        return services.BuildServiceProvider();
    }

    private sealed class StubPasswords : IPasswordProvider
    {
        public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Password?>(null);
    }

    private sealed class StubPrompt : IConflictPrompt
    {
        public ValueTask<ConflictDecision> ResolveAsync(ConflictRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConflictDecision.Once(ConflictAction.Cancel));
    }
}
