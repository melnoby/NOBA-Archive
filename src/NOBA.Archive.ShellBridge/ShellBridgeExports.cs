using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Engine;
using NOBA.Archive.Presentation.Bidi;

namespace NOBA.Archive.ShellBridge;

/// <summary>
/// The C ABI the native adapter calls, and the only unmanaged surface this assembly has.
/// </summary>
/// <remarks>
/// <para>
/// <b>One function, so the surface cannot grow by accident</b> (plan section B.3). Two request kinds
/// discriminated inside the JSON, rather than two exports that would each need marshalling,
/// versioning and a test.
/// </para>
/// <para>
/// <b>Nothing throws across this boundary.</b> An exception crossing an unmanaged call boundary is a
/// process kill, and this process is a surrogate that Explorer is holding a right-click open on.
/// Every failure becomes a response with <c>ok: false</c>, and plan section E.3's fail-safe applies:
/// the adapter returns a failure HRESULT and starts nothing.
/// </para>
/// </remarks>
public static class ShellBridgeExports
{
    /// <summary>Success. A response was produced, whatever it says.</summary>
    private const int Ok = 0;

    /// <summary>The bridge could not produce a response at all - not even a failing one.</summary>
    private const int Failed = -1;

    /// <summary>
    /// Answers one query. UTF-8 in, UTF-8 out; the caller frees the response with
    /// <see cref="NobaShellFree"/>.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "NobaShellQuery")]
    public static unsafe int NobaShellQuery(byte* requestJson, byte** responseJson, int* responseLength)
    {
        if (responseJson is null || responseLength is null)
        {
            return Failed;
        }

        *responseJson = null;
        *responseLength = 0;

        try
        {
            var request = requestJson is null ? null : Marshal.PtrToStringUTF8((nint)requestJson);
            var response = Handle(request);

            var bytes = Encoding.UTF8.GetBytes(response);
            var buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            new Span<byte>(bytes).CopyTo(new Span<byte>(buffer, bytes.Length));
            buffer[bytes.Length] = 0;

            *responseJson = buffer;
            *responseLength = bytes.Length;
            return Ok;
        }
        catch (Exception)
        {
            // Deliberately unfiltered. There is no exception worth letting escape into a surrogate
            // process holding somebody's context menu open, and the adapter's contract already says
            // that a non-zero return starts nothing.
            return Failed;
        }
    }

    /// <summary>Frees a response buffer.</summary>
    [UnmanagedCallersOnly(EntryPoint = "NobaShellFree")]
    public static unsafe void NobaShellFree(byte* responseJson)
    {
        if (responseJson is not null)
        {
            NativeMemory.Free(responseJson);
        }
    }

    /// <summary>
    /// Warms the runtime and the engine without answering anything.
    /// </summary>
    /// <remarks>
    /// <c>GetState</c> returns <c>E_PENDING</c> when the CLR is cold and <c>fOkToBeSlow</c> is FALSE,
    /// and the shell re-asks on a background thread (plan section 0.2). This is what the adapter calls
    /// on that background thread. It answers no query, so a warm-up can never draw a menu.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "NobaShellWarm")]
    public static int NobaShellWarm()
    {
        try
        {
            _ = Catalog.Value;
            return Ok;
        }
        catch (Exception)
        {
            return Failed;
        }
    }

    /// <summary>
    /// The managed entry point, in managed types, so the whole round trip is testable without an
    /// unmanaged caller.
    /// </summary>
    internal static string Handle(string? requestJson)
    {
        var query = ShellQuery.Parse(requestJson);
        if (query is null)
        {
            return ShellBridgeResponse.Failure("request was not valid JSON").ToJson();
        }

        ShellBridgeCatalog catalog;
        try
        {
            catalog = Catalog.Value;
        }
        catch (Exception ex)
        {
            return ShellBridgeResponse.Failure($"engine unavailable: {ex.GetType().Name}").ToJson();
        }

        var selection = SelectionFrom(query);

        switch (query.ResolvedKind)
        {
            case ShellQueryKind.Menu:
                return ShellBridgeResponse.ForMenu(
                    ShellBridgeService.Menu(selection, Localised(catalog, query.Language))).ToJson();

            case ShellQueryKind.Invoke:
                if (string.IsNullOrEmpty(query.EntryId))
                {
                    return ShellBridgeResponse.Failure("invoke without an entryId").ToJson();
                }

                var plan = ShellBridgeService.Invoke(query.EntryId, selection, Localised(catalog, query.Language));
                return plan is null
                    ? ShellBridgeResponse.Failure($"'{query.EntryId}' is not available for this selection").ToJson()
                    : ShellBridgeResponse.ForInvoke(new ShellInvokeResult(plan.Executable, plan.Arguments)).ToJson();

            default:
                return ShellBridgeResponse.Failure($"unknown request kind '{query.Kind}'").ToJson();
        }
    }

