using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>Why the Create button is not available yet.</summary>
public enum CreateBlocker
{
    None = 0,
    NoInputs,
    NoDestination,
    NoName,
    NoCreatableFormat,
    Busy,

    /// <summary>
    /// More than one input, and the chosen format holds exactly one member.
    /// </summary>
    /// <remarks>
    /// Not a rule about GZIP. It is the absence of <see cref="ArchiveCapabilities.AddEntries"/>, which
    /// is how a single-member container describes itself — so a future format with the same shape is
    /// blocked by the same line, and GZIP gaining multi-member support would unblock it without an
    /// edit here.
    /// </remarks>
    TooManyInputsForFormat,
}

/// <summary>How a finished creation ended, in the terms the screen reports.</summary>
public enum CreateStatus
{
    Idle = 0,
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// The create screen: what to compress, where to put it, and how.
/// </summary>
/// <remarks>
/// <para>
/// Everything about compression itself is <see cref="CreateOptionsViewModel"/>, which already derives
/// every option from the provider registry. This adds the other half — the inputs, the destination and
/// the name — and drives the operation.
/// </para>
/// <para>
/// It refuses rather than guesses. An existing archive is not overwritten because the screen decided
/// it probably meant to: the engine refuses the publication, this reports the collision, and
/// overwriting happens only after the user says so. That ordering is the difference between a product
/// and a way to lose a file.
/// </para>
/// </remarks>
public sealed class CreateViewModel : ObservableObject
{
    private readonly IArchiveWorkspace _workspace;
    private readonly OperationsViewModel? _operations;
    private readonly List<string> _inputs = [];
    private readonly Func<string, bool> _exists;

    private string? _destinationFolder;
    private string _archiveName = string.Empty;
    private bool _overwrite;
    private bool _isBusy;
    private CreateStatus _status = CreateStatus.Idle;
    private string? _failureDetail;
    private CreateOutcome? _result;
    private DropOutcome _lastDrop = DropOutcome.Nothing;

    /// <param name="exists">
    /// How to tell whether a dropped path is still there. Defaults to the filesystem; a test supplies
    /// its own so the drop rules can be exercised without creating files, and so "the user deleted it
    /// mid-drag" is reachable at all — it is not a state a real disk can be asked to hold still in.
    /// </param>
    public CreateViewModel(
        IArchiveWorkspace workspace,
        OperationsViewModel? operations = null,
        Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _operations = operations;
        _exists = exists ?? System.IO.Path.Exists;
        Options = new CreateOptionsViewModel(workspace);
        Options.PropertyChanged += (_, _) => RaiseComputed();
    }

    /// <summary>Format, level, solid, encryption — each one gated on what the provider reports.</summary>
    public CreateOptionsViewModel Options { get; }

    /// <summary>Files and folders to compress, in the order they were added.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    public bool HasInputs => _inputs.Count > 0;

    /// <summary>
    /// Adds paths, ignoring ones already listed.
    /// </summary>
    /// <remarks>
    /// Duplicates are dropped here rather than left for the engine, which would happily add the same
    /// folder twice and produce an archive with every entry in it twice over.
    /// </remarks>
    public int AddInputs(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || IsListed(path))
            {
                continue;
            }

            _inputs.Add(path);
            added++;
        }

        if (added > 0)
        {
            // The message describes the last drop. Adding files another way makes it history.
            ClearDropMessage();
            Settled();
        }

