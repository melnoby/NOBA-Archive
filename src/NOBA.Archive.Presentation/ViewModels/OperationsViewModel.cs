using System.Globalization;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>
/// One operation, as a screen needs to read it.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is derived from the engine's own <see cref="OperationProgress"/>; none of it is
/// invented. In particular <see cref="Percent"/> stays nullable all the way to the surface, because a
/// solid archive genuinely cannot say how far along it is until the block ends — and a progress bar
/// that shows a confident 40% it made up is worse than one that admits it does not know (ADR-0007).
/// </para>
/// <para>
/// State is never assigned here. It arrives from <see cref="IOperationQueue"/>, which owns the state
/// machine; this class would be the wrong place to decide that something is paused, because two things
/// deciding that is how a screen and an engine come to disagree.
/// </para>
/// </remarks>
public sealed class OperationViewModel : ObservableObject
{
    private OperationProgress? _progress;
    private OperationState _state;
    private string? _errorDetail;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private OperationResult? _result;

    public OperationViewModel(OperationId id, OperationType type, string description)
    {
        Id = id;
        Type = type;
        Description = description;
    }

    public OperationId Id { get; }

    public OperationType Type { get; }

    /// <summary>
    /// What is being worked on, in the engine's own words.
    /// </summary>
    /// <remarks>
    /// The queue is the single writer: it names the operation when it enqueues one, and a snapshot may
    /// arrive before anything above the seam has had a chance to register it. Whoever displays this
    /// isolates it — see <see cref="OperationFormatter.TargetOf"/> — because it is a filename shown
    /// inside a sentence, and it may be Arabic.
    /// </remarks>
    public string Description { get; private set; }

    public OperationState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                RaiseAll();
            }
        }
    }

    /// <summary>Where the files are going. Null for an operation that writes nothing.</summary>
    public string? Destination { get; private set; }

    /// <summary>Whether this kind of work can be suspended. Decided by the engine, never guessed here.</summary>
    public bool SupportsPause { get; private set; } = true;

    public string? ErrorDetail
    {
        get => _errorDetail;
        private set => Set(ref _errorDetail, value);
    }

    /// <summary>Null while the total is unknown. Never synthesised.</summary>
    public double? Percent => _progress?.Percent;

    public bool IsIndeterminate => _progress is null || _progress.IsIndeterminate;

    public bool IsRunning => _state is OperationState.Running or OperationState.Paused or OperationState.Cancelling;

    public bool IsFinished => _state is OperationState.Completed or OperationState.Cancelled or OperationState.Failed;

    /// <summary>Cancellation was asked for and the work has not stopped yet.</summary>
    public bool IsCancelling => _state == OperationState.Cancelling;

    public bool CanPause => _state == OperationState.Running && SupportsPause;

    public bool CanResume => _state == OperationState.Paused;

    /// <summary>
    /// False once cancellation has been requested.
    /// </summary>
    /// <remarks>
    /// Pressing Cancel a second time cannot make the writer stop sooner, and a button that stays live
    /// after it has been used invites exactly that. The row says Cancelling instead, which is the honest
    /// answer to "did it hear me".
    /// </remarks>
    public bool CanCancel => _state is OperationState.Queued or OperationState.Running or OperationState.Paused;

    /// <summary>The entry the engine is on, or null before the first report.</summary>
    public string? CurrentItem => _progress?.CurrentItem;

    public long ItemsDone => _progress?.ItemsDone ?? 0;

    public long? ItemsTotal => _progress?.ItemsTotal;

    public long BytesDone => _progress?.BytesDone ?? 0;

    public long? BytesTotal => _progress?.BytesTotal;

    public double BytesPerSecond => _progress?.AverageBytesPerSecond ?? 0;

    public TimeSpan? Eta => _progress?.Eta;

    public TimeSpan Elapsed => _progress?.Elapsed ?? TimeSpan.Zero;

    /// <summary>How long it ran, from the queue's own clock. Null until it stops.</summary>
    public TimeSpan? Duration =>
        _startedAt is { } started && _finishedAt is { } finished ? finished - started : null;

    /// <summary>
    /// What the operation produced, once it has.
    /// </summary>
    /// <remarks>
    /// It arrives from whoever awaited the operation rather than from the queue, because the queue
    /// schedules work of every type and has no business knowing what an extraction returns. Null for
    /// anything still running, cancelled, or failed.
    /// </remarks>
    public OperationResult? Result
    {
        get => _result;
        private set
        {
            if (Set(ref _result, value))
            {
                Raise(nameof(HasResult));
            }
        }
    }

    public bool HasResult => _result is not null;

    /// <summary>Takes the outcome from the caller that awaited this operation.</summary>
    public void Complete(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result;
    }

    /// <summary>Takes a progress report. Ignores one addressed to another operation.</summary>
    public void Apply(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Id != Id)
        {
            return;
        }

        _progress = progress;
        RaiseAll();
    }

    /// <summary>Takes a state transition from the queue, which is the only thing that may decide one.</summary>
    public void Apply(OperationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id != Id)
        {
            return;
        }

        ErrorDetail = snapshot.Error?.TechnicalDetail;

        if (snapshot.SupportsPause != SupportsPause)
        {
            SupportsPause = snapshot.SupportsPause;
            Raise(nameof(SupportsPause));
            Raise(nameof(CanPause));
        }

        if (snapshot.Destination is { Length: > 0 } destination && destination != Destination)
        {
            Destination = destination;
            Raise(nameof(Destination));
        }
        _startedAt = snapshot.StartedAt;
        _finishedAt = snapshot.FinishedAt;

        if (snapshot.Description is { Length: > 0 } named && named != Description)
        {
            Description = named;
            Raise(nameof(Description));
        }

        if (snapshot.LastProgress is { } last)
        {
            _progress = last;
        }

        State = snapshot.State;
    }

    private void RaiseAll()
    {
        Raise(nameof(Percent));
        Raise(nameof(IsIndeterminate));
        Raise(nameof(IsRunning));
        Raise(nameof(IsFinished));
        Raise(nameof(IsCancelling));
        Raise(nameof(CanPause));
        Raise(nameof(CanResume));
        Raise(nameof(CanCancel));
        Raise(nameof(CurrentItem));
        Raise(nameof(ItemsDone));
        Raise(nameof(ItemsTotal));
        Raise(nameof(BytesDone));
        Raise(nameof(BytesTotal));
        Raise(nameof(BytesPerSecond));
        Raise(nameof(Eta));
        Raise(nameof(Elapsed));
        Raise(nameof(Duration));
    }
}

