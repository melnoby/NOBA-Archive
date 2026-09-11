using System.Text.Json;
using System.Text.Json.Serialization;

namespace NOBA.Archive.ShellBridge;

/// <summary>
/// The three states <c>IExplorerCommand::GetState</c> can express, named as the model names them.
/// </summary>
/// <remarks>
/// <b>The third state is the point of Phase 5.</b> The classic registry surface has only present and
/// absent, so <i>Compress to GZIP</i> on a folder had to be omitted entirely (SHELL-L9). Here the
/// model's <c>ShellMenuEntry.Disabled(..., ShellBlocker.SingleMemberFormat)</c> finally reaches a
/// surface that can show it greyed. The C++ maps these to <c>ECS_ENABLED</c> / <c>ECS_DISABLED</c> /
/// <c>ECS_HIDDEN</c> and does no interpretation (plan section C.1 step 6).
/// </remarks>
public enum ShellEntryState
{
    Hidden = 0,
    Disabled,
    Enabled,
}

/// <summary>One line of the submenu, ready to draw.</summary>
/// <param name="Id">
/// The registered entry id, e.g. <c>noba.compress.zip</c>. It comes from
/// <c>ShellRegistration.Entries</c> and is never composed here - the id scheme has one home, for the
/// same reason the capability derivation does.
/// </param>
/// <param name="Title">The label, already localised and already expanded per format.</param>
/// <param name="State">Enabled, disabled or hidden.</param>
/// <param name="Blocker">
/// Why a disabled entry is disabled, as the model's own name for it. Carried even though nothing
/// displays it yet: whether a greyed entry can explain itself is unverified and deferred to section
/// G.5, and a reason that was never carried across the boundary cannot be shown once it is answered.
/// </param>
public sealed record ShellMenuEntryResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("blocker")] string? Blocker);

/// <summary>
/// The response to a <c>menu</c> query.
/// </summary>
/// <param name="RootState">
/// The submenu root. <c>hidden</c> when every child is hidden, so an empty submenu never appears
/// (plan section D). Decided here with the rest of the model, not by the C++.
/// </param>
public sealed record ShellMenuResult(
    [property: JsonPropertyName("rootState")] string RootState,
    [property: JsonPropertyName("rootTitle")] string RootTitle,
    [property: JsonPropertyName("entries")] IReadOnlyList<ShellMenuEntryResult> Entries);

/// <summary>
/// The response to an <c>invoke</c> query: one executable and one argument array.
/// </summary>
/// <remarks>
/// <b>An argument array, never a command string.</b> The C++ passes this to <c>CreateProcessW</c>
/// without composing a line and without <c>cmd.exe</c>, which is what makes quoting a non-question
/// (plan section C.2 step 6).
/// </remarks>
public sealed record ShellInvokeResult(
    [property: JsonPropertyName("executable")] string Executable,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments);

/// <summary>
/// What the bridge hands back for any query, including the ones it refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refusal is a value, not an exception.</b> Exceptions cannot cross the ABI, and plan section
/// E.3's fail-safe requires that any bridge failure start nothing. So every outcome is expressible:
/// <c>ok</c> false with an <c>error</c> the adapter logs and does not interpret.
/// </para>
/// <para>
/// <b>An absent menu and a failed query are different answers.</b> An empty <c>menu</c> with
/// <c>rootState: hidden</c> means the model had nothing to offer for this selection, which is a
/// correct result. <c>ok: false</c> means the bridge could not answer. Collapsing the two would be
/// the same defect the E2 harness spent two runs on.
/// </para>
/// </remarks>
public sealed class ShellBridgeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("menu")]
    public ShellMenuResult? Menu { get; init; }

    [JsonPropertyName("invoke")]
    public ShellInvokeResult? Invoke { get; init; }

    public static ShellBridgeResponse Failure(string error) => new() { Ok = false, Error = error };

    public static ShellBridgeResponse ForMenu(ShellMenuResult menu) => new() { Ok = true, Menu = menu };

    public static ShellBridgeResponse ForInvoke(ShellInvokeResult invoke) => new() { Ok = true, Invoke = invoke };

    public string ToJson() => JsonSerializer.Serialize(this, ShellQuery.Json);
}
