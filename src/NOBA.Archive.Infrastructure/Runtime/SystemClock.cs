using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Infrastructure.Runtime;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
