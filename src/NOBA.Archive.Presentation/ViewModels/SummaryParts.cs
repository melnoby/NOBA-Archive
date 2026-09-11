using System.Globalization;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>What a summary fragment is, so a renderer can style it without parsing it.</summary>
public enum SummaryPartKind
{
    /// <summary>A number, a size, a percentage — notation.</summary>
    Value = 0,

    /// <summary>A word in the UI language.</summary>
    Label,

    /// <summary>The dot between facts.</summary>
    Separator,
}

/// <summary>Which way this fragment runs, independently of the fragments beside it.</summary>
public enum SummaryDirection
{
    LeftToRight = 0,
    RightToLeft,
}

/// <summary>
/// One fragment of a summary line, carrying its own direction.
/// </summary>
/// <remarks>
/// <para>
/// The summary used to be a single string, and under an Arabic UI its digits came out Arabic-Indic
/// while the identical digits in the table beside it came out Western. Three attempts failed on it:
/// a <c>LEFT-TO-RIGHT MARK</c> around the number, formatting with <see cref="CultureInfo.InvariantCulture"/>,
/// and setting <c>FlowDirection</c> on the text block. None worked, because none of them addressed what
/// was actually happening.
/// </para>
/// <para>
/// The digits leave .NET as ASCII in every one of those cases. What replaces them is the text shaper,
/// which substitutes national digits when the run they sit in has Arabic in it — and a bidi control
/// character is not a strong character, an element's flow direction is not the run's content, and the
/// culture never entered into it. The evidence was in the window the whole time: every table cell holds
/// digits and no Arabic word, and every table cell renders Western. So the fix is not to argue with the
/// shaper but to stop handing it mixed runs.
/// </para>
/// </remarks>
public sealed record SummaryPart(string Text, SummaryPartKind Kind, SummaryDirection Direction)
{
    /// <summary>Notation: a count, a size, a percentage. Always left to right.</summary>
    public static SummaryPart Value(string text) =>
        new(text, SummaryPartKind.Value, SummaryDirection.LeftToRight);

    /// <summary>A word, running in the UI language's own direction.</summary>
    public static SummaryPart Label(string text, UiLanguage language) =>
        new(
            text,
            SummaryPartKind.Label,
            language.IsRightToLeft ? SummaryDirection.RightToLeft : SummaryDirection.LeftToRight);

    public static SummaryPart Separator { get; } =
        new("·", SummaryPartKind.Separator, SummaryDirection.LeftToRight);
}

/// <summary>
/// Builds the archive and selection summaries as fragments rather than as a sentence.
/// </summary>
/// <remarks>
/// A formatter rather than a property on the view model, for the same reason
/// <see cref="RowColumnFormatter"/> is one: it reads what the view model already exposes, adds no state,
/// and can be tested on a machine with no WinUI. <c>BrowseViewModel</c> is untouched.
/// </remarks>
public static class SummaryFormatter
{
    /// <summary>The archive's own facts: counts, size, ratio, and how it was made.</summary>
    public static IReadOnlyList<SummaryPart> ForArchive(ArchiveStatistics? statistics, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (statistics is null)
        {
            return [];
        }

        var language = text.Language;
        var parts = new List<SummaryPart>(12)
        {
            SummaryPart.Value(Count(statistics.FileCount)),
            SummaryPart.Label(text.FilesLabel, language),
            SummaryPart.Separator,
            SummaryPart.Value(Count(statistics.DirectoryCount)),
            SummaryPart.Label(text.FoldersLabel, language),
        };

        // The archive's own declared total, not the tree's sum. The counts beside it are the
        // statistics' counts, which include entries the reader was refused; taking the size from the
        // same record keeps the three numbers describing one archive rather than two.
        if (statistics.TotalUncompressedSize.Bytes > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(Size(statistics.TotalUncompressedSize)));
        }

        if (statistics.RejectedEntryCount > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(Count(statistics.RejectedEntryCount)));
            parts.Add(SummaryPart.Label(text.BlockedLabel, language));
        }

        // The one number that says whether compressing this was worth doing.
        if (statistics.CompressionRatio is { } ratio && ratio > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(ratio.ToString("P0", CultureInfo.InvariantCulture)));
        }

        // Solid and multi-volume both change what extraction costs, so they are facts, not trivia.
        if (statistics.IsSolid)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Label(text.SolidLabel, language));
        }

        if (statistics.VolumeCount > 1)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(Count(statistics.VolumeCount)));
            parts.Add(SummaryPart.Label(text.VolumesLabel, language));
        }

        return parts;
    }

    /// <summary>What is selected, or nothing at all when nothing is.</summary>
    public static IReadOnlyList<SummaryPart> ForSelection(
        IReadOnlyCollection<ArchiveNode> selection,
        LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(text);

        if (selection.Count == 0)
        {
            return [];
        }

        var parts = new List<SummaryPart>(4)
        {
            SummaryPart.Value(Count(selection.Count)),
            SummaryPart.Label(text.SelectedLabel, text.Language),
        };

        var bytes = selection.Sum(static node => node.TotalSize.Bytes);
        if (bytes > 0)
        {
            parts.Add(SummaryPart.Separator);
            parts.Add(SummaryPart.Value(Size(ByteSize.FromBytes(bytes))));
        }

        return parts;
    }

    /// <summary>
    /// A count, with no isolate wrapper.
    /// </summary>
    /// <remarks>
    /// The isolate belonged to the old single-string form, where the digits had to survive being sat
    /// next to Arabic. A fragment that contains nothing but digits has nothing to be isolated from, and
    /// the control characters would only show up in a test's expected value.
    /// </remarks>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Size(ByteSize size) => size.ToDisplayString(CultureInfo.InvariantCulture);
}
