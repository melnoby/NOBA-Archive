using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Infrastructure.Runtime;

/// <summary>
/// The 1.0 update service: disabled, and deliberately containing no URL. Shipping an unverified
/// updater is a worse vulnerability than having no updater (THREAT_MODEL.md T12).
/// </summary>
public sealed class DisabledUpdateService : IUpdateService
{
    public bool IsEnabled => false;

    public ValueTask<bool> CheckForUpdateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
