using System.Globalization;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The file list's cells: <c>COL-001…008</c>.
/// </summary>
/// <remarks>
/// The table is the screen. Six columns of notation that a person reads across and compares down, in a
/// window that mirrors — which makes almost every failure here a string failure, and therefore one that
/// can be caught on a machine with no WinUI at all.
/// </remarks>
public sealed class RowColumnTests
{
    private static readonly LocalizedStrings Arabic = LocalizedStrings.For(UiLanguage.Arabic);
    private static readonly LocalizedStrings English = LocalizedStrings.For(UiLanguage.English);

    /// <summary>COL-001: a file reports every column the archive knows.</summary>
    [Fact]
    public void A_file_fills_every_column()
    {
        var columns = Format(Entry("photos/scan551.jpg", size: 8_118_831, packed: 7_652_145), English);

        Assert.Equal(BidiText.Isolate("scan551.jpg"), columns.Name);
        Assert.Equal(BidiText.IsolateAsLtr("JPG"), columns.TypeLabel);
        Assert.Equal(BidiText.IsolateAsLtr("8,118,831"), columns.Size);
        Assert.Equal(BidiText.IsolateAsLtr("7,652,145"), columns.PackedSize);
        Assert.Equal(BidiText.IsolateAsLtr("A"), columns.Attributes);
        Assert.False(columns.IsFolder);
    }

    /// <summary>
    /// COL-002: a folder is a folder in both languages, and reports no size of its own.
    /// </summary>
    /// <remarks>
    /// An em dash rather than <c>0</c>: a folder does not weigh nothing, it has no size, and a column of
    /// zeroes invites the reader to add them up.
    /// </remarks>
    [Fact]
    public void A_folder_states_that_it_has_no_size()
    {
        var columns = Format(Directory("photos"), Arabic);

        Assert.Equal("مجلد", columns.TypeLabel);
        Assert.Equal(RowColumnFormatter.NotApplicable, columns.Size);
        Assert.Equal(RowColumnFormatter.NotApplicable, columns.PackedSize);
        Assert.Equal(BidiText.IsolateAsLtr("DIR"), columns.Attributes);
        Assert.True(columns.IsFolder);
    }

    /// <summary>
    /// COL-003: digits are pinned left to right, in every column, in Arabic.
    /// </summary>
    /// <remarks>
    /// The one that matters most. A size, a packed size and a timestamp are read <em>across</em> a row
    /// and compared <em>down</em> a column; if the paragraph direction is allowed to reorder any of
    /// them, the table stops being a table and becomes a set of plausible-looking numbers.
    /// </remarks>
    [Fact]
    public void Every_numeric_column_is_pinned_left_to_right_in_arabic()
    {
        var columns = Format(
            Entry("مستندات/تقرير.pdf", size: 1_234, packed: 999, modified: new DateTime(2011, 11, 11, 10, 28, 0)),
            Arabic);

        foreach (var cell in new[] { columns.Size, columns.PackedSize, columns.Modified, columns.Attributes })
        {
            Assert.Equal(BidiText.LeftToRightIsolate, cell[0]);
            Assert.Equal(BidiText.PopDirectionalIsolate, cell[^1]);
        }
    }

    /// <summary>
    /// COL-004: an Arabic name keeps its own direction, and its extension keeps the name's order.
    /// </summary>
    /// <remarks>
    /// The name is isolated, not pinned: an Arabic file name should read right to left, which is not
    /// true of the columns beside it. What isolation buys is that neither can reorder the other.
    /// </remarks>
    [Fact]
    public void An_arabic_name_is_isolated_rather_than_pinned()
    {
        var columns = Format(Entry("شنايش من محرم بك الي كفر عشري.rar"), Arabic);

        Assert.Equal(BidiText.Isolate("شنايش من محرم بك الي كفر عشري.rar"), columns.Name);
        Assert.Equal(BidiText.IsolateAsLtr("RAR"), columns.TypeLabel);
    }

    /// <summary>
    /// COL-005: the extension is uppercased invariantly.
    /// </summary>
    /// <remarks>
    /// Turkish lowercases <c>I</c> to a dotless <c>ı</c>. Under a Turkish UI culture a <c>.gif</c> would
    /// otherwise report a type that no other row in the table shares, and sorting by type would split
    /// identical files apart.
    /// </remarks>
    [Fact]
    public void The_type_is_the_same_identifier_under_every_culture()
    {
        var turkish = CultureInfo.GetCultureInfo("tr-TR");
        var columns = Format(Entry("a/animation.gif"), English, turkish);

        Assert.Equal(BidiText.IsolateAsLtr("GIF"), columns.TypeLabel);
    }

