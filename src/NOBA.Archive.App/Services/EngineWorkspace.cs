using System.Collections.Concurrent;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.App.Services;

/// <summary>
/// The adapter between the view models and the engine's handlers.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately dull, and that is the point: it translates and forwards, and there is nowhere in it for
/// a decision to hide. Every question the UI asks — what can this format do, what is in this archive,
/// extract these entries — is answered by the same handlers the CLI calls.
/// </para>
/// <para>
/// It lives in the app rather than in the presentation layer because it is the only piece that needs
/// the composed engine. The view models see the interface, which is why they can be tested with no
/// filesystem, no native library, and no Windows.
/// </para>
/// </remarks>
internal sealed class EngineWorkspace : IArchiveWorkspace
{
    private readonly IArchiveProviderRegistry _registry;
    private readonly ListArchiveHandler _list;
    private readonly ExtractArchiveHandler _extract;
    private readonly TestArchiveHandler _test;
    private readonly CreateArchiveHandler _create;
    private readonly IOperationQueue _queue;

    /// <summary>
    /// Outcomes waiting to be collected by whoever awaited the operation.
    /// </summary>
    /// <remarks>
    /// The queue records that an operation finished, not what it produced - deliberately, since it
    /// schedules work of every type and has no business knowing what an extraction returns. So the
    /// result is parked here by id and taken out once.
    /// </remarks>
    private readonly ConcurrentDictionary<string, ExtractOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ArchiveTestResult> _testResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CreateOutcome> _created = new(StringComparer.Ordinal);

    public EngineWorkspace(
        IArchiveProviderRegistry registry,
        ListArchiveHandler list,
        ExtractArchiveHandler extract,
        TestArchiveHandler test,
        CreateArchiveHandler create,
        IOperationQueue queue)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(extract);
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(queue);

