using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Formats;

/// <summary>
/// Resolves (format, required capability) to the best provider. Capability answers come from the
/// providers themselves, never from a hard-coded format list, which is what lets a backend be
/// replaced without touching the UI.
/// </summary>
public sealed class ArchiveProviderRegistry : IArchiveProviderRegistry
{
    private readonly List<IArchiveProvider> _providers;

    public ArchiveProviderRegistry(IEnumerable<IArchiveProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [.. providers];
    }

    public IReadOnlyList<IArchiveProvider> Providers => _providers;

    public IReadOnlyList<ArchiveFormat> SupportedFormats =>
        [.. _providers.SelectMany(static p => p.SupportedFormats).Distinct()];

    public ArchiveCapabilities CapabilitiesFor(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        var union = ArchiveCapabilities.None;
        foreach (var provider in _providers)
        {
            if (provider.SupportedFormats.Contains(format))
            {
                union |= provider.GetCapabilities(format);
            }
        }

        return union;
    }

    public IArchiveProvider? Resolve(ArchiveFormat format, ArchiveCapabilities required)
    {
        ArgumentNullException.ThrowIfNull(format);

        IArchiveProvider? best = null;
        var bestPriority = int.MinValue;

        foreach (var provider in _providers)
        {
            if (!provider.SupportedFormats.Contains(format))
            {
                continue;
            }

            if (required != ArchiveCapabilities.None && !provider.GetCapabilities(format).Has(required))
            {
                continue;
            }

            var priority = provider.PriorityFor(format);
            if (priority > bestPriority)
            {
                best = provider;
                bestPriority = priority;
            }
        }

        return best;
    }
}