    /// <summary>
    /// COL-009: a timestamp carries no letter that could reshape its digits.
    /// </summary>
    /// <remarks>
    /// The Arabic UI rendered this column in Arabic-Indic digits while the size column beside it stayed
    /// Western, because the locale's own time pattern puts "ص"/"م" in the run and the shaper takes its
    /// cue from the nearest strong character. The column is now digits and separators only, in both
    /// languages, so a reader comparing rows is comparing one number system.
    /// </remarks>
    [Fact]
    public void A_timestamp_is_digits_and_separators_in_both_languages()
    {
        var moment = new DateTime(2011, 11, 11, 10, 27, 0);

        foreach (var text in new[] { Arabic, English })
        {
            var cell = Format(Entry("a/b.txt", modified: moment), text, CultureInfo.GetCultureInfo("ar-EG")).Modified;
            var bare = cell.Trim(BidiText.LeftToRightIsolate, BidiText.PopDirectionalIsolate);

            // The shape, not a wall-clock value. The entry carries a UTC instant and the column renders
            // it in local time, so asserting "10:27" only held while the runner happened to be UTC -
            // and scripts/test.sh deliberately pins Africa/Cairo, which is the whole point of pinning a
            // zone with an offset. What this test is actually about is that no letter reaches the run:
            // ar-EG's own time pattern would put "ص"/"م" in it, and the shaper takes its cue from the
            // nearest strong character.
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(bare, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$"),
                $"not a bare timestamp: '{bare}'");
            Assert.DoesNotContain(bare, char.IsLetter);
        }
    }

    /// <summary>COL-006: a file with no extension is still typed, not left blank.</summary>
    [Fact]
    public void A_file_without_an_extension_is_still_typed()
    {
        Assert.Equal("File", RowColumnFormatter.TypeOf("LICENSE", isFolder: false, English));
        Assert.Equal("ملف", RowColumnFormatter.TypeOf("LICENSE", isFolder: false, Arabic));
    }

    /// <summary>
    /// COL-007: a refused entry carries a label and the engine's own explanation.
    /// </summary>
    /// <remarks>
    /// Both, because the row must be identifiable without colour: the label is what a person reading in
    /// greyscale sees, and the detail is what tells them why.
    /// </remarks>
    [Fact]
    public void A_refused_entry_carries_a_label_and_a_reason()
    {
        var row = new BrowseRow(NodeFor(FakeWorkspace.Rejected("../../evil.txt")), Depth: 0);
        var columns = RowColumnFormatter.For(row, Arabic, CultureInfo.InvariantCulture);

        Assert.True(columns.IsRejected);
        Assert.Equal(Arabic.BlockedLabel, columns.StateLabel);
        Assert.False(string.IsNullOrWhiteSpace(columns.StateDetail));
    }

    /// <summary>COL-008: the parent row is navigation, and says so rather than faking a folder.</summary>
    [Fact]
    public void The_parent_row_is_marked_as_navigation()
    {
        var parent = RowColumnFormatter.ParentLink(English);

        Assert.True(parent.IsParentLink);
        Assert.True(parent.IsFolder);
        Assert.Equal("..", parent.Name);
        Assert.Equal(RowColumnFormatter.NotApplicable, parent.Modified);
    }

    private static RowColumns Format(ArchiveEntry entry, LocalizedStrings text, CultureInfo? culture = null) =>
        RowColumnFormatter.For(
            new BrowseRow(NodeFor(entry), Depth: 0),
            text,
            culture ?? CultureInfo.GetCultureInfo("en-US"));

    /// <summary>The leaf the entry actually describes, not the folder that happens to hold it.</summary>
    private static ArchiveNode NodeFor(ArchiveEntry entry)
    {
        var node = ArchiveNode.BuildTree([entry]).Children[0];
        while (node.Children.Count == 1 && node.Entry is null)
        {
            node = node.Children[0];
        }

        return node;
    }

    private static ArchiveEntry Entry(
        string path,
        long size = 10,
        long packed = 5,
        DateTime? modified = null) =>
        new()
        {
            RawPath = path,
            Path = ArchivePath.TryCreate(path, out var validated, out _) ? validated : null,
            Kind = EntryKind.File,
            UncompressedSize = ByteSize.FromBytes(size),
            CompressedSize = ByteSize.FromBytes(packed),
            Modified = modified is { } m ? new DateTimeOffset(m, TimeSpan.Zero) : null,
            Attributes = FileAttributes.Archive,
        };

    private static ArchiveEntry Directory(string path) => FakeWorkspace.Directory(path);
}
