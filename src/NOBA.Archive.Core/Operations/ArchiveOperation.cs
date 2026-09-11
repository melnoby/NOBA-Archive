using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Operations;

/// <summary>A queued or running unit of work. State lives here, never in a ViewModel.</summary>
public sealed class ArchiveOperation
{
    private readonly List<PathRejection> _blockedEntries = [];

    public ArchiveOperation(OperationId id, OperationType type, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Id = id;
        Type = type;
        Description = description;
    }

    public OperationId Id { get; }

    public OperationType Type { get; }

    public string Description { get; }

    public OperationState State { get; private set; } = OperationState.Queued;

    public ArchiveError? Error { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Entries refused by the path policy. Reported to the user, never silently dropped.</summary>
    public IReadOnlyList<PathRejection> BlockedEntries => _blockedEntries;

    public bool TryApply(OperationTrigger trigger, DateTimeOffset now, ArchiveError? error = null)
    {
        if (!OperationStateMachine.TryTransition(State, trigger, out var next))
        {
            return false;
        }

        State = next;
        switch (trigger)
        {
            case OperationTrigger.Start:
                StartedAt ??= now;
                break;
            case OperationTrigger.Complete:
            case OperationTrigger.Cancel:
                FinishedAt = now;
                break;
            case OperationTrigger.Fail:
                FinishedAt = now;
                Error = error;
                break;
            case OperationTrigger.Pause:
            case OperationTrigger.Resume:
            default:
                break;
        }

        return true;
    }

    public void RecordBlockedEntry(PathRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        _blockedEntries.Add(rejection);
    }
}
