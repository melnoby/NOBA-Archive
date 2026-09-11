using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Feature 01, presentation gate: <c>TST-001…013</c>.
/// </summary>
/// <remarks>
/// The engine already verifies checksums and returns findings; nothing here re-tests that. What is
/// tested is the part that was missing — that a verdict reaches the screen intact, that the four states
/// stay distinguishable, and above all that <em>unsupported</em> never renders as <em>damaged</em>.
/// </remarks>
public sealed class ArchiveTestFeatureTests
{
    private static readonly LocalizedStrings Arabic = LocalizedStrings.For(UiLanguage.Arabic);
    private static readonly LocalizedStrings English = LocalizedStrings.For(UiLanguage.English);

    // ---------- the report ----------

    /// <summary>TST-001: a clean archive reads as good, and says so in words as well as tone.</summary>
    [Fact]
    public void A_valid_archive_reads_as_good()
    {
        var report = TestReportFormatter.For(ArchiveTestResult.Valid(2970), English);

        Assert.Equal(TestTone.Good, report.Tone);
        Assert.Equal(English.TestValid, report.Headline);
        Assert.True(report.IsUsable);
        Assert.Empty(report.Findings);
    }

    /// <summary>TST-002: warnings are extractable, and the tone says so rather than crying damage.</summary>
    [Fact]
    public void Warnings_do_not_read_as_damage()
    {
        var result = ArchiveTestResult.FromFindings(10, [Note("a/b.jpg", "Encrypted.")]);
        var report = TestReportFormatter.For(result, English);

        Assert.Equal(ArchiveTestStatus.ValidWithWarnings, result.Status);
        Assert.Equal(TestTone.Warning, report.Tone);
        Assert.True(report.IsUsable);
    }

    /// <summary>TST-003: one error is enough to make the archive damaged and unusable.</summary>
    [Fact]
    public void One_error_makes_the_archive_damaged()
    {
        var result = ArchiveTestResult.FromFindings(10, [Note("a/b.jpg", "CRC failed.", error: true)]);
        var report = TestReportFormatter.For(result, English);

        Assert.Equal(TestTone.Bad, report.Tone);
        Assert.Equal(English.TestCorrupted, report.Headline);
        Assert.False(report.IsUsable);
    }

    /// <summary>
    /// TST-004: the assertion this whole feature turns on — unsupported is not corrupted.
    /// </summary>
    /// <remarks>
    /// The engine goes to real trouble to separate these two: 7-Zip answers "unsupported method" when
    /// it cannot build a decoder, and reporting that as corruption tells someone to throw away a file
    /// that is perfectly intact. If the UI collapsed the distinction, the engine's care would be spent
    /// for nothing.
    /// </remarks>
    [Fact]
    public void Unsupported_is_not_corrupted()
    {
        var result = new ArchiveTestResult
        {
            Status = ArchiveTestStatus.Unsupported,
            EntriesTested = 4,
            Findings = [Note("a/b.bin", "Unsupported method.", error: true)],
        };

        var report = TestReportFormatter.For(result, English);

        Assert.Equal(TestTone.Info, report.Tone);
        Assert.NotEqual(English.TestCorrupted, report.Headline);
        Assert.Equal(English.TestUnsupported, report.Headline);
        Assert.Contains(report.Detail, p => p.Text == English.TestUnsupportedDetail);
    }

    /// <summary>TST-005: a missing password is a question, not a verdict on the file.</summary>
    [Fact]
    public void A_missing_password_is_not_a_verdict()
    {
        var result = new ArchiveTestResult { Status = ArchiveTestStatus.PasswordRequired };
        var report = TestReportFormatter.For(result, English);

        Assert.Equal(TestTone.Info, report.Tone);
        Assert.Equal(English.TestPasswordRequired, report.Headline);
    }

    /// <summary>
    /// TST-006: the detail line never puts a digit and an Arabic letter in one fragment.
    /// </summary>
    /// <remarks>
    /// The same invariant the archive summary earned the hard way. Reused rather than restated, because
    /// a second mechanism for the same rule is a second thing to get wrong.
    /// </remarks>
    [Fact]
    public void The_detail_line_never_mixes_a_digit_with_arabic()
    {
        var result = ArchiveTestResult.FromFindings(2970, [Note("a/b.jpg", "Encrypted.")]);

        foreach (var part in TestReportFormatter.For(result, Arabic).Detail)
        {
            var digit = part.Text.Any(char.IsDigit);
            var arabic = part.Text.Any(static c => c is >= '؀' and <= 'ۿ');
            Assert.False(digit && arabic, $"mixed fragment: '{part.Text}'");
        }
    }