    /// <summary>
    /// The selection, built from what the shell already knew.
    /// </summary>
    /// <remarks>
    /// <c>isDirectory</c> is taken from the request, never re-derived from the path: it came from
    /// <c>SFGAO_FOLDER</c>, and a folder named <c>Backups.zip</c> is a folder.
    /// </remarks>
    private static ShellSelection SelectionFrom(ShellQuery query) =>
        new([.. (query.Items ?? [])
            .Where(static i => !string.IsNullOrWhiteSpace(i.Path))
            .Select(static i => ShellItem.For(i.Path, i.IsDirectory))]);

    /// <remarks>
    /// <b>The typed properties, not the string keys.</b> <c>LocalizedStrings</c>' indexer returns the
    /// KEY when a key is missing, so a typo produces a menu titled <c>shell.menuTitle</c> rather than
    /// an exception - silent, and shipped. The first draft of this file did exactly that. Every label
    /// here goes through a compiled property, and the assertion in
    /// <c>The_menu_title_resolves_to_a_string_and_not_to_its_own_key</c> covers what remains.
    /// </remarks>
    private static ShellBridgeCatalog Localised(ShellBridgeCatalog catalog, string? languageTag)
    {
        var strings = LocalizedStrings.For(LanguageFor(languageTag));
        return catalog with
        {
            TextFor = key => strings[key],
            MenuTitle = strings.ShellMenu,
        };
    }

    /// <summary>
    /// The label language, resolved here rather than in C++ (plan section E.1). An unknown or absent
    /// tag falls back to English rather than failing - a menu in the wrong language is a smaller
    /// problem than no menu.
    /// </summary>
    private static UiLanguage LanguageFor(string? tag) =>
        UiLanguage.Supported.FirstOrDefault(l =>
            tag is not null && tag.StartsWith(l.Tag, StringComparison.OrdinalIgnoreCase))
        ?? UiLanguage.English;

    /// <summary>
    /// The engine, built once per surrogate process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lazy, and thread-safe by construction.</b> The shell calls <c>GetState</c> on more than one
    /// thread, and two of them racing to build a provider registry inside a right-click is not a
    /// failure anybody would reproduce on purpose.
    /// </para>
    /// <para>
    /// <b>Headless, and therefore obliged to name a conflict policy.</b> The engine refuses
    /// Headless + Ask at construction because there is nobody to ask - it caught the G.1 harness
    /// getting this wrong on its first run. A shell menu has even less of a person available than a
    /// harness does.
    /// </para>
    /// </remarks>
    private static readonly Lazy<ShellBridgeCatalog> Catalog = new(BuildCatalog, isThreadSafe: true);

    private static ShellBridgeCatalog BuildCatalog()
    {
        var services = new ServiceCollection();
        services.AddNobaArchiveEngine(options =>
        {
            options.Headless = true;
            options.DefaultConflictPolicy = ConflictPolicy.Fail;
        });

        var registry = services.BuildServiceProvider().GetRequiredService<IArchiveProviderRegistry>();

        FormatCapabilityReport ReportFor(ArchiveFormat format) =>
            new(format, registry.CapabilitiesFor(format));

        var strings = LocalizedStrings.For(UiLanguage.English);

        return new ShellBridgeCatalog(
            ReportFor,
            CreatableFormats.From(registry.SupportedFormats, ReportFor),
            key => strings[key],
            strings.ShellMenu);
    }
}
