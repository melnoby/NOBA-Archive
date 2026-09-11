using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The capability-driven UI contract: <c>UI-080…089</c>.
/// </summary>
/// <remarks>
/// These are the negative controls DROP 05 turns on. Each one takes a capability away from the engine
/// and asserts that the product stops offering the thing — because a UI that is capability-driven only
/// while every capability happens to be present is not capability-driven, it is lucky.
/// </remarks>
public sealed class FormatCatalogTests
{
    private static readonly LocalizedStrings English = LocalizedStrings.For(UiLanguage.English);
    private static readonly LocalizedStrings Arabic = LocalizedStrings.For(UiLanguage.Arabic);

    /// <summary>A workspace whose formats and capabilities are exactly what the test says.</summary>
    private static FakeWorkspace With(params (ArchiveFormat Format, ArchiveCapabilities Capabilities)[] formats) =>
        new(formats.ToDictionary(static f => f.Format, static f => f.Capabilities));

    /// <summary>
    /// UI-080: taking Create away from a provider takes it off the screen.
    /// </summary>
    /// <remarks>
    /// Negative control 1. The same workspace, the same view model, one capability removed — and the
    /// format has to leave the creatable list without anything in the UI being edited.
    /// </remarks>
    [Fact]
    public void Removing_create_from_a_provider_removes_it_from_the_create_screen()
    {
        var canCreate = With(
            (ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries),
            (ArchiveFormat.Tar, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries));

        Assert.Contains(new CreateOptionsViewModel(canCreate).CreatableFormats, f => f == ArchiveFormat.Tar);

        // The only change: TAR no longer claims Create.
        var readOnlyTar = With(
            (ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries),
            (ArchiveFormat.Tar, ArchiveCapabilities.ReadOnlySet));

        var options = new CreateOptionsViewModel(readOnlyTar);
        Assert.DoesNotContain(options.CreatableFormats, f => f == ArchiveFormat.Tar);
        Assert.Contains(options.CreatableFormats, f => f == ArchiveFormat.Zip);
    }

    /// <summary>
    /// UI-081: a format no provider serves is not in the vocabulary at all.
    /// </summary>
    /// <remarks>
    /// Negative control 3. Unregistering a provider must remove its formats from every list the UI
    /// builds — the open filter included, which is the one that decides which files a person is even
    /// allowed to choose.
    /// </remarks>
    [Fact]
    public void Removing_a_provider_removes_its_format_from_every_list()
    {
        var withTar = With(
            (ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create),
            (ArchiveFormat.Tar, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create));

        Assert.Contains(".tar", string.Join(" ", new BrowseViewModel(withTar).OpenableExtensions));
        Assert.Contains(new FormatCatalogViewModel(withTar).Usable, d => d.Format == ArchiveFormat.Tar);

        var withoutTar = With(
            (ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create));

        Assert.DoesNotContain(".tar", string.Join(" ", new BrowseViewModel(withoutTar).OpenableExtensions));
        Assert.Empty(new FormatCatalogViewModel(withoutTar).All.Where(static d => d.Format == ArchiveFormat.Tar));
    }

    /// <summary>
    /// UI-082: a format whose backend could not load is unavailable, and explained rather than hidden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Negative control 5. This is the shape the engine really produces: <c>UnavailableNativeBackend</c>
    /// registers 7Z and RAR with <see cref="ArchiveCapabilities.None"/> rather than vanishing, precisely
    /// so the reason survives. Nothing crashes, nothing is offered, and the person gets a sentence.
    /// </para>
    /// <para>
    /// The distinction the sentence has to make is between "this installation cannot" and "this format
    /// cannot". Telling somebody that 7Z archives cannot be created, on a machine that is merely missing
    /// a library, sends them looking for the wrong problem entirely.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_format_whose_backend_is_missing_is_unavailable_and_explained()
    {
        var workspace = With(
            (ArchiveFormat.Zip, ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create),
            (ArchiveFormat.SevenZip, ArchiveCapabilities.None));

        var catalog = new FormatCatalogViewModel(workspace);

        var sevenZip = Assert.Single(catalog.Unavailable, static d => d.Format == ArchiveFormat.SevenZip);
        Assert.False(sevenZip.IsAvailable);
        Assert.DoesNotContain(catalog.Usable, d => d.Format == ArchiveFormat.SevenZip);

        // Present in the vocabulary, so it can be talked about.
        Assert.Contains(catalog.All, d => d.Format == ArchiveFormat.SevenZip);

        Assert.Equal(
            FormatBlocker.ProviderUnavailable,
            FormatCatalogViewModel.Reason(sevenZip.Report, FormatOperation.Open));

        var explanation = FormatCatalogViewModel.Explain(sevenZip.Report, FormatOperation.Create, English);
        Assert.NotNull(explanation);
        Assert.Contains("7Z", explanation!);
        Assert.Contains("missing", explanation);

        // And it must not be mistaken for a format that simply cannot create.
        Assert.NotEqual(
            FormatCatalogViewModel.Explain(
                new FormatCapabilityReport(ArchiveFormat.Rar, ArchiveCapabilities.ReadOnlySet),
                FormatOperation.Create,
                English),
            explanation);
    }

