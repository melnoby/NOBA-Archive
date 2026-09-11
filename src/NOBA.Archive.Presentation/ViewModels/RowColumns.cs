using System.Globalization;
using System.IO;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>
/// One row of the file list, as six finished strings.
/// </summary>
/// <remarks>
/// Every value is display-ready: localised, culture-formatted, and already carrying whatever
/// directional isolation it needs. A cell is drawn exactly as it arrives.
/// </remarks>
public sealed record RowColumns(
    string Name,
    string TypeLabel,
    string Size,
    string PackedSize,
    string Modified,
    string Attributes,
    bool IsFolder,
    bool IsParentLink,
    bool IsRejected,
    bool IsEncrypted,
    string? StateLabel,
    string? StateDetail);

/// <summary>
/// Turns a <see cref="BrowseRow"/> into the strings a table cell shows.
/// </summary>
/// <remarks>
/// <para>
/// This lives here rather than in the page for the reason the whole presentation layer exists: it is
/// the part that can be wrong, and it is the part that can be tested on a machine that cannot build
/// WinUI. A size formatted in the wrong culture, a date that mirrors, an extension that changes order
/// inside an Arabic name — every one of those is a bug in a string, not in a control.
/// </para>
/// <para>
/// <b>Direction is decided per column, not per row.</b> The name is isolated and left to find its own
/// direction, because a file can legitimately be named in either script. Sizes, dates and attributes
/// are pinned left-to-right, because they are not language — they are notation, and notation whose
/// digits the bidirectional algorithm has reordered is worse than useless in a table someone is
/// comparing rows in.
/// </para>
/// </remarks>
public static class RowColumnFormatter
{
    /// <summary>
    /// What a cell shows when the archive does not say.
    /// </summary>
    /// <remarks>
    /// Notation, not language, so it is not in the string table — an em dash reads the same in both
    /// scripts, and <c>L10N-001</c> is right to treat an identical pair of "translations" as a mistake.
    /// It is also not <c>0</c>: a folder does not weigh nothing, it has no size, and a column of zeroes
    /// invites the reader to total them.
    /// </remarks>
    public const string NotApplicable = "\u2014";

    /// <summary>The row that walks up a folder. Rendered like a row, but it is navigation.</summary>
    public static RowColumns ParentLink(LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new RowColumns(
            Name: "..",
            TypeLabel: text.TypeFolder,
            Size: NotApplicable,
            PackedSize: NotApplicable,
            Modified: NotApplicable,
            Attributes: NotApplicable,
            IsFolder: true,
            IsParentLink: true,
            IsRejected: false,
            IsEncrypted: false,
            StateLabel: null,
            StateDetail: null);
    }

    public static RowColumns For(BrowseRow row, LocalizedStrings text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(culture);

        var node = row.Node;
        var entry = node.Entry;

        return new RowColumns(
            Name: node.DisplayName,
            TypeLabel: TypeOf(node.Name, row.IsFolder, text),
            Size: row.IsFolder ? NotApplicable : Bytes(entry?.UncompressedSize.Bytes, culture),
            PackedSize: row.IsFolder ? NotApplicable : Bytes(entry?.CompressedSize.Bytes, culture),
            Modified: Timestamp(entry?.Modified, text),
            Attributes: AttributesOf(row.IsFolder, entry?.Attributes, text),
            IsFolder: row.IsFolder,
            IsParentLink: false,
            IsRejected: row.IsRejected,
            IsEncrypted: row.IsEncrypted,
            StateLabel: row.IsRejected ? text.BlockedLabel : row.IsEncrypted ? text.EncryptedLabel : null,
            StateDetail: row.RejectionDetail);
    }

    /// <summary>
    /// The type column: a folder, or the extension in capitals.
    /// </summary>
    /// <remarks>
    /// The extension is uppercased with the invariant culture on purpose. Turkish lowercases <c>I</c> to
    /// a dotless <c>ı</c>, so a <c>.gif</c> under a Turkish UI would report a type no other row shares.
    /// A file type is an identifier, not prose.
    /// </remarks>
    public static string TypeOf(string name, bool isFolder, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);

        if (isFolder)
        {
            return text.TypeFolder;
        }

        var extension = Path.GetExtension(name);
        return extension.Length > 1
            ? BidiText.IsolateAsLtr(extension[1..].ToUpperInvariant())
            : text.TypeFile;
    }

    /// <summary>
    /// A byte count, grouped for the UI culture and pinned left to right.
    /// </summary>
    /// <remarks>
    /// Grouped digits rather than <c>1.4 MB</c>: this column is read to compare rows, and a column of
    /// mixed units cannot be compared at a glance. The archive's own total is where a human-readable
    /// size belongs.
    /// </remarks>
    private static string Bytes(long? value, CultureInfo culture) =>
        value is { } bytes ? BidiText.FormatCount(bytes, culture) : string.Empty;

    private static string Timestamp(DateTimeOffset? value, LocalizedStrings text)
    {
        if (value is not { } moment)
        {
            return NotApplicable;
        }

        // A fixed pattern under the invariant culture, deliberately not the UI culture's own.
        //
        // Two reasons, and neither is about taste. The Arabic short pattern carries "ص"/"م" inside the
        // run, and DirectWrite's contextual digit substitution keys off the nearest strong character -
        // so those two letters turned the whole column into Arabic-Indic digits while the size column
        // beside it stayed Western. One table, two number systems, in the same row. An LTR isolate does
        // not help: it sets direction, not shaping.
        //
        // And "9/6/2020" is two different days depending on who is reading it. This column is scanned
        // down the page to compare timestamps; a big-endian pattern sorts visually and cannot be
        // misread, which is worth more here than matching the reader's locale.
        return BidiText.IsolateAsLtr(
            moment.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The DOS-style attribute letters, which are notation and never translated.
    /// </summary>
    /// <remarks>
    /// Every archive format and every file manager shows these as the same Latin letters, so a reader
    /// comparing this window against another is comparing like with like. They are pinned LTR for the
    /// same reason.
    /// </remarks>
    private static string AttributesOf(bool isFolder, FileAttributes? attributes, LocalizedStrings text)
    {
        if (isFolder)
        {
            return BidiText.IsolateAsLtr("DIR");
        }

        if (attributes is not { } flags)
        {
            return NotApplicable;
        }

        var letters = string.Concat(
            flags.HasFlag(FileAttributes.ReadOnly) ? "R" : string.Empty,
            flags.HasFlag(FileAttributes.Hidden) ? "H" : string.Empty,
            flags.HasFlag(FileAttributes.System) ? "S" : string.Empty,
            flags.HasFlag(FileAttributes.Archive) ? "A" : string.Empty);

        return letters.Length == 0 ? NotApplicable : BidiText.IsolateAsLtr(letters);
    }
}
