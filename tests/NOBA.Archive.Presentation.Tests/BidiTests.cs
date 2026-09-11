using System.Globalization;
using NOBA.Archive.Presentation.Bidi;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Bidirectional text: <c>BIDI-001…006</c>.
/// </summary>
/// <remarks>
/// These are security tests wearing typographic clothes. A path that renders with its segments
/// reordered is a path the user cannot judge, and the dialog asking them to judge it is then actively
/// misleading rather than merely ugly.
/// </remarks>
public sealed class BidiTests
{
    /// <summary>BIDI-001: the UI language decides direction, script and culture together.</summary>
    [Fact]
    public void Language_carries_direction_and_script()
    {
        Assert.Equal(FlowDirection.RightToLeft, UiLanguage.Arabic.Flow);
        Assert.Equal(UiScript.Arabic, UiLanguage.Arabic.Script);
        Assert.True(UiLanguage.Arabic.IsRightToLeft);

        Assert.Equal(FlowDirection.LeftToRight, UiLanguage.English.Flow);
        Assert.Equal(UiScript.Latin, UiLanguage.English.Script);
        Assert.False(UiLanguage.English.IsRightToLeft);
    }

    /// <summary>BIDI-002: an Arabic system culture selects Arabic; anything else falls back to English.</summary>
    [Theory]
    [InlineData("ar-EG", "ar")]
    [InlineData("ar", "ar")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "en")]
    public void The_system_culture_selects_a_language(string culture, string expected) =>
        Assert.Equal(expected, UiLanguage.ForCulture(CultureInfo.GetCultureInfo(culture)).Tag);

    /// <summary>
    /// BIDI-003: a path is isolated, and pinned left-to-right.
    /// </summary>
    /// <remarks>
    /// The traversal path is the case that matters: <c>..\..\evil.txt</c> beside an Arabic folder name
    /// is exactly the string a refusal dialog has to show correctly, and exactly the one the
    /// bidirectional algorithm rearranges without an isolate.
    /// </remarks>
    [Fact]
    public void A_path_is_isolated_left_to_right()
    {
        const string path = @"مستندات\..\..\evil.txt";
        var display = BidiText.IsolateAsLtr(path);

        Assert.Equal(BidiText.LeftToRightIsolate, display[0]);
        Assert.Equal(BidiText.PopDirectionalIsolate, display[^1]);
        Assert.True(display.Contains(path, StringComparison.Ordinal), display);
    }

    /// <summary>BIDI-004: a name is isolated by first strong character, so either script reads right.</summary>
    [Fact]
    public void A_name_is_isolated_by_first_strong_character()
    {
        var arabic = BidiText.Isolate("تقرير.txt");
        var latin = BidiText.Isolate("report.txt");

        Assert.Equal(BidiText.FirstStrongIsolate, arabic[0]);
        Assert.Equal(BidiText.FirstStrongIsolate, latin[0]);
        Assert.Equal(BidiText.PopDirectionalIsolate, arabic[^1]);
        Assert.Equal(BidiText.PopDirectionalIsolate, latin[^1]);
    }

    /// <summary>BIDI-005: empty input produces empty output rather than a pair of invisible marks.</summary>
    [Fact]
    public void Empty_input_is_left_alone()
    {
        Assert.Equal(string.Empty, BidiText.Isolate(null));
        Assert.Equal(string.Empty, BidiText.Isolate(string.Empty));
        Assert.Equal(string.Empty, BidiText.IsolateAsLtr(null));
    }

    /// <summary>BIDI-006: numbers are LTR runs in both languages, with the culture's own digits.</summary>
    /// <remarks>
    /// Direction and digit shape are separate decisions. Arabic-Indic digits in a right-to-left
    /// paragraph still run left to right, and a size column that flips is a size column that misleads.
    /// </remarks>
    [Fact]
    public void Numbers_are_ltr_runs_in_both_languages()
    {
        foreach (var language in UiLanguage.Supported)
        {
            var formatted = BidiText.FormatCount(1234567, language.Culture);

            Assert.Equal(BidiText.LeftToRightIsolate, formatted[0]);
            Assert.Equal(BidiText.PopDirectionalIsolate, formatted[^1]);
        }
    }

    /// <summary>BIDI-007: right-to-left content is detected, so callers can decide to isolate.</summary>
    [Theory]
    [InlineData("report.txt", false)]
    [InlineData("تقرير.txt", true)]
    [InlineData("docs/تقرير.txt", true)]
    [InlineData("", false)]
    public void Right_to_left_content_is_detected(string value, bool expected) =>
        Assert.Equal(expected, BidiText.ContainsRightToLeft(value));
}

/// <summary>Localisation: <c>L10N-001…003</c>.</summary>
public sealed class LocalizationTests
{
    /// <summary>
    /// L10N-001: both languages define exactly the same keys.
    /// </summary>
    /// <remarks>
    /// The most common localisation defect there is, and the one that shows up as a blank label rather
    /// than as an error. Holding the strings as data instead of as <c>.resw</c> files is what lets this
    /// be a test at all — and one that runs where WinUI cannot even be restored.
    /// </remarks>
    [Fact]
    public void Both_languages_define_the_same_keys()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        var english = LocalizedStrings.For(UiLanguage.English);

        foreach (var key in LocalizedStrings.Keys)
        {
            Assert.True(!string.IsNullOrWhiteSpace(arabic[key]), $"Arabic is missing '{key}'");
            Assert.True(!string.IsNullOrWhiteSpace(english[key]), $"English is missing '{key}'");
            Assert.True(arabic[key] != key, $"Arabic falls through for '{key}'");
            Assert.True(english[key] != key, $"English falls through for '{key}'");
        }
    }

    /// <summary>L10N-002: the two languages are actually different text, not one copied over the other.</summary>
    [Fact]
    public void The_languages_are_not_the_same_strings()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);
        var english = LocalizedStrings.For(UiLanguage.English);

        var identical = LocalizedStrings.Keys
            .Where(key => string.Equals(arabic[key], english[key], StringComparison.Ordinal))
            .ToList();

        // "app.name" is deliberately not on this list: the product is named in both languages, and the
        // language toggle shows the *other* language's name, so those differ too.
        Assert.True(identical.Count == 0, $"untranslated: {string.Join(", ", identical)}");
    }

    /// <summary>L10N-003: Arabic text really is Arabic.</summary>
    /// <remarks>
    /// Cheap, and it catches the failure where a merge leaves the English string in the Arabic table:
    /// every key still exists, nothing is blank, and the UI is silently half-translated.
    /// </remarks>
    [Fact]
    public void Arabic_strings_contain_arabic()
    {
        var arabic = LocalizedStrings.For(UiLanguage.Arabic);

        // The language toggle is the exception: in the Arabic UI it reads "English", by design.
        var suspicious = LocalizedStrings.Keys
            .Where(static key => key != "action.toggleLanguage")
            .Where(key => !BidiText.ContainsRightToLeft(arabic[key]))
            .ToList();

        Assert.True(suspicious.Count == 0, $"not Arabic: {string.Join(", ", suspicious)}");
    }
}
