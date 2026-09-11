using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Operations;

/// <summary>Hands out the correlation identifiers users quote back to us in bug reports.</summary>
public sealed class OperationIdFactory
{
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private DateOnly _day;
    private int _sequence;

    public OperationIdFactory(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public OperationId Next(OperationType type)
    {
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        int sequence;
        lock (_gate)
        {
            if (today != _day)
            {
                _day = today;
                _sequence = 0;
            }

            sequence = ++_sequence;
        }

        return OperationId.Create(type, now, sequence);
    }
}