/// <summary>
/// What a finished operation produced.
/// </summary>
/// <remarks>
/// Deliberately not the engine's <c>ExtractOutcome</c>: the operations screen shows work of every
/// type, and binding it to one handler's return shape would make the next type of operation a
/// rewrite rather than an addition.
/// </remarks>
public sealed record OperationResult(long ItemsWritten, long BytesWritten, long BlockedCount);

/// <summary>
/// Every operation the queue knows about, kept current from its two events.
/// </summary>
/// <remarks>
/// Lives on the shell rather than on a screen: an extraction started from Browse outlives the moment
/// the button was pressed, and a person who navigates away has not cancelled anything.
/// </remarks>
public sealed class OperationsViewModel : ObservableObject
{
    private readonly IArchiveWorkspace _workspace;
    private readonly Dictionary<string, OperationViewModel> _byId = new(StringComparer.Ordinal);
    private readonly List<OperationViewModel> _order = [];
    private OperationViewModel? _active;

    public OperationsViewModel(IArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;

        _workspace.ProgressReported += OnProgress;
        _workspace.OperationChanged += OnChanged;
    }

    /// <summary>
    /// Everything the queue has told us about, newest first, finished ones included.
    /// </summary>
    /// <remarks>
    /// Order is kept explicitly rather than read off the dictionary: a dictionary's enumeration order
    /// is an implementation detail, and a list of operations whose order can change between two reads
    /// is a list that jumps under the reader's cursor.
    /// </remarks>
    public IReadOnlyList<OperationViewModel> All
    {
        get
        {
            var newestFirst = new List<OperationViewModel>(_order);
            newestFirst.Reverse();
            return newestFirst;
        }
    }

    public bool HasAny => _order.Count > 0;

    /// <summary>
    /// The one worth showing a progress strip for, or null when nothing is in flight.
    /// </summary>
    public OperationViewModel? Active
    {
        get => _active;
        private set
        {
            if (Set(ref _active, value))
            {
                Raise(nameof(HasActive));
            }
        }
    }

    public bool HasActive => _active is not null;

    /// <summary>Starts tracking an operation the moment it is enqueued, before any event arrives.</summary>
    public OperationViewModel Track(OperationId id, OperationType type, string description)
    {
        if (!_byId.TryGetValue(id.Value, out var operation))
        {
            operation = new OperationViewModel(id, type, description);
            _byId[id.Value] = operation;
            _order.Add(operation);
            Raise(nameof(HasAny));
        }

        Active = operation;
        Raise(nameof(All));
        return operation;
    }

    public bool Pause(OperationId id) => _workspace.TryPause(id);

    public bool Resume(OperationId id) => _workspace.TryResume(id);

    public bool Cancel(OperationId id) => _workspace.TryCancel(id);

