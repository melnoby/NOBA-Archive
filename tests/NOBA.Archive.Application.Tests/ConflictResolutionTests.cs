using NOBA.Archive.Application.Policies;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Application.Tests;

/// <summary>APP-032 and APP-033: nothing is overwritten unless asked, and "...All" really latches.</summary>
public sealed class ConflictResolutionTests
{
    private static ConflictRequest Request(string path = "/dest/report.pdf") =>
        new(path, ByteSize.FromKibibytes(4), ByteSize.FromKibibytes(8));

    [Theory]
    [InlineData(ConflictPolicy.Overwrite, ConflictAction.Overwrite)]
    [InlineData(ConflictPolicy.Skip, ConflictAction.Skip)]
    [InlineData(ConflictPolicy.Rename, ConflictAction.Rename)]
    [InlineData(ConflictPolicy.Fail, ConflictAction.Cancel)]
    public async Task Applies_a_fixed_policy_without_prompting(ConflictPolicy policy, ConflictAction expected)
    {
        var prompt = new ScriptedConflictPrompt();
        var service = new ConflictResolutionService(prompt);

        var action = await service.ResolveAsync(Request(), policy, CancellationToken.None);

        Assert.Equal(expected, action);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Asks_once_per_conflict_when_the_policy_is_ask()
    {
        var prompt = new ScriptedConflictPrompt(
            ConflictDecision.Once(ConflictAction.Skip),
            ConflictDecision.Once(ConflictAction.Overwrite));
        var service = new ConflictResolutionService(prompt);

        var first = await service.ResolveAsync(Request("/dest/a"), ConflictPolicy.Ask, CancellationToken.None);
        var second = await service.ResolveAsync(Request("/dest/b"), ConflictPolicy.Ask, CancellationToken.None);

        Assert.Equal(ConflictAction.Skip, first);
        Assert.Equal(ConflictAction.Overwrite, second);
        Assert.Equal(2, prompt.CallCount);
    }

    [Fact]
    public async Task Latches_an_apply_to_all_decision_and_stops_prompting()
    {
        var prompt = new ScriptedConflictPrompt(ConflictDecision.All(ConflictAction.Overwrite));
        var service = new ConflictResolutionService(prompt);

        var first = await service.ResolveAsync(Request("/dest/a"), ConflictPolicy.Ask, CancellationToken.None);
        var second = await service.ResolveAsync(Request("/dest/b"), ConflictPolicy.Ask, CancellationToken.None);
        var third = await service.ResolveAsync(Request("/dest/c"), ConflictPolicy.Ask, CancellationToken.None);

        Assert.Equal(ConflictAction.Overwrite, first);
        Assert.Equal(ConflictAction.Overwrite, second);
        Assert.Equal(ConflictAction.Overwrite, third);
        Assert.Equal(1, prompt.CallCount);
    }

    /// <summary>A latched decision must not leak into the next operation.</summary>
    [Fact]
    public async Task Reset_clears_the_latched_decision()
    {
        var prompt = new ScriptedConflictPrompt(
            ConflictDecision.All(ConflictAction.Skip),
            ConflictDecision.Once(ConflictAction.Overwrite));
        var service = new ConflictResolutionService(prompt);

        await service.ResolveAsync(Request(), ConflictPolicy.Ask, CancellationToken.None);
        service.ResetLatchedDecision();
        var afterReset = await service.ResolveAsync(Request(), ConflictPolicy.Ask, CancellationToken.None);

        Assert.Equal(ConflictAction.Overwrite, afterReset);
        Assert.Equal(2, prompt.CallCount);
    }

    [Fact]
    public void Builds_a_non_colliding_name()
    {
        var service = new ConflictResolutionService(new ScriptedConflictPrompt());
        var taken = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("dir", "report (2).pdf"),
            Path.Combine("dir", "report (3).pdf"),
        };

        var renamed = service.BuildRenamedPath(Path.Combine("dir", "report.pdf"), taken.Contains);

        Assert.Equal(Path.Combine("dir", "report (4).pdf"), renamed);
    }

    [Fact]
    public void Preserves_a_compound_extension_stem()
    {
        var service = new ConflictResolutionService(new ScriptedConflictPrompt());

        var renamed = service.BuildRenamedPath(Path.Combine("dir", "backup.tar.gz"), static _ => false);

        Assert.Equal(Path.Combine("dir", "backup.tar (2).gz"), renamed);
    }
}

public sealed class CapabilityGateTests
{
    private const ArchiveCapabilities RarLike = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.ReadSplitVolumes;

    [Fact]
    public void Allows_what_the_backend_supports()
    {
        Assert.True(CapabilityGate.IsAllowed(RarLike, ArchiveCapabilities.Extract));
        Assert.Null(CapabilityGate.Check(RarLike, ArchiveCapabilities.Test, ArchiveFormat.Rar));
    }

    /// <summary>ADR-0004: RAR creation is not a missing feature, it is a permanent exclusion.</summary>
    [Fact]
    public void Refuses_creation_for_a_read_only_backend()
    {
        var error = CapabilityGate.Check(RarLike, ArchiveCapabilities.Create, ArchiveFormat.Rar);

        Assert.NotNull(error);
        Assert.Equal(Core.Errors.ArchiveErrorCode.UnsupportedOperation, error.Code);
        Assert.Contains("RAR", error.TechnicalDetail);
    }

    [Fact]
    public void Reports_exactly_which_capabilities_are_missing()
    {
        var missing = CapabilityGate.Missing(
            RarLike,
            ArchiveCapabilities.Extract | ArchiveCapabilities.Create | ArchiveCapabilities.Encrypt);

        Assert.True(missing.Has(ArchiveCapabilities.Create));
        Assert.True(missing.Has(ArchiveCapabilities.Encrypt));
        Assert.False(missing.Has(ArchiveCapabilities.Extract));
    }

    [Fact]
    public void EnsureAllowed_throws_a_typed_error()
    {
        var ex = Assert.Throws<Core.Errors.ArchiveOperationException>(
            () => CapabilityGate.EnsureAllowed(RarLike, ArchiveCapabilities.Create, ArchiveFormat.Rar));

        Assert.Equal(Core.Errors.ArchiveErrorCode.UnsupportedOperation, ex.Error.Code);
    }
}