    /// <summary>TST-007: counts in the detail line are Western digits in both languages.</summary>
    [Fact]
    public void Counts_are_western_digits_in_both_languages()
    {
        var result = ArchiveTestResult.Valid(2970);

        foreach (var text in new[] { Arabic, English })
        {
            var report = TestReportFormatter.For(result, text);
            Assert.Contains(report.Detail, static p => p.Text == "2,970");
        }
    }

    /// <summary>TST-008: errors come before notes, and engine order survives inside each group.</summary>
    [Fact]
    public void Errors_come_first_and_engine_order_is_kept()
    {
        var result = ArchiveTestResult.FromFindings(5,
        [
            Note("first.jpg", "Encrypted."),
            Note("broken-a.bin", "CRC failed.", error: true),
            Note("second.jpg", "Encrypted."),
            Note("broken-b.bin", "CRC failed.", error: true),
        ]);

        var rows = TestReportFormatter.For(result, English).Findings;

        Assert.Equal(
            ["broken-a.bin", "broken-b.bin", "first.jpg", "second.jpg"],
            rows.Select(static r => r.EntryPath).ToArray());
    }

    /// <summary>TST-009: the chip is localised; the engine's own sentence is not paraphrased.</summary>
    [Fact]
    public void The_chip_is_translated_and_the_engine_message_is_not()
    {
        var result = ArchiveTestResult.FromFindings(1, [Note("a/b.bin", "CRC failed.", error: true)]);
        var row = Assert.Single(TestReportFormatter.For(result, Arabic).Findings, static _ => true);

        Assert.Equal(Arabic.TestErrorChip, row.ChipLabel);
        Assert.Equal("CRC failed.", row.Message);
        Assert.Equal("a/b.bin", row.EntryPath);
    }

    // ---------- the view model ----------

