using System.Collections.ObjectModel;
using System.Globalization;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>One row of the file list.</summary>
public sealed record BrowseRow(ArchiveNode Node, int Depth)
{
    public bool IsFolder => Node.IsFolder;

    public bool IsRejected => Node.Entry?.IsRejected ?? false;

    public bool IsEncrypted => Node.IsEncrypted;

    /// <summary>Why the entry was refused, for the row's own label. Never colour alone.</summary>
    public string? RejectionDetail => Node.Entry?.Rejection?.Detail;
}

/// <summary>
/// The browse screen: open an archive, see what is in it, search it, extract from it.
/// </summary>
/// <remarks>
/// <para>
/// It holds no archive logic and no policy. Listing, extraction, path validation, conflict resolution
/// and the resource guard all happen behind <see cref="IArchiveWorkspace"/>; what lives here is the
/// state a screen needs — which node is selected, what the search box contains, whether an operation
/// is running — and the presentation of results.
/// </para>
/// <para>
/// The one thing it does decide is what to <em>show</em>, and there the rule is that a refused entry
/// is shown and labelled rather than hidden. A user is entitled to know that an archive tried
/// something.
/// </para>
/// </remarks>
public sealed class BrowseViewModel : ObservableObject
{
    private readonly IArchiveWorkspace _workspace;

    private string? _archivePath;
    private ArchiveNode? _root;
    private ArchiveNode? _currentFolder;
    private string _search = string.Empty;
    private bool _isBusy;
    private string? _status;
    private ArchiveFormat? _format;
    private ArchiveStatistics? _statistics;
    private EntryNameEncodingInfo? _nameEncoding;
    private CultureInfo _culture = CultureInfo.InvariantCulture;
    private ArchiveTestResult? _testResult;
    private bool _isTesting;
    private OperationsViewModel? _operations;