    /// <summary>UI-083: a read-only format says it cannot be created, in either language.</summary>
    [Fact]
    public void A_read_only_format_explains_that_it_cannot_be_created()
    {
        var report = new FormatCapabilityReport(ArchiveFormat.Rar, ArchiveCapabilities.ReadOnlySet);

        Assert.Equal(FormatBlocker.None, FormatCatalogViewModel.Reason(report, FormatOperation.Extract));
        Assert.Equal(
            FormatBlocker.NotSupportedByFormat,
            FormatCatalogViewModel.Reason(report, FormatOperation.Create));

        Assert.Null(FormatCatalogViewModel.Explain(report, FormatOperation.Extract, English));

        foreach (var text in new[] { English, Arabic })
        {
            var explanation = FormatCatalogViewModel.Explain(report, FormatOperation.Create, text);

            Assert.NotNull(explanation);
            Assert.Contains("RAR", explanation!);

            // A missing key renders as the key itself rather than as blank; that must not reach a user.
            Assert.DoesNotContain("format.", explanation);
            Assert.DoesNotContain("{0}", explanation);
        }
    }

    /// <summary>
    /// UI-084: the report's permission and the screen's offer are the same answer.
    /// </summary>
    /// <remarks>
    /// Exhaustive over the operations rather than spot-checked, because the failure mode is one row of
    /// a switch reading the wrong flag — which no single example would catch.
    /// </remarks>
    [Theory]
    [InlineData(FormatOperation.Open, ArchiveCapabilities.Open)]
    [InlineData(FormatOperation.Browse, ArchiveCapabilities.ListEntries)]
    [InlineData(FormatOperation.Test, ArchiveCapabilities.Test)]
    [InlineData(FormatOperation.Extract, ArchiveCapabilities.Extract)]
    [InlineData(FormatOperation.Create, ArchiveCapabilities.Create)]
    public void Each_operation_reads_its_own_capability(FormatOperation operation, ArchiveCapabilities capability)
    {
        var granted = new FormatCapabilityReport(ArchiveFormat.Zip, capability);
        Assert.True(FormatCatalogViewModel.Permits(granted, operation));

        // Everything except the one flag. The operation must still be refused.
        var everythingElse = new FormatCapabilityReport(
            ArchiveFormat.Zip,
            ~capability & (ArchiveCapabilities)0x7FFFF);

        Assert.False(FormatCatalogViewModel.Permits(everythingElse, operation));
    }

    /// <summary>
    /// UI-085: a clean test on a format with no content checksum says what it actually verified.
    /// </summary>
    /// <remarks>
    /// The panel is the last place this can be said, and the only place a person reads it. Nothing in
    /// the formatter knows which format it is looking at — it reads
    /// <see cref="FormatCapabilityReport.CanVerifyContents"/> and nothing else.
    /// </remarks>
    [Fact]
    public void A_clean_verdict_says_what_it_covered()
    {
        var clean = ArchiveTestResult.Valid(12);

        var checksummed = new FormatCapabilityReport(
            ArchiveFormat.Zip,
            ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.ContentIntegrityCheck);

        var structureOnly = new FormatCapabilityReport(ArchiveFormat.Tar, ArchiveCapabilities.ReadOnlySet);

        var withChecksum = TestReportFormatter.For(clean, English, checksummed);
        var withoutChecksum = TestReportFormatter.For(clean, English, structureOnly);

        // The caveat itself, matched exactly: "no checksum errors" is a different sentence that the
        // clean verdict has always carried, and matching on the word alone confused the two.
        Assert.DoesNotContain(
            withChecksum.Detail,
            p => string.Equals(p.Text, English.FormatNoContentChecksum, StringComparison.Ordinal));

        Assert.Contains(
            withoutChecksum.Detail,
            p => string.Equals(p.Text, English.FormatNoContentChecksum, StringComparison.Ordinal));

        // And the format with no checksums must not also claim there were no checksum errors.
        Assert.Contains(
            withChecksum.Detail,
            p => string.Equals(p.Text, English.TestCleanDetail, StringComparison.Ordinal));

        Assert.DoesNotContain(
            withoutChecksum.Detail,
            p => string.Equals(p.Text, English.TestCleanDetail, StringComparison.Ordinal));

        // Both are still "intact"; the caveat qualifies the verdict, it does not downgrade it.
        Assert.Equal(withChecksum.Headline, withoutChecksum.Headline);
        Assert.Equal(withChecksum.Tone, withoutChecksum.Tone);

        // And with no format supplied, the report is exactly what it always was.
        Assert.Equal(
            TestReportFormatter.For(clean, English).Detail.Count,
            withChecksum.Detail.Count);
    }

    /// <summary>UI-086: the caveat is not attached to a verdict that already says the archive is broken.</summary>
    [Fact]
    public void A_corrupt_verdict_is_not_footnoted()
    {
        var corrupt = ArchiveTestResult.FromFindings(
            3,
            [new ArchiveTestFinding("a.txt", "CRC mismatch", IsError: true)]);

        var report = TestReportFormatter.For(
            corrupt,
            English,
            new FormatCapabilityReport(ArchiveFormat.Tar, ArchiveCapabilities.ReadOnlySet));

        Assert.DoesNotContain(
            report.Detail,
            p => string.Equals(p.Text, English.FormatNoContentChecksum, StringComparison.Ordinal));
    }

    /// <summary>UI-087: the catalogue describes a format without translating its name.</summary>
    /// <remarks>
    /// 7Z, ZIP, RAR, TAR and GZIP are names, not words. Everything around them is localised; they are
    /// not, in either direction.
    /// </remarks>
    [Fact]
    public void Format_names_are_not_translated()
    {
        var workspace = With((ArchiveFormat.Tar, ArchiveCapabilities.ReadOnlySet));
        var description = new FormatCatalogViewModel(workspace).Describe(ArchiveFormat.Tar);

        Assert.Equal("TAR", description.DisplayName);
        Assert.Contains(".tar", description.Extensions);
    }
}
