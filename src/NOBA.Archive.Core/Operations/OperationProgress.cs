using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Operations;

/// <summary>
/// A progress snapshot. The nullable totals are the point: streaming and solid archives genuinely
/// cannot always know them, and an indeterminate indicator beats an invented percentage (ADR-0007).
/// </summary>
public sealed record OperationProgress
{
    public required OperationId Id { get; init; }

    public required OperationType Type { get; init; }

    public required OperationState State { get; init; }

    public string? CurrentItem { get; init; }

    public long ItemsDone { get; init; }

    public long? ItemsTotal { get; init; }

    public long BytesDone { get; init; }

    public long? BytesTotal { get; init; }

    public double InstantBytesPerSecond { get; init; }

    public double AverageBytesPerSecond { get; init; }

    public TimeSpan Elapsed { get; init; }

    public double? CompressionRatio { get; init; }

    /// <summary>Null means indeterminate. Never synthesised from a guess.</summary>
    public double? Percent =>
        BytesTotal is > 0
            ? Math.Clamp((double)BytesDone / BytesTotal.Value * 100d, 0d, 100d)
            : ItemsTotal is > 0
                ? Math.Clamp((double)ItemsDone / ItemsTotal.Value * 100d, 0d, 100d)
                : null;

    public bool IsIndeterminate => Percent is null;

    /// <summary>Null when the total is unknown or throughput has not stabilised.</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (BytesTotal is not > 0 || AverageBytesPerSecond <= 0)
            {
                return null;
            }

            var remaining = BytesTotal.Value - BytesDone;
            if (remaining <= 0)
            {
                return TimeSpan.Zero;
            }

            var seconds = remaining / AverageBytesPerSecond;
            return seconds > TimeSpan.MaxValue.TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
        }
    }

    public static OperationProgress Starting(OperationId id, OperationType type) =>
        new() { Id = id, Type = type, State = OperationState.Running };
}
