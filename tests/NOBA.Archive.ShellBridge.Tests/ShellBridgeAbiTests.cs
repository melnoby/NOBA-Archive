using System.Text.Json;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.ShellBridge;
using Xunit;

namespace NOBA.Archive.ShellBridge.Tests;

/// <summary>
/// The JSON round trip, driven through the same managed entry point the C ABI calls.
/// </summary>
/// <remarks>
/// <para>
/// These go through <c>ShellBridgeExports.Handle</c> rather than through the unmanaged pointer, so
/// they exercise the real parsing, the real engine wiring and the real serialisation without needing
/// a native caller. What they do not cover is the marshalling itself - that needs the adapter, and it
/// is measured on Windows.
/// </para>
/// <para>
/// <b>The refusals matter more than the successes here.</b> Plan section E.3 requires that any bridge
/// failure start nothing, and a refusal that is indistinguishable from an empty menu would let the
/// adapter draw a submenu for a query the bridge never answered.
/// </para>
/// </remarks>
public sealed class ShellBridgeAbiTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_menu_query_answers_with_entries()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","items":[{"path":"C:\\d\\bundle.7z","isDirectory":false}]}"""));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.True(response.GetProperty("menu").GetProperty("entries").GetArrayLength() > 0);
    }

    /// <summary>
    /// The state strings are a wire contract: the C++ compares them literally, so a rename here is a
    /// menu that silently stops appearing.
    /// </summary>
    [Fact]
    public void Entry_states_are_the_three_wire_names_and_nothing_else()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","items":[{"path":"C:\\d\\stuff","isDirectory":true}]}"""));

        foreach (var entry in response.GetProperty("menu").GetProperty("entries").EnumerateArray())
        {
            var state = entry.GetProperty("state").GetString();
            Assert.True(
                state is "enabled" or "disabled" or "hidden",
                $"'{state}' is not one of the three states the adapter maps to ECS_*");
        }
    }

    /// <summary>
    /// The silent-fallback guard. <c>LocalizedStrings</c>' indexer returns the KEY when the key is
    /// missing, so a mistyped label produces a menu titled <c>shell.menuTitle</c> instead of an
    /// exception. The first draft of the bridge did exactly that, and nothing failed.
    /// </summary>
    [Fact]
    public void The_menu_title_resolves_to_a_string_and_not_to_its_own_key()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","items":[{"path":"C:\\d\\bundle.7z","isDirectory":false}]}"""));

        var title = response.GetProperty("menu").GetProperty("rootTitle").GetString();

        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.DoesNotContain("shell.", title!);
        Assert.Equal(LocalizedStrings.For(UiLanguage.English).ShellMenu, title);
    }

    /// <summary>Every label the menu publishes must resolve too, for the same reason.</summary>
    [Fact]
    public void No_entry_title_is_an_unresolved_key()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","items":[{"path":"C:\\d\\bundle.7z","isDirectory":false}]}"""));

        foreach (var entry in response.GetProperty("menu").GetProperty("entries").EnumerateArray())
        {
            var title = entry.GetProperty("title").GetString();
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.DoesNotContain("shell.", title!);
        }
    }

    /// <summary>The language is resolved managed-side, never in C++ (plan section E.1).</summary>
    [Fact]
    public void A_language_tag_changes_the_labels()
    {
        var english = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","language":"en","items":[{"path":"C:\\d\\bundle.7z","isDirectory":false}]}"""));
        var arabic = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","language":"ar","items":[{"path":"C:\\d\\bundle.7z","isDirectory":false}]}"""));

        Assert.NotEqual(
            english.GetProperty("menu").GetProperty("rootTitle").GetString(),
            arabic.GetProperty("menu").GetProperty("rootTitle").GetString());
        Assert.Equal(
            LocalizedStrings.For(UiLanguage.Arabic).ShellMenu,
            arabic.GetProperty("menu").GetProperty("rootTitle").GetString());
    }

    /// <summary>An unknown tag falls back rather than failing: a menu in the wrong language beats none.</summary>
    [Fact]
    public void An_unknown_language_falls_back_to_english()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","language":"zz-ZZ","items":[{"path":"C:\\d\\bundle.7z","isDirectory":false}]}"""));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(
            LocalizedStrings.For(UiLanguage.English).ShellMenu,
            response.GetProperty("menu").GetProperty("rootTitle").GetString());
    }

    [Fact]
    public void An_invoke_query_answers_with_an_executable_and_an_argument_array()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """
            {"kind":"invoke","entryId":"noba.compress.zip","items":[
              {"path":"C:\\d\\a.txt","isDirectory":false},
              {"path":"C:\\d\\b.txt","isDirectory":false}]}
            """));

        Assert.True(response.GetProperty("ok").GetBoolean());
        var invoke = response.GetProperty("invoke");
        Assert.False(string.IsNullOrWhiteSpace(invoke.GetProperty("executable").GetString()));
        Assert.True(invoke.GetProperty("arguments").GetArrayLength() > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Refusals. Each one must be distinguishable from an answer.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Malformed_json_is_refused_and_carries_no_menu()
    {
        var response = Parse(ShellBridgeExports.Handle("{ this is not json"));

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.False(response.TryGetProperty("menu", out _));
        Assert.False(response.TryGetProperty("invoke", out _));
    }

    [Fact]
    public void A_null_request_is_refused()
    {
        Assert.False(Parse(ShellBridgeExports.Handle(null)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void An_unknown_kind_is_refused_rather_than_guessed_at()
    {
        var response = Parse(ShellBridgeExports.Handle("""{"kind":"delete","items":[]}"""));

        Assert.False(response.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void An_invoke_without_an_entry_id_is_refused()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"invoke","items":[{"path":"C:\\d\\a.txt","isDirectory":false}]}"""));

        Assert.False(response.GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// The distinction that cost the E2 harness two runs, in its new home: an empty menu is an
    /// ANSWER - the model had nothing to offer - and a refusal is not. Collapsing them would let the
    /// adapter draw a submenu for a query the bridge never answered.
    /// </summary>
    [Fact]
    public void An_empty_selection_answers_with_a_hidden_menu_rather_than_a_refusal()
    {
        var response = Parse(ShellBridgeExports.Handle("""{"kind":"menu","items":[]}"""));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("hidden", response.GetProperty("menu").GetProperty("rootState").GetString());
    }

    /// <summary>An entry that is not available for this selection is refused, and starts nothing.</summary>
    [Fact]
    public void Invoking_an_entry_the_selection_does_not_offer_is_refused()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"invoke","entryId":"noba.open","items":[{"path":"C:\\d\\notes.txt","isDirectory":false}]}"""));

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.False(response.TryGetProperty("invoke", out _));
    }

    /// <summary>
    /// A folder is a folder however it is named. Re-deriving <c>isDirectory</c> from the path would
    /// offer to extract <c>Backups.zip\</c>, which the engine cannot honour.
    /// </summary>
    [Fact]
    public void A_directory_named_like_an_archive_is_not_treated_as_one()
    {
        var response = Parse(ShellBridgeExports.Handle(
            """{"kind":"menu","items":[{"path":"C:\\d\\Backups.zip","isDirectory":true}]}"""));

        var open = response.GetProperty("menu").GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "noba.open");

        Assert.Equal("hidden", open.GetProperty("state").GetString());
    }
}