    /// <summary>TST-010: the button follows the format's real capability, not a hard-coded flag.</summary>
    [Fact]
    public async Task Testing_is_offered_only_where_the_format_supports_it()
    {
        var workspace = new FakeWorkspace(new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.Zip] = ArchiveCapabilities.Open | ArchiveCapabilities.ListEntries,
        })
        {
            Listing = Listing(ArchiveFormat.Zip),
        };

        var model = new BrowseViewModel(workspace);
        Assert.False(model.CanTest, "no archive is open yet");

        await model.OpenAsync("a.zip");
        Assert.False(model.CanTest, "this format declares no Test capability");
    }

    /// <summary>TST-011: with the capability present, the verdict arrives and is kept.</summary>
    [Fact]
    public async Task A_verdict_reaches_the_view_model()
    {
        var model = await Opened(ArchiveTestResult.FromFindings(3, [Note("x.bin", "CRC failed.", error: true)]));

        Assert.True(model.CanTest);
        Assert.False(model.HasTestResult);

        var result = await model.TestAsync();

        Assert.NotNull(result);
        Assert.True(model.HasTestResult);
        Assert.Equal(ArchiveTestStatus.Corrupted, model.TestResult!.Status);
        Assert.False(model.IsTesting);
        Assert.False(model.IsBusy);
    }

    /// <summary>
    /// TST-012: a test that could not run still reaches the screen, with the engine's own words.
    /// </summary>
    /// <remarks>
    /// The failure mode this prevents is the quiet one: an exception swallowed into a status string,
    /// and a panel that never appears, leaving the button looking broken instead of the archive.
    /// </remarks>
    [Fact]
    public async Task A_failure_to_run_the_test_is_still_shown()
    {
        var workspace = new FakeWorkspace { Listing = Listing(ArchiveFormat.SevenZip) };
        var model = new BrowseViewModel(workspace);
        await model.OpenAsync("a.7z");

        workspace.TestFailure = new ArchiveOperationException(
            ArchiveError.Create(ArchiveErrorCode.IoFailure, "The file is locked.", "a.7z"));

        var result = await model.TestAsync();

        Assert.NotNull(result);
        Assert.Equal(ArchiveTestStatus.UnknownError, result!.Status);
        Assert.True(model.HasTestResult);
        Assert.Equal(TestTone.Bad, TestReportFormatter.For(result, English).Tone);
        Assert.Contains(result.Findings, static f => f.Message == "The file is locked.");
    }

    /// <summary>TST-013: a verdict belongs to one archive; opening another retires it.</summary>
    [Fact]
    public async Task Opening_another_archive_retires_the_verdict()
    {
        var model = await Opened(ArchiveTestResult.Valid(3));
        await model.TestAsync();
        Assert.True(model.HasTestResult);

        await model.OpenAsync("b.7z");

        Assert.False(model.HasTestResult);
    }

    // ---------- helpers ----------

    private static ArchiveTestFinding Note(string path, string message, bool error = false) =>
        new(path, message, error);

    private static ListOutcome Listing(ArchiveFormat format) => new(
        format,
        new ArchiveStatistics { FileCount = 2, DirectoryCount = 0 },
        [FakeWorkspace.File("one.jpg", 4096), FakeWorkspace.File("two.jpg", 2048)]);

    /// <summary>
    /// ATF-020: testing an archive is an operation, with a row of its own.
    /// </summary>
    /// <remarks>
    /// It used to call the handler directly, outside the queue: no progress, no cancel, no history.
    /// Verifying 3,800 entries is several seconds of work, and for those seconds the window could
    /// neither say what it was doing nor be stopped — while bypassing the one component that is meant
    /// to own every unit of work.
    /// </remarks>
    [Fact]
    public async Task Testing_an_archive_goes_through_the_operation_queue()
    {
        var workspace = new FakeWorkspace { Listing = Listing(ArchiveFormat.SevenZip) };
        var shell = new ShellViewModel(workspace);
        await shell.Browse.OpenAsync("a.7z", TestContext.Token);

        await shell.Browse.TestAsync(TestContext.Token);

        Assert.Single(workspace.TestsStarted);

        Assert.Single(shell.Operations.All);
        var operation = shell.Operations.All[0];
        Assert.Equal(OperationType.Test, operation.Type);
        Assert.Equal(OperationState.Completed, operation.State);
    }

    /// <summary>
    /// ATF-021: a test row says it is testing, and offers no Pause.
    /// </summary>
    /// <remarks>
    /// Every row used to read "Extracting" because extraction was the only thing the queue had been
    /// given, and <c>CanPause</c> was decided by state alone — so a test would have offered a button
    /// the engine refuses. ADR-0007: a pause we cannot honour is reported as unavailable.
    /// </remarks>
    [Fact]
    public async Task A_test_row_reads_testing_and_cannot_be_paused()
    {
        var workspace = new FakeWorkspace { Listing = Listing(ArchiveFormat.SevenZip) };
        var shell = new ShellViewModel(workspace);
        await shell.Browse.OpenAsync("a.7z", TestContext.Token);

        var id = workspace.StartTest("a.7z");
        var operation = shell.Operations.Track(id, OperationType.Test, "a.7z");
        operation.Apply(workspace.Operations().Single(s => s.Id == id));

        Assert.False(operation.SupportsPause);
        Assert.False(operation.CanPause);
        Assert.True(operation.CanCancel, "a test must still be stoppable");

        Assert.Equal("Testing", OperationFormatter.VerbOf(operation, LocalizedStrings.For(UiLanguage.English)));
        Assert.Equal("اختبار", OperationFormatter.VerbOf(operation, LocalizedStrings.For(UiLanguage.Arabic)));
    }

    /// <summary>ATF-022: a cancelled test reports no verdict, rather than a misleading clean one.</summary>
    [Fact]
    public async Task A_cancelled_test_reports_no_verdict()
    {
        var workspace = new FakeWorkspace { Listing = Listing(ArchiveFormat.SevenZip) };
        var model = new BrowseViewModel(workspace);
        await model.OpenAsync("a.7z", TestContext.Token);

        workspace.CancelNextOperation = true;
        var result = await model.TestAsync(TestContext.Token);

        Assert.Null(result);
        Assert.Null(model.TestResult);
    }

    private static async Task<BrowseViewModel> Opened(ArchiveTestResult? verdict)
    {
        var workspace = new FakeWorkspace
        {
            Listing = Listing(ArchiveFormat.SevenZip),
            TestResult = verdict,
        };

        var model = new BrowseViewModel(workspace);
        await model.OpenAsync("a.7z");
        return model;
    }
}
