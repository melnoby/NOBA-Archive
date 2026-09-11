using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Operations;

/// <summary>
/// Turns raw byte counts into a progress snapshot: instantaneous and average throughput, elapsed time,
/// and totals that stay <c>null</c> when they are genuinely unknown (ADR-0007).
/// </summary>
public sealed class ProgressAggregator
{
    private readonly OperationId _id;
    private readonly OperationType _type;
    private readonly TimeSpan _minimumSampleInterval;
    private readonly Lock _gate = new();

    private TimeSpan _startedAt;
    private TimeSpan _lastSampleAt;
    private long _lastSampleBytes;
    private long _bytesDone;
    private long _itemsDone;
    private double _instantRate;
    private string? _currentItem;

    public ProgressAggregator(OperationId id, OperationType type, TimeSpan? minimumSampleInterval = null)
    {
        _id = id;
        _type = type;
        _minimumSampleInterval = minimumSampleInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public long? ItemsTotal { get; set; }

    public long? BytesTotal { get; set; }

    public void Start(TimeSpan now)
    {
        lock (_gate)
        {
            _startedAt = now;
            _lastSampleAt = now;
        }
    }

    public void ReportBytes(long deltaBytes, TimeSpan now)
    {
        lock (_gate)
        {
            _bytesDone += deltaBytes;
            var since = now - _lastSampleAt;
            if (since >= _minimumSampleInterval)
            {
                var bytes = _bytesDone - _lastSampleBytes;
                _instantRate = bytes / since.TotalSeconds;
                _lastSampleAt = now;
                _lastSampleBytes = _bytesDone;
            }
        }
    }

    public void ReportItem(string? currentItem, TimeSpan now)
    {
        lock (_gate)
        {
            _itemsDone++;
            _currentItem = currentItem;
            _ = now;
        }
    }

    public OperationProgress Snapshot(OperationState state, TimeSpan now)
    {
        lock (_gate)
        {
            var elapsed = now - _startedAt;
            var average = elapsed.TotalSeconds > 0 ? _bytesDone / elapsed.TotalSeconds : 0d;
            return new OperationProgress
            {
                Id = _id,
                Type = _type,
                State = state,
                CurrentItem = _currentItem,
                ItemsDone = _itemsDone,
                ItemsTotal = ItemsTotal,
                BytesDone = _bytesDone,
                BytesTotal = BytesTotal,
                InstantBytesPerSecond = _instantRate,
                AverageBytesPerSecond = average,
                Elapsed = elapsed,
            };
        }
    }
}