        return added;
    }

    /// <summary>
    /// Takes what was dropped on the screen and reports what became of each item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller has already turned whatever the platform handed it into paths; an item with no
    /// filesystem path — a virtual file from a mail client, a dragged selection of text — arrives as
    /// null or empty and is refused rather than guessed at.
    /// </para>
    /// <para>
    /// <b>One bad item does not spoil the drop.</b> Each is judged alone, and the good ones are added.
    /// Rejecting all six files because the seventh was a shortcut to nowhere would be the program
    /// punishing the user for the contents of a folder they did not curate.
    /// </para>
    /// <para>
    /// Nothing is read, copied or enumerated here. A dropped folder becomes one entry; walking it is
    /// the engine's job, later, off this thread — the alternative is a drop that stalls the window for
    /// as long as it takes to count ten thousand files.
    /// </para>
    /// </remarks>
    public DropOutcome AcceptDrop(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var added = 0;
        var rejected = new List<DropRejectionDetail>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                rejected.Add(new DropRejectionDetail(string.Empty, DropRejection.NotAFilesystemItem));
                continue;
            }

            var candidate = path.Trim();

            if (TargetPath is { } target && PathKey.Same(candidate, target))
            {
                rejected.Add(new DropRejectionDetail(candidate, DropRejection.SelfTarget));
                continue;
            }

            if (IsListed(candidate))
            {
                rejected.Add(new DropRejectionDetail(candidate, DropRejection.AlreadyListed));
                continue;
            }

            if (!_exists(candidate))
            {
                rejected.Add(new DropRejectionDetail(candidate, DropRejection.Unreachable));
                continue;
            }

            _inputs.Add(candidate);
            added++;
        }

        _lastDrop = new DropOutcome(added, rejected);

        if (added > 0)
        {
            Settled();
        }

        Raise(nameof(LastDrop));
        return _lastDrop;
    }

    /// <summary>What the most recent drop did, for the screen to explain. Never null.</summary>
    public DropOutcome LastDrop => _lastDrop;

    /// <summary>Forgets the last drop's message, so it does not outlive what it described.</summary>
    public void ClearDropMessage()
    {
        if (_lastDrop == DropOutcome.Nothing)
        {
            return;
        }

        _lastDrop = DropOutcome.Nothing;
        Raise(nameof(LastDrop));
    }

    private bool IsListed(string path)
    {
        var key = PathKey.For(path);
        foreach (var existing in _inputs)
        {
            if (string.Equals(PathKey.For(existing), key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void Settled()
    {
        ResetResult();
        Raise(nameof(Inputs));
        Raise(nameof(HasInputs));
        RaiseComputed();
    }

    public bool RemoveInput(string path)
    {
        if (!_inputs.Remove(path))
        {
            return false;
        }

        ClearDropMessage();
        Settled();
        return true;
    }

    public void ClearInputs()
    {
        if (_inputs.Count == 0)
        {
            return;
        }

        _inputs.Clear();
        ClearDropMessage();
        Settled();
    }

    /// <summary>The folder the archive will be written into.</summary>
    public string? DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (Set(ref _destinationFolder, value, nameof(DestinationFolder)))
            {
                ResetResult();
                RaiseComputed();
            }
        }
    }

    /// <summary>The archive's name, with or without an extension — the format decides the ending.</summary>
    public string ArchiveName
    {
        get => _archiveName;
        set
        {
            if (Set(ref _archiveName, value ?? string.Empty, nameof(ArchiveName)))
            {
                ResetResult();
                RaiseComputed();
            }
        }
    }

    /// <summary>
    /// Whether an existing archive at <see cref="TargetPath"/> may be replaced.
    /// </summary>
    /// <remarks>
    /// False until the user says otherwise, every time. It is reset by
    /// <see cref="ResetResult"/> whenever the target changes, so consenting to overwrite one file never
    /// silently carries over to the next.
    /// </remarks>
    public bool Overwrite
    {
        get => _overwrite;
        set => Set(ref _overwrite, value, nameof(Overwrite));
    }

    /// <summary>
    /// Where the archive will be written, extension included.
    /// </summary>
    /// <remarks>
    /// The extension comes from the chosen format rather than from what was typed: a user who names
    /// their archive "backup.zip" and then picks 7Z gets <c>backup.7z</c>, not a 7z file wearing a zip
    /// name that every other tool on the machine will misread.
    /// </remarks>
    public string? TargetPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_destinationFolder)
                || string.IsNullOrWhiteSpace(_archiveName)
                || Options.Format is not { } format)
            {
                return null;
            }

            var stem = Path.GetFileNameWithoutExtension(_archiveName.Trim());
            if (string.IsNullOrWhiteSpace(stem))
            {
                return null;
            }

            return Path.Combine(_destinationFolder, stem + format.PrimaryExtension);
        }
    }

    /// <summary>What is stopping the operation from starting, or <see cref="CreateBlocker.None"/>.</summary>
    public CreateBlocker Blocker
    {
        get
        {
            if (_isBusy)
            {
                return CreateBlocker.Busy;
            }

            if (!Options.CanCreateAnything)
            {
                return CreateBlocker.NoCreatableFormat;
            }

            if (_inputs.Count == 0)
            {
                return CreateBlocker.NoInputs;
            }

            // Before the destination, because it is about what was chosen rather than what is missing:
            // a user with two files and GZIP selected should be told which of the two to change, not
            // sent to pick a folder for an archive that cannot be written either way.
            if (!Options.PermitsInputCount(_inputs.Count))
            {
                return CreateBlocker.TooManyInputsForFormat;
            }

            if (string.IsNullOrWhiteSpace(_destinationFolder))
            {
                return CreateBlocker.NoDestination;
            }

            return TargetPath is null ? CreateBlocker.NoName : CreateBlocker.None;
        }
    }

    public bool CanCreate => Blocker == CreateBlocker.None;

    public bool IsBusy => _isBusy;

    public CreateStatus Status => _status;

    /// <summary>The engine's own sentence when a creation failed. Null otherwise.</summary>
    public string? FailureDetail => _failureDetail;

    /// <summary>True when the last attempt failed because something is already at the target path.</summary>
    public bool TargetExists { get; private set; }

    /// <summary>What the last successful creation produced.</summary>
    public CreateOutcome? Result => _result;

    /// <summary>
    /// Builds the archive.
    /// </summary>
    /// <remarks>
    /// Enqueued and then awaited, exactly as extraction is: the id comes back at once so the operation
    /// can be paused or cancelled while it runs, and the row exists before the first progress report so
    /// a fast creation cannot finish before anything was watching.
    /// </remarks>
    public async ValueTask<CreateOutcome?> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreate || TargetPath is not { } target)
        {
            return null;
        }

        _isBusy = true;
        _status = CreateStatus.Running;
        _failureDetail = null;
        TargetExists = false;
        _result = null;
        RaiseAll();

        try
        {
            var id = _workspace.StartCreate(target, [.. _inputs], Options.Build(), _overwrite);
            _operations?.Track(id, OperationType.Create, Path.GetFileName(target));

            var outcome = await _workspace.WaitForCreateAsync(id, cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                // Cancelled, or failed in a way the queue recorded rather than threw. Either way this is
                // not a completion, and it must never be reported as one.
                _status = FailureFrom(id);
                return null;
            }

            _result = outcome;
            _status = CreateStatus.Completed;

            // Consent to overwrite is spent. The next archive asks again.
            _overwrite = false;
            return outcome;
        }
        catch (ArchiveOperationException ex)
        {
            _status = CreateStatus.Failed;
            _failureDetail = ex.Error.TechnicalDetail;
            TargetExists = ex.Error.Code == ArchiveErrorCode.IoFailure
                && ex.Error.TechnicalDetail.Contains("already exists", StringComparison.Ordinal);
            return null;
        }
        finally
        {
            _isBusy = false;
            RaiseAll();
        }
    }

    /// <summary>
    /// Reads the ending from the queue rather than assuming a failure.
    /// </summary>
    /// <remarks>
    /// A null outcome means "did not complete", which is two different endings to a user: they stopped
    /// it, or it broke. Reporting a cancellation as a failure would be a small lie with a real cost —
    /// somebody looking for a fault that was their own decision.
    /// </remarks>
    private CreateStatus FailureFrom(OperationId id)
    {
        var snapshot = _workspace.Operations().FirstOrDefault(s => s.Id == id);
        if (snapshot is null)
        {
            return CreateStatus.Failed;
        }

        if (snapshot.Error is { } error)
        {
            _failureDetail = error.TechnicalDetail;
            TargetExists = error.Code == ArchiveErrorCode.IoFailure
                && error.TechnicalDetail.Contains("already exists", StringComparison.Ordinal);
        }

        return snapshot.State == OperationState.Cancelled ? CreateStatus.Cancelled : CreateStatus.Failed;
    }

    private void ResetResult()
    {
        _result = null;
        _status = CreateStatus.Idle;
        _failureDetail = null;
        TargetExists = false;
        _overwrite = false;
    }

    private void RaiseComputed()
    {
        Raise(nameof(TargetPath));
        Raise(nameof(Blocker));
        Raise(nameof(CanCreate));
    }

    private void RaiseAll()
    {
        Raise(nameof(IsBusy));
        Raise(nameof(Status));
        Raise(nameof(FailureDetail));
        Raise(nameof(TargetExists));
        Raise(nameof(Result));
        Raise(nameof(Overwrite));
        RaiseComputed();
    }
}