    public BrowseViewModel(IArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <summary>
    /// Where started operations are registered.
    /// </summary>
    /// <remarks>
    /// Set by the shell rather than injected, because the operations outlive this screen and belong to
    /// the shell. Optional so the browse view model stays testable on its own.
    /// </remarks>
    public void UseOperations(OperationsViewModel operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        _operations = operations;
    }

    public ObservableCollection<BrowseRow> Rows { get; } = [];

    public ObservableCollection<ArchiveNode> Breadcrumbs { get; } = [];

    public ObservableCollection<ArchiveNode> Selection { get; } = [];

    public string? ArchivePath
    {
        get => _archivePath;
        private set => Set(ref _archivePath, value);
    }

    /// <summary>The archive's own name, isolated: it is shown inside a sentence in either language.</summary>
    public string ArchiveDisplayName =>
        _archivePath is null ? string.Empty : BidiText.Isolate(Path.GetFileName(_archivePath));

    public ArchiveFormat? Format
    {
        get => _format;
        private set => Set(ref _format, value);
    }

    public ArchiveStatistics? Statistics
    {
        get => _statistics;
        private set => Set(ref _statistics, value);
    }

    /// <summary>
    /// How entry names were decoded, when the format did not say.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than hidden because a guessed encoding produces names that look plausible and
    /// are wrong, and the user is the only one who can tell. The UI shows the code page and offers to
    /// reinterpret; it never presents a guess as a fact.
    /// </remarks>
    public EntryNameEncodingInfo? NameEncoding
    {
        get => _nameEncoding;
        private set
        {
            if (Set(ref _nameEncoding, value))
            {
                Raise(nameof(ShowEncodingWarning));
            }
        }
    }

    public bool ShowEncodingWarning => _nameEncoding?.IsGuess ?? false;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanExtractSelection));
                Raise(nameof(CanExtractAll));
                Raise(nameof(CanTest));
            }
        }
    }

    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Refresh();
            }
        }
    }

    /// <summary>Set by the shell so numbers format in the UI language.</summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Set(ref _culture, value))
            {
                Raise(nameof(SummaryLine));
            }
        }
    }

    public bool HasArchive => _root is not null;

    public bool CanExtractAll => HasArchive && !IsBusy;

    public bool CanExtractSelection => HasArchive && !IsBusy && Selection.Count > 0;

    /// <summary>
    /// Whether this build can actually test the open archive.
    /// </summary>
    /// <remarks>
    /// Asked of the provider registry, not hard-coded. The button used to be disabled in markup, which
    /// is the same visible result for the wrong reason: it said "not implemented" when the truth is
    /// "depends on the format", and it would have gone on saying it after the feature existed.
    /// </remarks>
    public bool CanTest =>
        HasArchive
        && !IsBusy
        && _format is { } format
        && _workspace.CapabilitiesFor(format).Has(ArchiveCapabilities.Test);

    /// <summary>
    /// What the open archive's format can do, or null when nothing is open.
    /// </summary>
    /// <remarks>
    /// Exposed so a screen can explain an unavailable action, and so the test panel can say what a
    /// clean verdict actually covered, without the screen ever learning which format it is looking at.
    /// </remarks>
    public FormatCapabilityReport? FormatReport =>
        _format is { } format ? _workspace.ReportFor(format) : null;

    /// <summary>
    /// Every file extension this build can actually open, for the file picker's filter.
    /// </summary>
    /// <remarks>
    /// The picker used to be given <c>.zip</c>, <c>.7z</c> and <c>.rar</c> written into the WinUI page
    /// by hand. Three problems with that, all of them silent: a build with no native library still
    /// offered 7Z and RAR and then failed on the file the user chose; a format added to the registry
    /// would not appear until somebody remembered this list; and it was a format decision living in a
    /// screen, which is exactly what the capability contract exists to prevent. Derived instead, from
    /// the same registry every other answer comes from.
    /// </remarks>
    public IReadOnlyList<string> OpenableExtensions =>
        [.. _workspace.AvailableFormats
            .Where(f => _workspace.CapabilitiesFor(f).Has(ArchiveCapabilities.Open))
            .SelectMany(static f => f.Extensions)
            .Where(static e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>True while a test is running, so the screen can say so rather than appear frozen.</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set => Set(ref _isTesting, value);
    }

    /// <summary>
    /// The last verdict, or null when none has been asked for.
    /// </summary>
    /// <remarks>
    /// The engine's own record, not a flattened string: the screen needs the status, the count and every
    /// finding, and anything this view model summarised here would be a decision taken in the wrong
    /// layer. <see cref="TestReportFormatter"/> turns it into something readable.
    /// </remarks>
    public ArchiveTestResult? TestResult
    {
        get => _testResult;
        private set
        {
            if (Set(ref _testResult, value))
            {
                Raise(nameof(HasTestResult));
            }
        }
    }

    public bool HasTestResult => _testResult is not null;

    /// <summary>Puts the result panel away. The verdict is not a modal, so it needs a way out.</summary>
    public void ClearTest() => TestResult = null;

    /// <summary>
    /// Whether there is a folder above the current one.
    /// </summary>
    /// <remarks>
    /// The UI cannot work this out from the breadcrumb: <c>PathTo</c> stops at the root and yields
    /// nothing for it, so one folder deep the trail has a single entry and "count &gt; 1" is false while
    /// there is very much somewhere to go back to. That inference is exactly how the window ended up
    /// with no way out of the first folder a person opened.
    /// </remarks>
    /// <summary>
    /// What is selected, for the status bar.
    /// </summary>
    /// <remarks>
    /// The status bar was repeating the archive summary that already sits in the header. A status bar
    /// earns its row by saying something no other part of the window does, and what the person has
    /// picked is exactly that.
    /// </remarks>
    public string SelectionSummary
    {
        get
        {
            if (Selection.Count == 0)
            {
                return string.Empty;
            }

            var text = LocalizedStrings.For(UiLanguage.ForCulture(_culture));
            var bytes = Selection.Sum(static node => node.TotalSize.Bytes);
            var count = BidiText.FormatCount(Selection.Count, CultureInfo.InvariantCulture);

            return bytes > 0
                ? $"{count} {text.SelectedLabel} · {BidiText.IsolateAsLtr(ByteSize.FromBytes(bytes).ToDisplayString(CultureInfo.InvariantCulture))}"
                : $"{count} {text.SelectedLabel}";
        }
    }

    public bool CanGoUp => _root is not null && _currentFolder is not null && _currentFolder != _root;

    /// <summary>
    /// The status-bar summary. Counts are LTR runs in both languages.
    /// </summary>
    public string SummaryLine
    {
        get
        {
            if (_statistics is null)
            {
                return string.Empty;
            }

            // Labelled, because "9 · 1" is not a sentence. The counts stay LTR runs in both languages
            // and the total size is the one human-readable size on the screen - every other size is a
            // grouped byte count, because those are read to be compared.
            // Invariant, not the UI culture - the same decision as the timestamp column. These counts
            // sit inside an Arabic sentence, and the shaper turns a digit that follows Arabic text into
            // an Arabic-Indic one. The table beside this line is Western, and a window that reports
            // "٩ ملفات" above a column of "9" is asking the reader to hold two number systems at once.
            var text = LocalizedStrings.For(UiLanguage.ForCulture(_culture));
            var files = BidiText.FormatCount(_statistics.FileCount, CultureInfo.InvariantCulture);
            var folders = BidiText.FormatCount(_statistics.DirectoryCount, CultureInfo.InvariantCulture);
            var parts = new List<string>(4)
            {
                $"{files} {text.FilesLabel}",
                $"{folders} {text.FoldersLabel}",
            };

            if (_root is { } root && root.TotalSize.Bytes > 0)
            {
                parts.Add(BidiText.IsolateAsLtr(root.TotalSize.ToDisplayString(CultureInfo.InvariantCulture)));
            }

            if (_statistics.RejectedEntryCount > 0)
            {
                parts.Add($"{BidiText.FormatCount(_statistics.RejectedEntryCount, CultureInfo.InvariantCulture)} {text.BlockedLabel}");
            }

            // Solid and multi-volume change what extraction costs, and the engine already knows both.
            // Not showing them was the UI withholding what it had.
            // The one number that says whether this archive was worth compressing. ArchiveStatistics
            // has computed it all along.
            if (_statistics.CompressionRatio is { } ratio && ratio > 0)
            {
                parts.Add(BidiText.IsolateAsLtr(ratio.ToString("P0", CultureInfo.InvariantCulture)));
            }

            if (_statistics.IsSolid)
            {
                parts.Add(text.SolidLabel);
            }

            if (_statistics.VolumeCount > 1)
            {
                parts.Add($"{BidiText.FormatCount(_statistics.VolumeCount, CultureInfo.InvariantCulture)} {text.VolumesLabel}");
            }

            return string.Join(" · ", parts);
        }
    }

    public async ValueTask<bool> OpenAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        IsBusy = true;
        Status = null;

        try
        {
            var listing = await _workspace.ListAsync(archivePath, cancellationToken).ConfigureAwait(false);

            ArchivePath = archivePath;
            Format = listing.Format;
            Statistics = listing.Statistics;
            NameEncoding = listing.NameEncoding;
            _root = ArchiveNode.BuildTree(listing.Entries);
            _currentFolder = _root;

            // A verdict belongs to the archive it was asked about, so opening another one retires it.
            TestResult = null;

            Raise(nameof(ArchiveDisplayName));
            Raise(nameof(HasArchive));
            Raise(nameof(SummaryLine));
            Raise(nameof(CanGoUp));
            Raise(nameof(CanTest));
            Refresh();
            return true;
        }
        catch (ArchiveOperationException ex)
        {
            // The engine's message, verbatim. The UI does not paraphrase an error it did not diagnose:
            // "this file is raw NTFS data, not an archive" is more use than "could not open archive".
            Reset();
            Status = ex.Error.TechnicalDetail;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Verifies the open archive and keeps the verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No archive logic here: the engine reads every entry and checks the checksums, and this method
    /// only decides when to ask and where to put the answer. The password prompt, if the archive needs
    /// one, comes from <c>IPasswordProvider</c> below the seam — the same dialog extraction already
    /// uses, so testing an encrypted archive asks once and asks properly.
    /// </para>
    /// <para>
    /// A failure to <em>run</em> the test is not the same as a failed test, and both have to reach the
    /// screen. The exception is turned into an <see cref="ArchiveTestStatus.UnknownError"/> result
    /// carrying the engine's own words, so the panel has exactly one shape to render and the error
    /// cannot vanish into a status line nobody reads.
    /// </para>
    /// </remarks>
    public async ValueTask<ArchiveTestResult?> TestAsync(CancellationToken cancellationToken = default)
    {
        if (_archivePath is null || !CanTest)
        {
            return null;
        }

        IsBusy = true;
        IsTesting = true;
        Status = null;
        TestResult = null;

        try
        {
            // Through the queue, like every other unit of work: a test on a large archive takes real
            // time, and the user is entitled to see it running and to stop it.
            var id = _workspace.StartTest(_archivePath);
            _operations?.Track(id, OperationType.Test, ArchiveDisplayName ?? _archivePath);

            var result = await _workspace.WaitForTestAsync(id, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                // Cancelled, or failed in a way the queue recorded. The operation's own row says which.
                return null;
            }

            TestResult = result;
            return result;
        }
        catch (ArchiveOperationException ex)
        {
            var failure = new ArchiveTestResult
            {
                Status = ArchiveTestStatus.UnknownError,
                EntriesTested = 0,
                Findings = [new ArchiveTestFinding(
                    ex.Error.Path ?? string.Empty,
                    ex.Error.TechnicalDetail,
                    IsError: true)],
            };

            TestResult = failure;
            return failure;
        }
        finally
        {
            IsTesting = false;
            IsBusy = false;
        }
    }

    public void Reset()
    {
        _root = null;
        _currentFolder = null;
        ArchivePath = null;
        Format = null;
        Statistics = null;
        NameEncoding = null;
        Rows.Clear();
        Breadcrumbs.Clear();
        Selection.Clear();
        TestResult = null;

        Raise(nameof(ArchiveDisplayName));
        Raise(nameof(HasArchive));
        Raise(nameof(SummaryLine));
        Raise(nameof(CanExtractAll));
        Raise(nameof(CanExtractSelection));
        Raise(nameof(CanGoUp));
        Raise(nameof(CanTest));
    }

    /// <summary>Navigates into a folder. Files are ignored — opening one is a separate action.</summary>
    public void Enter(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsFolder)
        {
            return;
        }

        _currentFolder = node;
        Selection.Clear();
        Refresh();
        Raise(nameof(CanGoUp));
    }

    public void GoUp()
    {
        if (_root is null || _currentFolder is null || _currentFolder == _root)
        {
            return;
        }

        var parentPath = _currentFolder.Path.Contains('/', StringComparison.Ordinal)
            ? _currentFolder.Path[.._currentFolder.Path.LastIndexOf('/')]
            : string.Empty;

        _currentFolder = FindByPath(_root, parentPath) ?? _root;
        Selection.Clear();
        Refresh();
        Raise(nameof(CanGoUp));
    }

    public void Select(IEnumerable<ArchiveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        Selection.Clear();
        foreach (var node in nodes)
        {
            Selection.Add(node);
        }

        Raise(nameof(CanExtractSelection));
        Raise(nameof(SelectionSummary));
    }

    /// <summary>
    /// Extracts the selection, or everything when nothing is selected.
    /// </summary>
    /// <remarks>
    /// The refused entries come back in the outcome and are reported: an extraction that silently
    /// dropped them would be indistinguishable from one that had nothing to drop.
    /// </remarks>
    public async ValueTask<ExtractOutcome?> ExtractAsync(
        string destination,
        bool selectionOnly,
        ConflictPolicy conflictPolicy = ConflictPolicy.Ask,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (_archivePath is null || _root is null)
        {
            return null;
        }

        List<string>? selected = null;
        if (selectionOnly)
        {
            // The validated path, never the raw one, and refused entries contribute nothing: their
            // Path is null by construction. The writer would refuse them again anyway - this simply
            // does not ask it to.
            selected = Selection
                .SelectMany(static n => n.Files())
                .Select(static n => n.Entry?.Path?.Value)
                .Where(static p => p is not null)
                .Select(static p => p!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (selected.Count == 0)
            {
                return null;
            }
        }

        IsBusy = true;
        Status = null;

        try
        {
            // Enqueued, not awaited into existence: the id comes back at once so the operation can be
            // paused, resumed or cancelled while it runs. Tracking starts before the first event, or a
            // fast operation could finish before anything was watching it.
            var id = _workspace.StartExtract(_archivePath, destination, selected, conflictPolicy);
            _operations?.Track(id, OperationType.Extract, ArchiveDisplayName ?? _archivePath);

            var outcome = await _workspace
                .WaitForExtractAsync(id, cancellationToken)
                .ConfigureAwait(false);

            if (outcome is null)
            {
                // Cancelled, or failed in a way the queue recorded rather than threw. Neither is an
                // error to report here - the operation's own state says what happened.
                return null;
            }

            // The queue knows the operation ended; only this call knows what it produced. Handing the
            // figures over here is what lets the operations screen say "306 files, 788 MB, in 0:09"
            // instead of just "finished".
            _operations?.RecordResult(
                id,
                new OperationResult(outcome.EntriesWritten, outcome.BytesWritten.Bytes, outcome.Blocked.Count));

            Status = outcome.HadBlockedEntries
                // Invariant here too: this counts entries, and it is read beside a table of invariant
                // counts. One number system in the window, not one per sentence.
                ? $"{BidiText.FormatCount(outcome.EntriesWritten, CultureInfo.InvariantCulture)} / {BidiText.FormatCount(outcome.Blocked.Count, CultureInfo.InvariantCulture)}"
                : BidiText.FormatCount(outcome.EntriesWritten, CultureInfo.InvariantCulture);

            return outcome;
        }
        catch (ArchiveOperationException ex)
        {
            Status = ex.Error.TechnicalDetail;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Rebuilds the visible rows: the current folder, or search results across the whole archive.
    /// </summary>
    /// <remarks>
    /// Searching is a filter over the listing already in memory, which is why it costs nothing —
    /// listing an archive never extracted anything in the first place. The comparison is
    /// culture-aware because this is display, and it is applied to names only, never to a path being
    /// judged.
    /// </remarks>
    private void Refresh()
    {
        Rows.Clear();
        Breadcrumbs.Clear();

        if (_root is null || _currentFolder is null)
        {
            return;
        }

        if (_search.Length > 0)
        {
            foreach (var match in _root.Files()
                .Concat(Descendants(_root).Where(static n => n.IsFolder))
                .Where(n => Matches(n.Name, _search))
                .OrderBy(static n => n.Path, StringComparer.Ordinal))
            {
                Rows.Add(new BrowseRow(match, Depth: 0));
            }

            return;
        }

        foreach (var crumb in PathTo(_currentFolder))
        {
            Breadcrumbs.Add(crumb);
        }

        foreach (var child in _currentFolder.Children)
        {
            Rows.Add(new BrowseRow(child, Depth: 0));
        }
    }

    private bool Matches(string name, string term) =>
        _culture.CompareInfo.IndexOf(name, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static IEnumerable<ArchiveNode> Descendants(ArchiveNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }

    private IEnumerable<ArchiveNode> PathTo(ArchiveNode node)
    {
        if (_root is null)
        {
            yield break;
        }

        var stack = new Stack<ArchiveNode>();
        var current = node;
        while (current is not null && current != _root)
        {
            stack.Push(current);
            current = FindParent(_root, current);
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
    }

    private static ArchiveNode? FindParent(ArchiveNode from, ArchiveNode target)
    {
        foreach (var child in from.Children)
        {
            if (child == target)
            {
                return from;
            }

            var found = FindParent(child, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static ArchiveNode? FindByPath(ArchiveNode from, string path)
    {
        if (path.Length == 0)
        {
            return from;
        }

        foreach (var child in from.Children)
        {
            if (string.Equals(child.Path, path, StringComparison.Ordinal))
            {
                return child;
            }

            var found = FindByPath(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