        _registry = registry;
        _list = list;
        _extract = extract;
        _create = create;
        _test = test;
        _queue = queue;
    }

    public IReadOnlyList<ArchiveFormat> AvailableFormats => _registry.SupportedFormats;

    public ArchiveCapabilities CapabilitiesFor(ArchiveFormat format) => _registry.CapabilitiesFor(format);

    public async ValueTask<ListOutcome> ListAsync(string archivePath, CancellationToken cancellationToken) =>
        await _list.HandleAsync(new ListRequest { ArchivePath = archivePath }, cancellationToken)
            .ConfigureAwait(false);

    public event EventHandler<OperationProgress>? ProgressReported
    {
        add => _queue.ProgressReported += value;
        remove => _queue.ProgressReported -= value;
    }

    public event EventHandler<OperationSnapshot>? OperationChanged
    {
        add => _queue.OperationChanged += value;
        remove => _queue.OperationChanged -= value;
    }

    public IReadOnlyList<OperationSnapshot> Operations() => _queue.Snapshot();

    public int RemoveFinishedOperations() => _queue.RemoveFinished();

    public bool TryPause(OperationId id) => _queue.TryPause(id);

    public bool TryResume(OperationId id) => _queue.TryResume(id);

    public bool TryCancel(OperationId id) => _queue.TryCancel(id);

    /// <summary>
    /// Puts an extraction on the queue and hands back its id.
    /// </summary>
    /// <remarks>
    /// This is the change Feature 02 turns on. The previous version minted an <c>OperationId</c> of its
    /// own and called the handler directly, which meant the queue — with its pause gate, its
    /// cancellation and its state machine, all of it already written and tested — was registered in the
    /// container and never reached. An extraction was therefore uncancellable, and the id it carried
    /// identified nothing anybody could act on.
    /// </remarks>
    public OperationId StartExtract(
        string archivePath,
        string destination,
        IReadOnlyList<string>? selectedEntries,
        ConflictPolicy conflictPolicy)
    {
        var request = BuildExtractRequest(archivePath, destination, selectedEntries, conflictPolicy);

        return _queue.Enqueue(new OperationRequest(
            OperationType.Extract,
            Path.GetFileName(archivePath),
            async (context, cancellationToken) =>
            {
                var outcome = await _extract
                    .HandleAsync(request, context.Id, context.Progress, context.PauseToken, cancellationToken)
                    .ConfigureAwait(false);

                _outcomes[context.Id.Value] = outcome;
            })
        {
            // The folder the picker returned, recorded on the operation so the screen can say where the
            // files are going. Taken from the built request rather than the argument, so what is shown
            // is the destination the engine was actually given.
            Destination = request.Options.DestinationRoot,
        });
    }

    /// <summary>
    /// Waits for the operation to reach a terminal state and returns what it produced.
    /// </summary>
    /// <remarks>
    /// Null for a cancelled or failed operation rather than an exception: the queue has already
    /// recorded why, and the caller can read it from the snapshot. Throwing here would report the same
    /// event twice in two different shapes.
    /// </remarks>
    public async Task<ExtractOutcome?> WaitForExtractAsync(OperationId id, CancellationToken cancellationToken)
    {
        await _queue.WaitForCompletionAsync(id, cancellationToken).ConfigureAwait(false);
        return _outcomes.TryRemove(id.Value, out var outcome) ? outcome : null;
    }

    /// <summary>
    /// Turns the screen's strings into a request the engine will accept.
    /// </summary>
    /// <remarks>
    /// Static because it reads nothing from this adapter: it is a pure translation, which is exactly
    /// what a seam adapter should be made of.
    /// </remarks>
    private static ExtractRequest BuildExtractRequest(
        string archivePath,
        string destination,
        IReadOnlyList<string>? selectedEntries,
        ConflictPolicy conflictPolicy)
    {
        // The selection arrives as strings the view model took from validated entries. Converting them
        // back through ArchivePath means anything that is not a legal entry path is dropped here, before
        // the request is built - the writer would refuse it as well, and neither check trusts the other.
        IReadOnlyList<ArchivePath>? selection = null;
        if (selectedEntries is not null)
        {
            var paths = new List<ArchivePath>(selectedEntries.Count);
            foreach (var candidate in selectedEntries)
            {
                if (ArchivePath.TryCreate(candidate, out var path, out _))
                {
                    paths.Add(path);
                }
            }

            selection = paths;
        }

        var options = new ExtractionOptions
        {
            DestinationRoot = destination,
            SelectedEntries = selection,
            ConflictPolicy = conflictPolicy,

            // The archive whose zone the extracted files inherit.
            //
            // This was missing, and its absence was silent. PropagateMarkOfTheWeb defaults to true, but
            // propagation also needs to know *what to propagate from* - a null SourceArchivePath turns
            // it off for the operation. So every extraction started from the window produced files with
            // no Zone.Identifier, while the option said they were protected and the tests that cover
            // propagation passed, because they set the path themselves. A file downloaded from the
            // internet and extracted here arrived on disk looking local: SmartScreen would not warn, and
            // Office would open it without Protected View. That is THREAT_MODEL.md T7, and it reached
            // the one surface an ordinary user actually uses.
            SourceArchivePath = archivePath,
        };

        return new ExtractRequest { ArchivePath = archivePath, Options = options };
    }

    /// <summary>
    /// Enqueues a test, exactly as an extraction is enqueued.
    /// </summary>
    /// <remarks>
    /// <c>SupportsPause = false</c> and no destination, both stated rather than inferred: the backend
    /// verifies the whole archive in one call with no copy loop to stand in, and it writes nothing
    /// anywhere. Saying so on the operation is what lets the screen disable Pause instead of offering
    /// a button that would be refused.
    /// </remarks>
    public OperationId StartTest(string archivePath)
    {
        var request = new TestRequest { ArchivePath = archivePath };

        return _queue.Enqueue(new OperationRequest(
            OperationType.Test,
            Path.GetFileName(archivePath),
            async (context, cancellationToken) =>
            {
                var result = await _test
                    .HandleAsync(request, context.Id, context.Progress, cancellationToken)
                    .ConfigureAwait(false);

                _testResults[context.Id.Value] = result;
            })
        {
            SupportsPause = false,
        });
    }

    public async Task<ArchiveTestResult?> WaitForTestAsync(OperationId id, CancellationToken cancellationToken)
    {
        await _queue.WaitForCompletionAsync(id, cancellationToken).ConfigureAwait(false);
        return _testResults.TryRemove(id.Value, out var result) ? result : null;
    }

    public FormatCapabilityReport ReportFor(ArchiveFormat format) =>
        new(format, _registry.CapabilitiesFor(format));

    /// <summary>
    /// Enqueues a creation, with the archive being built as the destination.
    /// </summary>
    /// <remarks>
    /// <c>SupportsPause</c> is left at its default because the create handler genuinely honours the
    /// token — between entries, and inside a large one through the stream it hands the builder. It is
    /// stated by the engine, not assumed by the screen.
    /// </remarks>
    public OperationId StartCreate(
        string targetPath,
        IReadOnlyList<string> sources,
        CompressionOptions options,
        bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var request = new CreateRequest
        {
            TargetPath = targetPath,
            Sources = [.. sources],
            Options = options,
            Overwrite = overwrite,
        };

        return _queue.Enqueue(new OperationRequest(
            OperationType.Create,
            Path.GetFileName(targetPath),
            async (context, cancellationToken) =>
            {
                var outcome = await _create
                    .HandleAsync(request, context.Id, context.Progress, context.PauseToken, cancellationToken)
                    .ConfigureAwait(false);

                _created[context.Id.Value] = outcome;
            })
        {
            Destination = targetPath,
        });
    }

    public async Task<CreateOutcome?> WaitForCreateAsync(OperationId id, CancellationToken cancellationToken)
    {
        await _queue.WaitForCompletionAsync(id, cancellationToken).ConfigureAwait(false);
        return _created.TryRemove(id.Value, out var outcome) ? outcome : null;
    }
}
