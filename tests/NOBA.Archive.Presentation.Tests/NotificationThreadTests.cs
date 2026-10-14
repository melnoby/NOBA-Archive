using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Application.Operations;
using NOBA.Archive.Presentation.Services;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Which thread a notification arrives on: <c>UIT-001…002</c>.
/// </summary>
/// <remarks>
/// <para>
/// The app closed the instant a file was chosen. <see cref="BrowseViewModel.OpenAsync"/> awaits with
/// <c>ConfigureAwait(false)</c>, so everything after that await — including every
/// <c>PropertyChanged</c> — runs on a thread-pool thread. The WinUI page was subscribed with a handler
/// that touched its elements directly, and a WinUI element may only be touched from the thread that
/// created it: <c>RPC_E_WRONGTHREAD</c>, thrown out of an <c>async void</c> handler, which ends the
/// process.
/// </para>
/// <para>
/// The fix is in the XAML page, which cannot be built here, so these tests do not verify it. They pin
/// down the fact that <em>makes it necessary</em>, which is the durable half: these view models raise
/// notifications off the calling thread, and any UI bound to them must marshal. If someone later
/// "simplifies" the page's handler back to a direct call, this file is what tells them why it was not
/// simple.
/// </para>
/// </remarks>
public sealed class NotificationThreadTests
{
    /// <summary>
    /// UIT-001: opening an archive raises notifications on a thread other than the caller's.
    /// </summary>
    /// <remarks>
    /// Run from a dedicated thread rather than the test's own, so the assertion cannot pass or fail by
    /// chance: a thread-pool continuation is never the dedicated thread, whatever the pool is doing.
    /// </remarks>
    [Fact]
    public void Opening_an_archive_notifies_off_the_calling_thread()
    {
        var model = new BrowseViewModel(new YieldingWorkspace());
        var threads = new List<int>();

        model.PropertyChanged += (_, _) =>
        {
            lock (threads)
            {
                threads.Add(Environment.CurrentManagedThreadId);
            }
        };

        var callerThreadId = 0;
        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            model.OpenAsync("archive.7z").AsTask().GetAwaiter().GetResult();
        });

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(10)), "OpenAsync did not finish");

        lock (threads)
        {
            Assert.True(threads.Count > 0, "no notification was raised at all");
            Assert.True(
                threads.Exists(id => id != callerThreadId),
                "every notification arrived on the calling thread; a UI bound to this model would have " +
                "been safe by accident, and the page's marshalling is what must keep it safe on purpose");
        }
    }

    /// <summary>UIT-002: the same is true of extraction, which is the next path to bind.</summary>
    [Fact]
    public async Task Extracting_notifies_off_the_calling_thread()
    {
        var model = new BrowseViewModel(new YieldingWorkspace());

        // Awaited, not blocked on: this is only arrangement. The blocking call that matters is the one
        // on the dedicated thread below, which is the whole subject of the test and stays as it is.
        await model.OpenAsync("archive.7z", TestContext.Token);

        var threads = new List<int>();
        model.PropertyChanged += (_, _) =>
        {
            lock (threads)
            {
                threads.Add(Environment.CurrentManagedThreadId);
            }
        };

        var callerThreadId = 0;
        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            model.ExtractAsync("C:\\out", selectionOnly: false).AsTask().GetAwaiter().GetResult();
        });

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(10)), "ExtractAsync did not finish");

        lock (threads)
        {
            Assert.True(threads.Count > 0, "no notification was raised at all");
            Assert.True(threads.Exists(id => id != callerThreadId), "every notification stayed on the caller");
        }
    }

    /// <summary>
    /// A workspace that really suspends, so the continuation really moves.
    /// </summary>
    /// <remarks>
    /// <see cref="FakeWorkspace"/> completes synchronously, which would leave the await on the caller's
    /// thread and make these tests pass for the wrong reason.
    /// </remarks>
    private sealed class YieldingWorkspace : IArchiveWorkspace
    {
        public IReadOnlyList<ArchiveFormat> AvailableFormats => [ArchiveFormat.SevenZip];

        public ArchiveCapabilities CapabilitiesFor(ArchiveFormat format) => ArchiveCapabilities.ReadOnlySet;

        public async ValueTask<ListOutcome> ListAsync(string archivePath, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new ListOutcome(
                ArchiveFormat.SevenZip,
                new ArchiveStatistics(),
                [FakeWorkspace.File("readme.txt")]);
        }

        private readonly OperationIdFactory _ids = new(new StepClock());

        public event EventHandler<OperationProgress>? ProgressReported;

        public event EventHandler<OperationSnapshot>? OperationChanged;

        public OperationId StartExtract(
            string archivePath,
            string destination,
            IReadOnlyList<string>? selectedEntries,
            ConflictPolicy conflictPolicy) => _ids.Next(OperationType.Extract);

        /// <summary>Suspends for real, so the continuation genuinely moves off the calling thread.</summary>
        public async Task<ExtractOutcome?> WaitForExtractAsync(OperationId id, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new ExtractOutcome { Format = ArchiveFormat.SevenZip };
        }

        public bool TryPause(OperationId id) => false;

        public bool TryResume(OperationId id) => false;

        public bool TryCancel(OperationId id) => false;

        public IReadOnlyList<OperationSnapshot> Operations() => [];

        public int RemoveFinishedOperations() => 0;

        /// <summary>Never raised here; declared so the events are not "unused" warnings.</summary>
        internal void RaiseNothing()
        {
            ProgressReported?.Invoke(this, OperationProgress.Starting(_ids.Next(OperationType.Extract), OperationType.Extract));
            OperationChanged?.Invoke(this, new OperationSnapshot(
                _ids.Next(OperationType.Extract), OperationType.Extract, OperationState.Queued, "x", null, null));
        }

        private sealed class StepClock : IClock
        {
            private long _tick;

            public DateTimeOffset UtcNow =>
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(_tick++);
        }

        public OperationId StartTest(string archivePath) => _ids.Next(OperationType.Test);

        public FormatCapabilityReport ReportFor(ArchiveFormat format) =>
            new(format, ArchiveCapabilities.ReadOnlySet);

        public OperationId StartCreate(
            string targetPath,
            IReadOnlyList<string> sources,
            CompressionOptions options,
            bool overwrite) => _ids.Next(OperationType.Create);

        public async Task<CreateOutcome?> WaitForCreateAsync(OperationId id, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new CreateOutcome { ArchivePath = "out.7z" };
        }

        public async Task<ArchiveTestResult?> WaitForTestAsync(OperationId id, CancellationToken cancellationToken)
        {
            // Really suspends, like the extraction above: the point of this fake is that the
            // continuation lands somewhere other than the caller's thread.
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new ArchiveTestResult { Status = ArchiveTestStatus.Valid };
        }

        public OperationId StartManage(
            string archivePath,
            IReadOnlyList<string> entriesToRemove,
            IReadOnlyList<EntryRenameRequest> entriesToRename,
            IReadOnlyList<string> sourcesToAdd,
            CompressionOptions options) => _ids.Next(OperationType.Add);

        public async Task<ManageOutcome?> WaitForManageAsync(OperationId id, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new ManageOutcome { ArchivePath = "out.7z" };
        }
    }
}