    /// <summary>Puts a finished operation's strip away. Does not touch the queue's own record.</summary>
    public void DismissActive()
    {
        if (_active?.IsFinished == true)
        {
            Active = null;
        }
    }

    /// <summary>
    /// Removes every finished operation, here and in the queue.
    /// </summary>
    /// <remarks>
    /// It asks the queue first and drops only what the queue agreed to forget. Clearing the list on
    /// its own would leave the two disagreeing, and the next snapshot would put the rows back.
    /// </remarks>
    public int ClearFinished()
    {
        var removed = _workspace.RemoveFinishedOperations();
        if (removed == 0)
        {
            return 0;
        }

        var finished = _order.Where(static o => o.IsFinished).ToList();
        foreach (var operation in finished)
        {
            _order.Remove(operation);
            _byId.Remove(operation.Id.Value);
        }

        if (_active?.IsFinished == true)
        {
            Active = null;
        }

        Raise(nameof(All));
        Raise(nameof(HasAny));
        Raise(nameof(HasFinished));
        return finished.Count;
    }

    /// <summary>Whether anything is finished and therefore clearable.</summary>
    public bool HasFinished => _order.Exists(static o => o.IsFinished);

    /// <summary>
    /// Records what an operation produced, from the caller that awaited it.
    /// </summary>
    /// <remarks>
    /// The queue reports that an operation ended; it cannot report what it wrote, because it is not
    /// typed to the work. This is the seam where the two halves of "finished" meet.
    /// </remarks>
    public void RecordResult(OperationId id, OperationResult result)
    {
        if (_byId.TryGetValue(id.Value, out var operation))
        {
            operation.Complete(result);
        }
    }

    private void OnProgress(object? sender, OperationProgress progress)
    {
        if (_byId.TryGetValue(progress.Id.Value, out var operation))
        {
            operation.Apply(progress);
        }
    }

    private void OnChanged(object? sender, OperationSnapshot snapshot)
    {
        if (!_byId.TryGetValue(snapshot.Id.Value, out var operation))
        {
            operation = new OperationViewModel(snapshot.Id, snapshot.Type, snapshot.Description);
            _byId[snapshot.Id.Value] = operation;
            _order.Add(operation);
            Raise(nameof(All));
            Raise(nameof(HasAny));
        }

        operation.Apply(snapshot);
        Raise(nameof(HasFinished));
    }
}

/// <summary>
/// Turns a running operation into fragments a screen can lay out.
/// </summary>
/// <remarks>
/// Fragments rather than a sentence, for the reason the archive summary and the test verdict already
/// are: a rate and an Arabic word in one text run come out in Arabic-Indic digits while the identical
/// figures in the table beside them come out Western.
/// </remarks>
public static class OperationFormatter
{
    /// <summary>
    /// The archive's name, isolated.
    /// </summary>
    /// <remarks>
    /// It sits between a verb and a state chip, so an Arabic name left bare would reorder the line
    /// around it. The same rule the file table applies to every name it shows.
    /// </remarks>
    /// <summary>
    /// What the operation is doing, by its own type.
    /// </summary>
    /// <remarks>
    /// Every row used to read "Extracting", because extraction was the only thing the queue had ever
    /// been given. A test row saying it was extracting would be a plain untruth about the user's
    /// archive, so the verb comes from the operation rather than from the screen that draws it.
    /// </remarks>
    public static string VerbOf(OperationViewModel operation, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(text);

        return operation.Type switch
        {
            OperationType.Test => text.OperationTesting,
            OperationType.Create => text.OperationCreating,
            _ => text.OperationExtracting,
        };
    }

    public static string TargetOf(OperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return BidiText.Isolate(operation.Description);
    }

    /// <summary>What is being worked on, if the engine has said yet.</summary>
    /// <summary>
    /// The destination, laid out so a right-to-left UI cannot reorder it.
    /// </summary>
    /// <remarks>
    /// A path is a technical value, not prose: isolating it keeps <c>C:\Users\...\out</c> reading
    /// left to right inside an Arabic sentence, exactly as the current-entry line already does.
    /// </remarks>
    public static string DestinationOf(OperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Destination is { Length: > 0 } destination ? BidiText.Isolate(destination) : string.Empty;
    }

    public static string CurrentItemOf(OperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.CurrentItem is { Length: > 0 } item
            ? BidiText.IsolateAsLtr(item)
            : string.Empty;
    }

