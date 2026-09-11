using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.TestKit.Fakes;

/// <summary>A clock the test drives, so nothing depends on wall time.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) =>
        UtcNow = start ?? new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
