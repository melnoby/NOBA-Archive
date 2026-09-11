using System.Globalization;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The summary as fragments rather than a sentence: <c>SUM-001…009</c>.
/// </summary>
/// <remarks>
/// <para>
/// These exist because three fixes for the same defect were shipped and all three failed. Under an
/// Arabic UI the header read <c>٩ ملفات</c> while the column of file sizes directly beneath it read
/// <c>9</c>. A <c>LEFT-TO-RIGHT MARK</c> before the number did nothing, <see cref="CultureInfo.InvariantCulture"/>
/// did nothing, and <c>FlowDirection="LeftToRight"</c> on the text block moved the line but not the
/// digits. Each was verified on Windows and each was wrong.
/// </para>
/// <para>
/// What none of them touched is the only thing that mattered: the shaper substitutes national digits
/// per <em>run</em>, based on what is in that run. A run holding <c>9 ملفات</c> has Arabic in it. A run
/// holding <c>9</c> does not. So the assertions below are not about direction properties — they are
/// about the one property that decides the outcome: no fragment carries both a digit and an Arabic
/// letter.
/// </para>
/// </remarks>
public sealed class SummaryPartTests
{
    private static readonly LocalizedStrings Arabic = LocalizedStrings.For(UiLanguage.Arabic);
    private static readonly LocalizedStrings English = LocalizedStrings.For(UiLanguage.English);

    /// <summary>
    /// Two entries whose sizes add up to the declared total, as a real listing's do.
    /// </summary>
    /// <remarks>
    /// The fixture is deliberately consistent because the two summaries read the total from different
    /// places: the header takes the archive's declared <see cref="ArchiveStatistics.TotalUncompressedSize"/>,
    /// while the old sentence summed the tree. Those agree in a real archive — <c>BuildTree</c> keeps
    /// rejected entries rather than dropping them, so both count the same bytes — and a fixture that
    /// made them disagree would be testing the fake, not the window.
    /// </remarks>
    private static readonly ArchiveEntry[] Entries =
    [
        FakeWorkspace.File("photos/one.jpg", 8_388_608),
        FakeWorkspace.File("photos/two.jpg", 4_194_304),
    ];

    private static readonly ArchiveStatistics Stats = new()
    {
        FileCount = 9,
        DirectoryCount = 1,
        TotalUncompressedSize = ByteSize.FromBytes(12_582_912),
        TotalCompressedSize = ByteSize.FromBytes(11_953_766),
        IsSolid = true,
        VolumeCount = 3,
        RejectedEntryCount = 2,
    };

    /// <summary>
    /// SUM-001: the Arabic summary comes back as separate fragments, not one string.
    /// </summary>
    /// <remarks>
    /// The requirement stated the other way round: a number and an Arabic word must never be handed to
    /// the renderer together. This is the structural half of that — that there is more than one
    /// fragment to hand over at all.
    /// </remarks>
    [Fact]
    public void Arabic_splits_text_from_numbers()
    {
        var parts = SummaryFormatter.ForArchive(Stats, Arabic);

        Assert.True(parts.Count > 1, $"expected fragments, got {parts.Count}");
        Assert.Contains(parts, static p => p.Kind == SummaryPartKind.Value);
        Assert.Contains(parts, static p => p.Kind == SummaryPartKind.Label);
    }

    /// <summary>
    /// SUM-002: the assertion the whole change exists for — no fragment mixes a digit with Arabic.
    /// </summary>
    /// <remarks>
    /// This is the invariant, not <see cref="SummaryPart.Direction"/>. A fragment could be marked
    /// left-to-right and still render Arabic-Indic digits if its own text contained an Arabic letter,
    /// which is exactly the failure the previous three fixes left in place.
    /// </remarks>
    [Fact]
    public void No_fragment_carries_a_digit_and_an_arabic_letter()
    {
        foreach (var part in SummaryFormatter.ForArchive(Stats, Arabic))
        {
            var hasDigit = part.Text.Any(char.IsDigit);
            var hasArabic = part.Text.Any(IsArabicLetter);
            Assert.False(hasDigit && hasArabic, $"mixed fragment: '{part.Text}'");
        }
    }

    /// <summary>SUM-003: every fragment that holds a number declares itself left to right.</summary>
    [Fact]
    public void Numbers_are_always_left_to_right()
    {
        foreach (var part in SummaryFormatter.ForArchive(Stats, Arabic))
        {
            if (part.Text.Any(char.IsDigit))
            {
                Assert.Equal(SummaryDirection.LeftToRight, part.Direction);
                Assert.Equal(SummaryPartKind.Value, part.Kind);
            }
        }
    }

