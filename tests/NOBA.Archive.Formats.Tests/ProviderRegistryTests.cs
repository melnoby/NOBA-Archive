using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Formats;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Formats.Tests;

/// <summary>
/// The registry is what lets a backend be swapped without touching the UI, and what keeps the UI from
/// offering an operation nothing can perform.
/// </summary>
public sealed class ProviderRegistryTests
{
    [Fact]
    public void Reports_no_formats_when_nothing_is_registered()
    {
        var registry = new ArchiveProviderRegistry([]);

        Assert.Empty(registry.SupportedFormats);
        Assert.Null(registry.Resolve(ArchiveFormat.Zip, ArchiveCapabilities.Extract));
        Assert.Equal(ArchiveCapabilities.None, registry.CapabilitiesFor(ArchiveFormat.Zip));
    }

    [Fact]
    public void Resolves_the_only_provider_that_can_do_the_job()
    {
        var reader = new FakeArchiveProvider(ArchiveFormat.Rar, ArchiveCapabilities.ReadOnlySet, providerId: "reader");
        var registry = new ArchiveProviderRegistry([reader]);

        Assert.Same(reader, registry.Resolve(ArchiveFormat.Rar, ArchiveCapabilities.Extract));
        Assert.Null(registry.Resolve(ArchiveFormat.Rar, ArchiveCapabilities.Create));
    }

    /// <summary>Higher priority wins - how the native backend takes over from the managed one.</summary>
    [Fact]
    public void Prefers_the_higher_priority_provider()
    {
        var managed = new FakeArchiveProvider(
            ArchiveFormat.Zip,
            ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
            priority: 10,
            providerId: "managed");
        var native = new FakeArchiveProvider(
            ArchiveFormat.Zip,
            ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.Encrypt,
            priority: 20,
            providerId: "native");

        var registry = new ArchiveProviderRegistry([managed, native]);

        Assert.Equal("native", registry.Resolve(ArchiveFormat.Zip, ArchiveCapabilities.Create)?.ProviderId);
    }

    /// <summary>
    /// Encrypted ZIP creation is exactly this case: only the native backend can write WinZip-AES, so the
    /// registry must route to it even though the managed provider also "does ZIP".
    /// </summary>
    [Fact]
    public void Routes_to_the_provider_that_has_the_needed_capability()
    {
        var managed = new FakeArchiveProvider(
            ArchiveFormat.Zip,
            ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create,
            priority: 50,
            providerId: "managed");
        var native = new FakeArchiveProvider(
            ArchiveFormat.Zip,
            ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.Encrypt,
            priority: 1,
            providerId: "native");

        var registry = new ArchiveProviderRegistry([managed, native]);

        Assert.Equal("managed", registry.Resolve(ArchiveFormat.Zip, ArchiveCapabilities.Create)?.ProviderId);
        Assert.Equal(
            "native",
            registry.Resolve(ArchiveFormat.Zip, ArchiveCapabilities.Create | ArchiveCapabilities.Encrypt)?.ProviderId);
    }

    /// <summary>The union is what the UI may offer at all; per-provider capability decides who runs it.</summary>
    [Fact]
    public void Unions_capabilities_across_providers()
    {
        var a = new FakeArchiveProvider(ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet, providerId: "a");
        var b = new FakeArchiveProvider(ArchiveFormat.Zip, ArchiveCapabilities.Create | ArchiveCapabilities.Encrypt, providerId: "b");

        var capabilities = new ArchiveProviderRegistry([a, b]).CapabilitiesFor(ArchiveFormat.Zip);

        Assert.True(capabilities.Has(ArchiveCapabilities.Extract));
        Assert.True(capabilities.Has(ArchiveCapabilities.Create));
        Assert.True(capabilities.Has(ArchiveCapabilities.Encrypt));
        Assert.False(capabilities.Has(ArchiveCapabilities.SplitVolumes));
    }

    [Fact]
    public void Ignores_providers_that_do_not_handle_the_format()
    {
        var rarReader = new FakeArchiveProvider(ArchiveFormat.Rar, ArchiveCapabilities.ReadOnlySet);

        var registry = new ArchiveProviderRegistry([rarReader]);

        Assert.Null(registry.Resolve(ArchiveFormat.SevenZip, ArchiveCapabilities.Extract));
        Assert.Equal(ArchiveCapabilities.None, registry.CapabilitiesFor(ArchiveFormat.SevenZip));
    }
}

/// <summary>
/// The rule from ADR-0005, exercised through the provider contract: a session hands bytes to a stream
/// the caller owns and has no way to name a destination on disk.
/// </summary>
public sealed class ProviderContractTests
{
    [Fact]
    public async Task A_session_decodes_into_a_caller_owned_stream()
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["docs/readme.txt"] = "hello"u8.ToArray(),
        };
        var provider = new FakeArchiveProvider(ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet, content);

        await using var session = await provider.OpenAsync(
            ArchiveSource.FromFile("/tmp/whatever.zip"),
            OpenOptions.Default,
            new ScriptedPasswordProvider(null),
            CancellationToken.None);

        var entries = new List<ArchiveEntry>();
        await foreach (var entry in session.ListEntriesAsync(CancellationToken.None))
        {
            entries.Add(entry);
        }

        using var destination = new MemoryStream();
        await session.ExtractToAsync(entries[0], destination, null, CancellationToken.None);

        Assert.Equal("hello"u8.ToArray().Length, destination.ToArray().Length);
    }

    /// <summary>A hostile entry stays listable so the user can be told what the archive attempted.</summary>
    [Fact]
    public async Task A_hostile_entry_is_listed_with_its_rejection_rather_than_throwing()
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [@"..\..\Windows\System32\evil.dll"] = [1, 2, 3],
            ["docs/ok.txt"] = [4, 5, 6],
        };
        var provider = new FakeArchiveProvider(ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet, content);

        await using var session = await provider.OpenAsync(
            ArchiveSource.FromFile("/tmp/hostile.zip"),
            OpenOptions.Default,
            new ScriptedPasswordProvider(null),
            CancellationToken.None);

        var entries = new List<ArchiveEntry>();
        await foreach (var entry in session.ListEntriesAsync(CancellationToken.None))
        {
            entries.Add(entry);
        }

        Assert.Equal(2, entries.Count);
        var hostile = entries.Single(static e => e.IsRejected);
        Assert.Null(hostile.Path);
        Assert.NotNull(hostile.Rejection);
        Assert.Equal(Core.Primitives.PathRejectionReason.TraversalSegment, hostile.Rejection.Reason);
        Assert.Contains("evil.dll", hostile.DisplayPath);
    }
}
