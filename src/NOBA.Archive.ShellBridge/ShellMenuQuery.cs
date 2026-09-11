using System.Text.Json;
using System.Text.Json.Serialization;

namespace NOBA.Archive.ShellBridge;

/// <summary>What the adapter is asking for.</summary>
public enum ShellQueryKind
{
    /// <summary>Unrecognised. Never a default that does something.</summary>
    Unknown = 0,

    /// <summary>Draw the submenu for a selection.</summary>
    Menu,

    /// <summary>The person clicked an entry; produce the launch plan.</summary>
    Invoke,
}

/// <summary>One selected item, exactly as Explorer described it.</summary>
/// <param name="Path">A filesystem path, from <c>SIGDN_FILESYSPATH</c>.</param>
/// <param name="IsDirectory">From <c>SFGAO_FOLDER</c>. Asked of the shell, never inferred from the path.</param>
public sealed record ShellQueryItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory);

/// <summary>
/// The request the C++ adapter sends across the ABI.
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON rather than a struct layout</b>, because the boundary crosses a language and a
/// marshaller, and a silently mismatched struct is the kind of defect that shows up as a wrong menu
/// six months later (plan section B.3).
/// </para>
/// <para>
/// <b>Nothing here interprets.</b> <c>isDirectory</c> arrives as a fact the shell already knew;
/// deriving it from the path would be the adapter deciding something, which plan section B.2
/// forbids, and would be wrong for a directory whose name ends in <c>.zip</c>.
/// </para>
/// </remarks>
public sealed class ShellQuery
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The registered entry id, for <see cref="ShellQueryKind.Invoke"/>. Null for a menu query.</summary>
    [JsonPropertyName("entryId")]
    public string? EntryId { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<ShellQueryItem>? Items { get; init; }

    /// <summary>
    /// BCP-47 tag for the label language, or null for the process default. Resolved managed-side by
    /// the same rule the application uses; never re-derived in C++ (plan section E.1).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    public ShellQueryKind ResolvedKind => Kind switch
    {
        "menu" => ShellQueryKind.Menu,
        "invoke" => ShellQueryKind.Invoke,
        _ => ShellQueryKind.Unknown,
    };

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses a request. Returns null for anything malformed rather than throwing across the ABI.
    /// </summary>
    /// <remarks>
    /// A parse failure is a bridge failure, and plan section E.3's fail-safe applies to it: the
    /// adapter returns a failure HRESULT and starts nothing. Returning null rather than throwing
    /// keeps that decision on the managed side of an exception boundary that cannot carry one.
    /// </remarks>
    public static ShellQuery? Parse(string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ShellQuery>(requestJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
