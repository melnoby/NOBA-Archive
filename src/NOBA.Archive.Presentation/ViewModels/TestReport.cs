using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>How a test result should read, independently of how it is painted.</summary>
/// <remarks>
/// A tone, not a colour. WCAG AA and the product's own rules forbid carrying meaning in colour alone,
/// so every tone is also given a headline and a chip that say the same thing in words.
/// </remarks>
public enum TestTone
{
    /// <summary>Nothing wrong.</summary>
    Good = 0,

    /// <summary>Extractable, but something is worth knowing.</summary>
    Warning,

    /// <summary>The data itself is damaged.</summary>
    Bad,

    /// <summary>Not a verdict on the archive: a statement about this build or a missing password.</summary>
    Info,
}

/// <summary>One line of the findings list.</summary>
/// <remarks>
/// <para>
/// <see cref="Message"/> is the engine's own words and is deliberately <em>not</em> translated. It comes
/// from the backend — 7-Zip's operation result, or the managed ZIP reader's exception — and inventing an
/// Arabic paraphrase of a technical failure would be a translation of something we did not author and
/// cannot guarantee the meaning of. What is localised is <see cref="ChipLabel"/>, which says whether the
/// entry is damaged or merely worth noting; the engine's sentence stands beside it as evidence.
/// </para>
/// <para>
/// <see cref="EntryPath"/> is a path and belongs in a left-to-right run of its own — the same rule the
/// file table follows, and the reason it is a separate field rather than folded into the message.
/// </para>
/// </remarks>
public sealed record TestFindingRow(string EntryPath, string Message, bool IsError, string ChipLabel);

/// <summary>
/// A test result, shaped for reading.
/// </summary>
/// <remarks>
/// The detail line is <see cref="SummaryPart"/> fragments rather than a sentence, for the reason the
/// archive summary already is: a count and an Arabic word in one text run come out in Arabic-Indic
/// digits while the identical count in the table beside it comes out Western.
/// </remarks>
public sealed record TestReport(
    TestTone Tone,
    string Headline,
    IReadOnlyList<SummaryPart> Detail,
    IReadOnlyList<TestFindingRow> Findings,
    bool IsUsable);

/// <summary>
/// Turns an engine verdict into something a person can read.
/// </summary>
/// <remarks>
/// A formatter rather than a property on the view model, for the same reason
/// <see cref="RowColumnFormatter"/> and <see cref="SummaryFormatter"/> are: it adds no state, reads only
/// what the engine already returned, and runs on a machine that cannot build WinUI.
/// </remarks>
public static class TestReportFormatter
{
    /// <param name="format">
    /// What the format can verify, when it is known.
    /// </param>
    /// <remarks>
    /// Optional, and its absence changes nothing but the caveat. A clean verdict means two different
    /// things depending on the format: for ZIP, 7Z, RAR and GZIP every entry's checksum was recomputed
    /// and matched; for TAR there is no checksum over content to recompute, so what passed was the
    /// structure. Both are honest answers to "test this archive", and only one of them is a statement
    /// about the data. The report says which it gave, and it reads the difference from the capability
    /// contract rather than from the format's name.
    /// </remarks>
    public static TestReport For(
        ArchiveTestResult result,
        LocalizedStrings text,
        FormatCapabilityReport? format = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);

        var language = text.Language;

        // Unsupported is not a verdict on the file. 7-Zip answers "unsupported method" when it cannot
        // build a decoder, and the engine already refuses to call that corruption; the UI has to keep
        // that distinction or it will tell someone to restore a backup they do not need.
        var (tone, headline) = result.Status switch
        {
            ArchiveTestStatus.Valid => (TestTone.Good, text.TestValid),
            ArchiveTestStatus.ValidWithWarnings => (TestTone.Warning, text.TestWarnings),
            ArchiveTestStatus.Corrupted => (TestTone.Bad, text.TestCorrupted),
            ArchiveTestStatus.Unsupported => (TestTone.Info, text.TestUnsupported),
            ArchiveTestStatus.PasswordRequired => (TestTone.Info, text.TestPasswordRequired),
            _ => (TestTone.Bad, text.TestFailed),
        };

        var detail = new List<SummaryPart>(8)
        {
            SummaryPart.Value(SummaryFormatter.Count(result.EntriesTested)),
            SummaryPart.Label(text.TestEntriesLabel, language),
        };

        if (result.Findings.Count > 0)
        {
            detail.Add(SummaryPart.Separator);
            detail.Add(SummaryPart.Value(SummaryFormatter.Count(result.Findings.Count)));
            detail.Add(SummaryPart.Label(text.TestFindingsLabel, language));
        }
        else if (result.Status == ArchiveTestStatus.Valid)
        {
            detail.Add(SummaryPart.Separator);

            // "No checksum errors" is the right thing to say about a format that has checksums. For one
            // that has none it is a sentence with no referent - it invites the reader to conclude that
            // checksums were examined and were fine. The caveat replaces it rather than joining it,
            // because saying both would be answering the same question twice, differently.
            detail.Add(SummaryPart.Label(
                format is { CanVerifyContents: false } ? text.FormatNoContentChecksum : text.TestCleanDetail,
                language));
        }

        if (result.Status == ArchiveTestStatus.Unsupported)
        {
            detail.Add(SummaryPart.Separator);
            detail.Add(SummaryPart.Label(text.TestUnsupportedDetail, language));
        }

        // Said on the usable runs that carried findings of their own, where the clean-detail branch
        // above did not fire. A person reading "corrupted" needs no footnote about what was checked; a
        // person reading "intact" is forming a belief about their data, and for a format with no
        // content checksum that belief would be wider than the evidence.
        if (format is { CanVerifyContents: false } && result.IsUsable && result.Findings.Count > 0)
        {
            detail.Add(SummaryPart.Separator);
            detail.Add(SummaryPart.Label(text.FormatNoContentChecksum, language));
        }

        // Errors first, and OrderBy rather than List.Sort because that one is not stable: within the
        // errors the engine's own order is the order the entries appear in the archive, which is the
        // only ordering that helps someone looking for the damage.
        var findings = result.Findings
            .Select(f => new TestFindingRow(
                f.EntryPath,
                f.Message,
                f.IsError,
                f.IsError ? text.TestErrorChip : text.TestWarningChip))
            .OrderByDescending(static f => f.IsError)
            .ToList();

        return new TestReport(tone, headline, detail, findings, result.IsUsable);
    }
}
