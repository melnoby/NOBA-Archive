using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// An engine seam with no engine behind it.
/// </summary>
/// <remarks>
/// The view models are supposed to hold no archive logic, so testing them should need no archive — and
/// the fact that this fake is thirty lines of dictionaries is itself evidence for that claim. If a
/// screen ever needed a real provider to be tested, something had moved into the UI that should not
/// have.
/// </remarks>
internal sealed class FakeWorkspace : IArchiveWorkspace
{
    private readonly Dictionary<ArchiveFormat, ArchiveCapabilities> _capabilities;

    public FakeWorkspace(Dictionary<ArchiveFormat, ArchiveCapabilities>? capabilities = null) =>
        _capabilities = capabilities ?? new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.SevenZip] = ArchiveCapabilities.ReadOnlySet
                | ArchiveCapabilities.Create
                | ArchiveCapabilities.AddEntries
                | ArchiveCapabilities.Encrypt
                | ArchiveCapabilities.EncryptFileNames
                | ArchiveCapabilities.SolidCompression,
            [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet
                | ArchiveCapabilities.Create
                | ArchiveCapabilities.AddEntries,
            [ArchiveFormat.Rar] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.SolidCompression,
        };

    public ListOutcome? Listing { get; set; }

    public ArchiveOperationException? ListFailure { get; set; }

    public ExtractOutcome? ExtractResult { get; set; }

    public IReadOnlyList<string>? LastSelection { get; private set; }

    public string? LastDestination { get; private set; }

    public IReadOnlyList<ArchiveFormat> AvailableFormats => _capabilities.Keys.ToList();

    public ArchiveCapabilities CapabilitiesFor(ArchiveFormat format) =>
        _capabilities.TryGetValue(format, out var value) ? value : ArchiveCapabilities.None;

    public ValueTask<ListOutcome> ListAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        return ValueTask.FromResult(Listing ?? new ListOutcome(
            ArchiveFormat.SevenZip,
            new ArchiveStatistics(),
            []));
    }

    // ---------- operations ----------

    private readonly OperationIdFactory _ids = new(new FixedClock());
    private readonly Dictionary<string, OperationState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    public event EventHandler<OperationProgress>? ProgressReported;

    public event EventHandler<OperationSnapshot>? OperationChanged;

    /// <summary>Ids handed out, in order, so a test can address the operation it started.</summary>
    public List<OperationId> Started { get; } = [];

    public IReadOnlyList<OperationId> Paused => _paused;

    public IReadOnlyList<OperationId> Cancelled => _cancelled;

    private readonly List<OperationId> _paused = [];
    private readonly List<OperationId> _cancelled = [];

    /// <summary>The conflict policy the last extraction was started with.</summary>
    public ConflictPolicy LastConflictPolicy { get; private set; } = ConflictPolicy.Ask;

    public OperationId StartExtract(
        string archivePath,
        string destination,
        IReadOnlyList<string>? selectedEntries,
        ConflictPolicy conflictPolicy = ConflictPolicy.Ask)
    {
        LastDestination = destination;
        LastSelection = selectedEntries;
        LastConflictPolicy = conflictPolicy;

        var id = _ids.Next(OperationType.Extract);
        Started.Add(id);
        _names[id.Value] = System.IO.Path.GetFileName(archivePath);
        Transition(id, OperationState.Running);
        return id;
    }

    public Task<ExtractOutcome?> WaitForExtractAsync(OperationId id, CancellationToken cancellationToken)
    {
        if (CancelNextOperation)
        {
            CancelNextOperation = false;
            Transition(id, OperationState.Cancelling);
            Transition(id, OperationState.Cancelled);
            return Task.FromResult<ExtractOutcome?>(null);
        }

        if (_states.TryGetValue(id.Value, out var state)
            && state is OperationState.Cancelled or OperationState.Cancelling or OperationState.Failed)
        {
            // Cancelling counts: the work is stopping, and it must not be allowed to report a result.
            Transition(id, OperationState.Cancelled);
            return Task.FromResult<ExtractOutcome?>(null);
        }

        Transition(id, OperationState.Completed);
        return Task.FromResult<ExtractOutcome?>(ExtractResult ?? new ExtractOutcome { Format = ArchiveFormat.SevenZip });
    }

    public bool TryPause(OperationId id)
    {
        if (_states.GetValueOrDefault(id.Value) != OperationState.Running)
        {
            return false;
        }

        _paused.Add(id);
        Transition(id, OperationState.Paused);
        return true;
    }

    public bool TryResume(OperationId id)
    {
        if (_states.GetValueOrDefault(id.Value) != OperationState.Paused)
        {
            return false;
        }

        Transition(id, OperationState.Running);
        return true;
    }

    /// <summary>
    /// Cancels the way the real queue does: Cancelling first, Cancelled when the work has unwound.
    /// </summary>
    /// <remarks>
    /// <see cref="FinishCancel"/> is the second half, so a test can hold an operation in the
    /// intermediate state and assert what the screen says while it is there. A fake that jumped
    /// straight to Cancelled would make the state untestable and let a UI that never handles it pass.
    /// </remarks>
    public bool TryCancel(OperationId id)
    {
        if (_states.GetValueOrDefault(id.Value)
            is OperationState.Completed or OperationState.Cancelled or OperationState.Cancelling)
        {
            return false;
        }

        _cancelled.Add(id);
        Transition(id, _states.GetValueOrDefault(id.Value) == OperationState.Queued
            ? OperationState.Cancelled
            : OperationState.Cancelling);
        return true;
    }

    /// <summary>Completes a cancellation that <see cref="TryCancel"/> started.</summary>
    public void FinishCancel(OperationId id) => Transition(id, OperationState.Cancelled);

    private readonly Dictionary<string, string> _destinations = new(StringComparer.Ordinal);

    /// <summary>Reports a destination on the operation, as the real queue does from the request.</summary>
    public void SetDestination(OperationId id, string destination)
    {
        _destinations[id.Value] = destination;
        Transition(id, _states.GetValueOrDefault(id.Value, OperationState.Running));
    }

    public int RemoveFinishedOperations()
    {
        var finished = Started
            .Where(id => _states.GetValueOrDefault(id.Value)
                is OperationState.Completed or OperationState.Cancelled or OperationState.Failed)
            .ToList();

        foreach (var id in finished)
        {
            _states.Remove(id.Value);
        }

        return finished.Count;
    }

    public IReadOnlyList<OperationSnapshot> Operations() =>
        [.. Started.Where(id => _states.ContainsKey(id.Value)).Select(id => Snapshot(id, _states[id.Value]))];

    /// <summary>Emits a progress report exactly as the queue would, for a test to observe.</summary>
    public void Report(OperationProgress progress) => ProgressReported?.Invoke(this, progress);

    private void Transition(OperationId id, OperationState state)
    {
        _states[id.Value] = state;

        // The real queue stamps these; a fake that did not would let a duration bug pass unnoticed.
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        _started.TryAdd(id.Value, now);
        if (state is OperationState.Completed or OperationState.Cancelled or OperationState.Failed)
        {
            _finished[id.Value] = now + TimeSpan.FromSeconds(9);
        }

        OperationChanged?.Invoke(this, Snapshot(id, state));
    }

    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _finished = new(StringComparer.Ordinal);

    private OperationSnapshot Snapshot(OperationId id, OperationState state) =>
        new(
            id,
            TestsStarted.Contains(id) ? OperationType.Test
                : CreatesStarted.Contains(id) ? OperationType.Create
                : ManagesStarted.Contains(id) ? OperationType.Add
                : OperationType.Extract,
            state,
            _names.GetValueOrDefault(id.Value, "fake.7z"),
            null,
            state == OperationState.Failed
                ? ArchiveError.Create(ArchiveErrorCode.IoFailure, "the disk went away")
                : null,
            _started.GetValueOrDefault(id.Value),
            _finished.TryGetValue(id.Value, out var f) ? f : null,
            Destination: _destinations.GetValueOrDefault(id.Value),
            // A test writes nothing and cannot be suspended, and the fake says so for the same reason
            // the engine does: a Pause button that would be refused must not be offered.
            SupportsPause: !TestsStarted.Contains(id));

    /// <summary>Drives an operation into a failed state, which the queue would do on an exception.</summary>
    public void Fail(OperationId id) => Transition(id, OperationState.Failed);

    private sealed class FixedClock : IClock
    {
        private long _tick;

        public DateTimeOffset UtcNow =>
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(_tick++);
    }

    /// <summary>The verdict to hand back, or null for a clean one.</summary>
    public ArchiveTestResult? TestResult { get; set; }

    /// <summary>Set to make the test itself fail, which is not the same as an archive failing its test.</summary>
    public ArchiveOperationException? TestFailure { get; set; }

    /// <summary>Ids handed out by <see cref="StartTest"/>, so a test can assert it was enqueued at all.</summary>
    public List<OperationId> TestsStarted { get; } = [];

    public OperationId StartTest(string archivePath)
    {
        var id = _ids.Next(OperationType.Test);
        TestsStarted.Add(id);
        Started.Add(id);
        _names[id.Value] = System.IO.Path.GetFileName(archivePath);
        Transition(id, OperationState.Running);
        return id;
    }

    /// <summary>Makes the next operation end cancelled, the way a user pressing Cancel would.</summary>
    public bool CancelNextOperation { get; set; }

    /// <summary>
    /// Makes the next operation end failed, the way the queue records a thrown error.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as a cancellation — the outcome is null either way — because that is
    /// exactly the ambiguity the view models have to resolve from the snapshot rather than guess.
    /// </remarks>
    public bool FailNextOperation { get; set; }

    public Task<ArchiveTestResult?> WaitForTestAsync(OperationId id, CancellationToken cancellationToken)
    {
        if (TestFailure is not null)
        {
            throw TestFailure;
        }

        if (CancelNextOperation)
        {
            CancelNextOperation = false;
            Transition(id, OperationState.Cancelled);
            return Task.FromResult<ArchiveTestResult?>(null);
        }

        if (_states.TryGetValue(id.Value, out var state)
            && state is OperationState.Cancelled or OperationState.Failed)
        {
            return Task.FromResult<ArchiveTestResult?>(null);
        }

        Transition(id, OperationState.Completed);
        return Task.FromResult<ArchiveTestResult?>(
            TestResult ?? new ArchiveTestResult { Status = ArchiveTestStatus.Valid });
    }

    /// <summary>Ids handed out by <see cref="StartCreate"/>, and what each was asked to build.</summary>
    public List<OperationId> CreatesStarted { get; } = [];

    public string? LastCreateTarget { get; private set; }

    public IReadOnlyList<string>? LastCreateSources { get; private set; }

    public CompressionOptions? LastCreateOptions { get; private set; }

    public bool LastCreateOverwrite { get; private set; }

    /// <summary>Set to make creation refuse the way the publisher refuses an existing archive.</summary>
    public bool TargetAlreadyExists { get; set; }

    public CreateOutcome? CreateResult { get; set; }

    public FormatCapabilityReport ReportFor(ArchiveFormat format) => new(format, CapabilitiesFor(format));

    public OperationId StartCreate(
        string targetPath,
        IReadOnlyList<string> sources,
        CompressionOptions options,
        bool overwrite)
    {
        LastCreateTarget = targetPath;
        LastCreateSources = sources;
        LastCreateOptions = options;
        LastCreateOverwrite = overwrite;

        var id = _ids.Next(OperationType.Create);
        CreatesStarted.Add(id);
        Started.Add(id);
        _names[id.Value] = System.IO.Path.GetFileName(targetPath);
        Transition(id, OperationState.Running);
        return id;
    }

    public Task<CreateOutcome?> WaitForCreateAsync(OperationId id, CancellationToken cancellationToken)
    {
        if (TargetAlreadyExists && !LastCreateOverwrite)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.IoFailure,
                $"'{LastCreateTarget}' already exists.",
                LastCreateTarget));
        }

        if (CancelNextOperation)
        {
            CancelNextOperation = false;
            Transition(id, OperationState.Cancelled);
            return Task.FromResult<CreateOutcome?>(null);
        }

        if (FailNextOperation)
        {
            FailNextOperation = false;
            Transition(id, OperationState.Failed);
            return Task.FromResult<CreateOutcome?>(null);
        }

        if (_states.TryGetValue(id.Value, out var state)
            && state is OperationState.Cancelled or OperationState.Failed)
        {
            return Task.FromResult<CreateOutcome?>(null);
        }

        Transition(id, OperationState.Completed);
        return Task.FromResult<CreateOutcome?>(
            CreateResult ?? new CreateOutcome { ArchivePath = LastCreateTarget ?? "out.7z", EntriesAdded = 1 });
    }

    // ---------- manage (add / remove / rename entries in an existing archive) ----------

    /// <summary>Ids handed out by <see cref="StartManage"/>.</summary>
    public List<OperationId> ManagesStarted { get; } = [];

    public string? LastManageArchivePath { get; private set; }

    public IReadOnlyList<string>? LastManageEntriesToRemove { get; private set; }

    public IReadOnlyList<EntryRenameRequest>? LastManageEntriesToRename { get; private set; }

    public IReadOnlyList<string>? LastManageSourcesToAdd { get; private set; }

    public CompressionOptions? LastManageOptions { get; private set; }

    public ManageOutcome? ManageResult { get; set; }

    public OperationId StartManage(
        string archivePath,
        IReadOnlyList<string> entriesToRemove,
        IReadOnlyList<EntryRenameRequest> entriesToRename,
        IReadOnlyList<string> sourcesToAdd,
        CompressionOptions options)
    {
        LastManageArchivePath = archivePath;
        LastManageEntriesToRemove = entriesToRemove;
        LastManageEntriesToRename = entriesToRename;
        LastManageSourcesToAdd = sourcesToAdd;
        LastManageOptions = options;

        var id = _ids.Next(OperationType.Add);
        ManagesStarted.Add(id);
        Started.Add(id);
        _names[id.Value] = System.IO.Path.GetFileName(archivePath);
        Transition(id, OperationState.Running);
        return id;
    }

    public Task<ManageOutcome?> WaitForManageAsync(OperationId id, CancellationToken cancellationToken)
    {
        if (CancelNextOperation)
        {
            CancelNextOperation = false;
            Transition(id, OperationState.Cancelled);
            return Task.FromResult<ManageOutcome?>(null);
        }

        if (FailNextOperation)
        {
            FailNextOperation = false;
            Transition(id, OperationState.Failed);
            return Task.FromResult<ManageOutcome?>(null);
        }

        if (_states.TryGetValue(id.Value, out var state)
            && state is OperationState.Cancelled or OperationState.Failed)
        {
            return Task.FromResult<ManageOutcome?>(null);
        }

        Transition(id, OperationState.Completed);
        return Task.FromResult<ManageOutcome?>(
            ManageResult ?? new ManageOutcome { ArchivePath = LastManageArchivePath ?? "out.7z" });
    }

    /// <summary>Builds a file entry with a validated path.</summary>
    public static ArchiveEntry File(string path, long size = 10)
    {
        ArchivePath.TryCreate(path, out var validated, out _);
        return new ArchiveEntry
        {
            RawPath = path,
            Path = validated,
            Kind = EntryKind.File,
            UncompressedSize = ByteSize.FromBytes(size),
        };
    }

    public static ArchiveEntry Directory(string path)
    {
        ArchivePath.TryCreate(path, out var validated, out _);
        return new ArchiveEntry { RawPath = path, Path = validated, Kind = EntryKind.Directory };
    }

    /// <summary>An entry the path policy refused: listable, reportable, and not extractable.</summary>
    public static ArchiveEntry Rejected(string rawPath)
    {
        ArchivePath.TryCreate(rawPath, out _, out var rejection);
        return new ArchiveEntry
        {
            RawPath = rawPath,
            Path = null,
            Rejection = rejection,
            Kind = EntryKind.File,
        };
    }
}