    /// <summary>
    /// SUM-004: the size and the ratio are notation, so they travel with the digits, not the words.
    /// </summary>
    /// <remarks>
    /// <c>MB</c> and <c>%</c> were called out by name because in the shipped window the percentage was
    /// the one figure that came out Western — and only by accident, because the unit letters ahead of
    /// it happened to be Latin. Accident is not a design; this pins it.
    /// </remarks>
    [Fact]
    public void The_size_and_the_ratio_stay_western()
    {
        var parts = SummaryFormatter.ForArchive(Stats, Arabic);

        var size = Assert.Single(parts, static p => p.Text.Contains("MB", StringComparison.Ordinal));
        Assert.Equal(SummaryDirection.LeftToRight, size.Direction);
        Assert.DoesNotContain(size.Text, IsArabicLetter);

        var ratio = Assert.Single(parts, static p => p.Text.Contains('%'));
        Assert.Equal(SummaryDirection.LeftToRight, ratio.Direction);
        Assert.DoesNotContain(ratio.Text, IsArabicLetter);
    }

    /// <summary>SUM-005: under English every fragment runs left to right, so nothing changed there.</summary>
    [Fact]
    public void English_is_unchanged()
    {
        var parts = SummaryFormatter.ForArchive(Stats, English);

        Assert.NotEmpty(parts);
        Assert.All(parts, static p => Assert.Equal(SummaryDirection.LeftToRight, p.Direction));
    }

    /// <summary>
    /// SUM-006: the fragments say exactly what the old sentence said.
    /// </summary>
    /// <remarks>
    /// The one test that would catch this drop quietly changing the window's content rather than only
    /// its typesetting. It joins the fragments back into a line and compares it to
    /// <see cref="BrowseViewModel.SummaryLine"/> with the bidi control characters stripped — those are
    /// the only difference the change is permitted to make, because a fragment that holds nothing but
    /// digits has nothing to be isolated from.
    /// </remarks>
    [Theory]
    [InlineData("ar")]
    [InlineData("en")]
    public async Task The_fragments_read_as_the_old_line_did(string tag)
    {
        var model = await Opened(tag);
        var text = LocalizedStrings.For(UiLanguage.ForCulture(model.Culture));

        var joined = Join(SummaryFormatter.ForArchive(model.Statistics, text));

        Assert.Equal(Strip(model.SummaryLine), joined);
    }

    /// <summary>SUM-007: an archive with no statistics yields no fragments, not a blank one.</summary>
    [Fact]
    public void No_statistics_yields_no_fragments()
    {
        Assert.Empty(SummaryFormatter.ForArchive(null, Arabic));
        Assert.Empty(SummaryFormatter.ForSelection([], Arabic));
    }

    /// <summary>SUM-008: the selection summary splits the same way, and still counts the same.</summary>
    [Fact]
    public async Task The_selection_fragments_read_as_the_old_line_did()
    {
        var model = await Opened("ar");
        model.Select(model.Rows.Select(static r => r.Node));

        var parts = SummaryFormatter.ForSelection(model.Selection, Arabic);

        Assert.Equal(Strip(model.SelectionSummary), Join(parts));
        foreach (var part in parts)
        {
            Assert.False(part.Text.Any(char.IsDigit) && part.Text.Any(IsArabicLetter), part.Text);
        }
    }

    /// <summary>
    /// SUM-009: the fragments carry no bidi control characters of their own.
    /// </summary>
    /// <remarks>
    /// Isolates were the previous attempt at this problem. Leaving them in would be two mechanisms
    /// arguing over the same text, and would put invisible characters into every expected value above.
    /// </remarks>
    [Fact]
    public void The_fragments_carry_no_control_characters()
    {
        foreach (var part in SummaryFormatter.ForArchive(Stats, Arabic))
        {
            Assert.DoesNotContain(part.Text, IsBidiControl);
        }
    }

    private static bool IsArabicLetter(char c) => c is >= '؀' and <= 'ۿ';

    private static bool IsBidiControl(char c) =>
        c is BidiText.FirstStrongIsolate
            or BidiText.PopDirectionalIsolate
            or BidiText.LeftToRightIsolate
            or '‎'
            or '‏';

    /// <summary>Renders the fragments the way the page does: text in order, a space between them.</summary>
    private static string Join(IReadOnlyList<SummaryPart> parts) =>
        string.Join(' ', parts.Select(static p => p.Text));

    private static string Strip(string line) =>
        new(line.Where(static c => !IsBidiControl(c)).ToArray());

    private static async Task<BrowseViewModel> Opened(string tag)
    {
        var model = new BrowseViewModel(new FakeWorkspace
        {
            Listing = new(ArchiveFormat.SevenZip, Stats, Entries),
        })
        {
            Culture = CultureInfo.GetCultureInfo(tag),
        };

        await model.OpenAsync("a.7z");
        return model;
    }
}
