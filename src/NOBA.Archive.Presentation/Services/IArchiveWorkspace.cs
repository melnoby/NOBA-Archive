using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Presentation.Services;

/// <summary>
/// Everything the UI is allowed to ask the engine for.
/// </summary>
/// <remarks>
/// <para>
/// A deliberately narrow seam, and narrow in a specific direction: it exposes <em>operations</em> and
/// <em>capabilities</em>, and nothing that would let a screen make a policy decision. There is no way
/// through this interface to validate a path, resolve a conflict, decide whether an entry is safe, or
/// choose a destination — those live in the security and application layers and stay there.
/// </para>
/// <para>
/// It also exists so the view models are testable without a filesystem or a native library, which is
/// what lets the whole presentation layer be validated on a machine that cannot build WinUI at all.
/// </para>
/// </remarks>
public interface IArchiveWorkspace
{
    /// <summary>What this build can actually do with a format, straight from the provider registry.</summary>
    ArchiveCapabilities CapabilitiesFor(ArchiveFormat format);

    /// <summary>Formats this build has a provider for.</summary>
    IReadOnlyList<ArchiveFormat> AvailableFormats { get; }

    ValueTask<ListOutcome> ListAsync(string archivePath, CancellationToken cancellationToken);

    /// <summary>
    /// Starts an extraction and returns at once with its identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It returns an id rather than an outcome because that is what makes the operation a <em>thing</em>
    /// the user can act on. The previous shape — one awaited call that produced the result — had nowhere
    /// to put a pause, a cancel, or a percentage: by the time the caller had anything in hand the work
    /// was over. A large extraction was therefore a frozen toolbar and no way out, which is not a missing
    /// nicety but the program holding the machine until it is finished.
    /// </para>
    /// <para>
    /// The queue behind this owns the state. Nothing above the seam may decide that an operation is
    /// paused or cancelled; it asks, and it is told what happened.
    /// </para>
    /// </remarks>
    /// <param name="conflictPolicy">
    /// What to do when the destination already holds a file. Passed rather than left to a default: the
    /// engine's default is <see cref="ConflictPolicy.Ask"/>, which is right, but "right by accident"
    /// is one refactor away from silently overwriting somebody's work. One policy, chosen once, carried
    /// to the writer — the same value the CLI's <c>--conflict</c> takes, so a future Explorer verb has
    /// a contract to call rather than a decision to re-invent.
    /// </param>
    OperationId StartExtract(
        string archivePath,
        string destination,
        IReadOnlyList<string>? selectedEntries,
        ConflictPolicy conflictPolicy);

    /// <summary>Completes when the operation reaches a terminal state. Null if it did not produce one.</summary>
    Task<ExtractOutcome?> WaitForExtractAsync(OperationId id, CancellationToken cancellationToken);

    /// <summary>Asks the queue to pause. False when the operation is not in a state that allows it.</summary>
    bool TryPause(OperationId id);

    bool TryResume(OperationId id);

    bool TryCancel(OperationId id);

    /// <summary>Every operation the queue knows about, running or finished.</summary>
    IReadOnlyList<OperationSnapshot> Operations();

    /// <summary>Forgets finished operations and returns how many went. Running ones are untouched.</summary>
    int RemoveFinishedOperations();

    /// <summary>
    /// Progress, as the engine measures it.
    /// </summary>
    /// <remarks>
    /// Raised from whichever thread the work is on — the queue builds its <c>Progress</c> sink inside a
    /// pool thread, so there is no synchronisation context to post back to. The subscriber owns the
    /// marshalling, because the subscriber is the one with the thread affinity.
    /// </remarks>
    event EventHandler<OperationProgress>? ProgressReported;

    /// <summary>A state transition: queued, running, paused, completed, cancelled, failed.</summary>
    event EventHandler<OperationSnapshot>? OperationChanged;

    /// <summary>
    /// Starts a test and returns at once with its identity.
    /// </summary>
    /// <remarks>
    /// Testing used to call the handler directly, outside the queue: no progress, no cancel, and no
    /// row in the history. On a 3,800-entry archive that is several seconds of a window that cannot
    /// say what it is doing and cannot be stopped — and it bypassed the one component that is supposed
    /// to own every unit of work. It is an operation like any other, and now it is one.
    /// </remarks>
    OperationId StartTest(string archivePath);

    /// <summary>Completes when the test reaches a terminal state. Null if it was cancelled or failed.</summary>
    Task<ArchiveTestResult?> WaitForTestAsync(OperationId id, CancellationToken cancellationToken);

    /// <summary>
    /// What the product may offer for a format, asked once and answered by the engine.
    /// </summary>
    /// <remarks>
    /// The screens ask this rather than knowing anything about formats themselves. A view that decided
    /// "7z can be encrypted" for itself would be a second, unversioned copy of the provider's
    /// capability set, and the day the two disagree the user gets an archive that is not what the
    /// dialog promised. An architecture test forbids the App project from naming a format at all.
    /// </remarks>
    FormatCapabilityReport ReportFor(ArchiveFormat format);

    /// <summary>
    /// Starts building an archive and returns at once with its identity.
    /// </summary>
    /// <remarks>
    /// Same shape as extraction and for the same reason: creation of a folder of any size is long
    /// enough that the user must be able to watch it and stop it. The engine stages the archive and
    /// publishes it only on success, so a cancelled or failed creation leaves any existing file at
    /// <paramref name="targetPath"/> exactly as it was.
    /// </remarks>
    OperationId StartCreate(
        string targetPath,
        IReadOnlyList<string> sources,
        CompressionOptions options,
        bool overwrite);

    /// <summary>Completes when the creation reaches a terminal state. Null if it did not produce one.</summary>
    Task<CreateOutcome?> WaitForCreateAsync(OperationId id, CancellationToken cancellationToken);

    /// <summary>
    /// Starts rewriting an open archive — adding, removing, and renaming entries — and returns at once
    /// with its identity.
    /// </summary>
    /// <remarks>
    /// Same shape as creation and extraction, and for the same reason: a rewrite walks every surviving
    /// entry before anything is published, which on a large archive is long enough that the user must
    /// be able to watch it and stop it. Paths travel as raw strings, exactly as <see cref="StartExtract"/>
    /// already does — turning them into <see cref="Core.Primitives.ArchivePath"/> is the engine's job,
    /// and a string that cannot be represented is refused there, not guessed at here.
    /// </remarks>
    OperationId StartManage(
        string archivePath,
        IReadOnlyList<string> entriesToRemove,
        IReadOnlyList<EntryRenameRequest> entriesToRename,
        IReadOnlyList<string> sourcesToAdd,
        CompressionOptions options);

    /// <summary>Completes when the rewrite reaches a terminal state. Null if it did not produce one.</summary>
    Task<ManageOutcome?> WaitForManageAsync(OperationId id, CancellationToken cancellationToken);
}

/// <summary>One entry's current and intended path, as raw strings from the screen.</summary>
/// <remarks>
/// Deliberately not <see cref="EntryRename"/>: that record carries an already-validated
/// <see cref="Core.Primitives.ArchivePath"/> on each side, which is an engine concern. The screen only
/// knows the path as it was displayed; turning it into something the engine will accept — or refusing
/// it — happens below this seam, the same way <see cref="IArchiveWorkspace.StartExtract"/> already
/// treats a selection as strings until it reaches the adapter.
/// </remarks>
public sealed record EntryRenameRequest(string From, string To);