    /// <summary>
    /// The measured facts: how far, how fast, how much longer.
    /// </summary>
    /// <remarks>
    /// Anything the engine could not measure is left out rather than shown as a zero. A speed of
    /// <c>0 B/s</c> on a running operation reads as "stuck"; the absence of a speed reads as "not yet
    /// known", which is the truth in the first moments of every extraction.
    /// </remarks>
    public static IReadOnlyList<SummaryPart> MeasuresOf(OperationViewModel operation, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(text);

        var language = text.Language;
        var parts = new List<SummaryPart>(10);

        if (operation.Percent is { } percent)
        {
            parts.Add(SummaryPart.Value(
                (percent / 100d).ToString("P0", CultureInfo.InvariantCulture)));
        }
        else
        {
            parts.Add(SummaryPart.Label(text.OperationIndeterminate, language));
        }

        if (operation.BytesTotal is { } total && total > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(
                $"{ByteSize.FromBytes(operation.BytesDone).ToDisplayString(CultureInfo.InvariantCulture)} / {ByteSize.FromBytes(total).ToDisplayString(CultureInfo.InvariantCulture)}"));
        }
        else if (operation.BytesDone > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(
                ByteSize.FromBytes(operation.BytesDone).ToDisplayString(CultureInfo.InvariantCulture)));
        }

        if (operation.BytesPerSecond > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(
                $"{ByteSize.FromBytes((long)operation.BytesPerSecond).ToDisplayString(CultureInfo.InvariantCulture)}/s"));
        }

        if (operation.Eta is { } eta)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Label(text.OperationEtaLabel, language));
            parts.Add(SummaryPart.Value(Clock(eta)));
        }

        if (operation.ItemsTotal is { } items && items > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(
                $"{SummaryFormatter.Count(operation.ItemsDone)} / {SummaryFormatter.Count(items)}"));
        }

        return parts;
    }

    /// <summary>
    /// What a finished operation produced: files, bytes, and how long it took.
    /// </summary>
    /// <remarks>
    /// Empty while the operation is still going, and empty for one that was cancelled or failed - a
    /// count of what a cancelled extraction managed to write before it stopped is a number nobody
    /// asked for and everybody would misread as success.
    /// </remarks>
    public static IReadOnlyList<SummaryPart> ResultOf(OperationViewModel operation, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(text);

        if (operation.State != OperationState.Completed || operation.Result is not { } result)
        {
            return [];
        }

        var language = text.Language;
        var parts = new List<SummaryPart>(8)
        {
            SummaryPart.Value(SummaryFormatter.Count(result.ItemsWritten)),
            SummaryPart.Label(text.FilesLabel, language),
        };

        if (result.BytesWritten > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(
                ByteSize.FromBytes(result.BytesWritten).ToDisplayString(CultureInfo.InvariantCulture)));
        }

        if (operation.Duration is { } duration)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Label(text.OperationTookLabel, language));
            parts.Add(SummaryPart.Value(Clock(duration)));
        }

        // Refused entries are never folded into the success count. An extraction that wrote 300 files
        // and refused 2 is not the same event as one that wrote 302.
        if (result.BlockedCount > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(SummaryFormatter.Count(result.BlockedCount)));
            parts.Add(SummaryPart.Label(text.BlockedLabel, language));
        }

        return parts;
    }

    /// <summary>
    /// A glyph per state, so the state never depends on colour.
    /// </summary>
    /// <remarks>
    /// Segoe Fluent Icons code points as escapes. Every state also carries a word from
    /// <see cref="StateLabel"/>; between the two, nothing here is legible only to someone who can
    /// distinguish the hues.
    /// </remarks>
    public static string GlyphFor(OperationState state) => state switch
    {
        OperationState.Queued => "\uE823",     // Clock
        OperationState.Running => "\uE768",    // Play
        OperationState.Paused => "\uE769",     // Pause
        OperationState.Cancelling => "\uE895",  // Sync - stopping, not stopped
        OperationState.Completed => "\uE73E",  // CheckMark
        OperationState.Cancelled => "\uE711",  // Cancel
        _ => "\uEA39",                          // ErrorBadge
    };

    /// <summary>The state, in the UI language.</summary>
    public static string StateLabel(OperationState state, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return state switch
        {
            OperationState.Queued => text.OperationQueued,
            OperationState.Running => text.OperationRunning,
            OperationState.Paused => text.OperationPaused,
            OperationState.Cancelling => text.OperationCancelling,
            OperationState.Completed => text.OperationCompleted,
            OperationState.Cancelled => text.OperationCancelled,
            _ => text.OperationFailed,
        };
    }

    /// <summary>
    /// A duration as a clock, never as prose.
    /// </summary>
    /// <remarks>
    /// <c>hh:mm:ss</c> with an invariant format: a localised duration string would put Arabic letters
    /// beside the digits, and the shaper turns digits that sit next to Arabic into Arabic-Indic ones.
    /// The table and the summary line are already Western; a third number system in the same window is
    /// not a translation, it is a defect.
    /// </remarks>
    private static string Clock(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
