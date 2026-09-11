using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Engine;

namespace NOBA.Phase5.G1;

/// <summary>
/// The managed half of the G.1 feasibility harness: one C ABI entry point, and the real menu behind
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This measures the real cost, not a stand-in.</b> The question G.1 has to answer is whether a
/// C++ surrogate can ask .NET for a menu inside the budget of a held right-click. A harness that
/// round-tripped a trivial string would answer a question nobody asked; what costs time is the CLR
/// starting, the engine assemblies loading, the DI container building, the provider registry
/// resolving, and the first <see cref="ShellMenuBuilder"/> call. So all of that is here.
/// </para>
/// <para>
/// <b>It decides nothing.</b> Exactly as the Phase 5 plan requires of the real bridge: JSON in,
/// <see cref="ShellSelection"/>, <see cref="ShellMenuBuilder"/>, JSON out. No capability rule, no
/// format name, no command construction lives in this file or in the C++ that calls it.
/// </para>
/// </remarks>
public static class BridgeExports
{
    private static ServiceProvider? _provider;
    private static IArchiveProviderRegistry? _registry;
    private static IReadOnlyList<ArchiveFormat>? _creatable;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Builds the menu for a selection. UTF-8 JSON in, UTF-8 JSON out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry point, so the boundary cannot grow by accident. JSON rather than a struct layout
    /// because this crosses a language and a marshaller, and a silently mismatched struct is the
    /// kind of defect that surfaces as a wrong menu months later.
    /// </para>
    /// <para>
    /// Returns 0 on success and a negative code on failure. **It never throws across the boundary** -
    /// an exception escaping into native code is undefined behaviour, and here it would be undefined
    /// behaviour inside a surrogate host serving Explorer.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "NobaShellQuery")]
    public static unsafe int NobaShellQuery(byte* requestUtf8, byte** responseUtf8, int* responseLength)
    {
        try
        {
            if (requestUtf8 is null || responseUtf8 is null || responseLength is null)
            {
                return -1;
            }

            *responseUtf8 = null;
            *responseLength = 0;

            var request = Marshal.PtrToStringUTF8((nint)requestUtf8);
            if (string.IsNullOrEmpty(request))
            {
                return -2;
            }

            var response = Handle(request);
            var bytes = Encoding.UTF8.GetBytes(response);

            // Allocated with the same allocator the free function releases. The C++ side must call
            // NobaShellFree and nothing else.
            var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            ((byte*)buffer)[bytes.Length] = 0;

            *responseUtf8 = (byte*)buffer;
            *responseLength = bytes.Length;
            return 0;
        }
        catch (Exception ex)
        {
            // PRODUCTION would stop here: a code, no detail, nothing shown. A HARNESS that did that
            // is a harness whose failures cannot be diagnosed - which is exactly what happened on the
            // first run of this file. So the message is returned through the same buffer, and the
            // real bridge must NOT copy this part.
            try
            {
                var detail = Encoding.UTF8.GetBytes("{\"error\":" + JsonSerializer.Serialize(ex.ToString()) + "}");
                var buffer = Marshal.AllocHGlobal(detail.Length + 1);
                Marshal.Copy(detail, 0, buffer, detail.Length);
                ((byte*)buffer)[detail.Length] = 0;
                *responseUtf8 = (byte*)buffer;
                *responseLength = detail.Length;
            }
            catch (Exception)
            {
                // Nothing further is safe to attempt across the boundary.
            }

            return -99;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "NobaShellFree")]
    public static unsafe void NobaShellFree(byte* buffer)
    {
        if (buffer is not null)
        {
            Marshal.FreeHGlobal((nint)buffer);
        }
    }

    /// <summary>Warms the engine without asking for a menu, so cold and warm can be told apart.</summary>
    [UnmanagedCallersOnly(EntryPoint = "NobaShellWarm")]
    public static int NobaShellWarm()
    {
        try
        {
            EnsureEngine();
            return 0;
        }
        catch (Exception)
        {
            return -99;
        }
    }

    private static string Handle(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;

        // A deliberate fault injection path, so G.1 can measure what failure costs and prove it is
        // safe rather than asserting it.
        if (root.TryGetProperty("fault", out var fault) && fault.GetString() is { } kind)
        {
            return kind switch
            {
                "throw" => throw new InvalidOperationException("injected"),
                "slow" => SlowResponse(),
                _ => "{\"entries\":[]}",
            };
        }

        EnsureEngine();

        var items = new List<ShellItem>();
        if (root.TryGetProperty("items", out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                var path = item.GetProperty("path").GetString() ?? string.Empty;
                var isDirectory = item.TryGetProperty("isDirectory", out var d) && d.GetBoolean();
                items.Add(ShellItem.For(path, isDirectory));
            }
        }

        var selection = new ShellSelection(items);
        var menu = ShellMenuBuilder.Build(selection, ReportFor, _creatable!);

        var entries = menu.Select(e => new
        {
            action = e.Action.ToString(),
            format = e.Format?.Id,
            state = e.IsEnabled ? "enabled" : "disabled",
            blocker = e.Blocker.ToString(),
        });

        return JsonSerializer.Serialize(new { entries });
    }

    private static string SlowResponse()
    {
        Thread.Sleep(2000);
        return "{\"entries\":[]}";
    }

    private static FormatCapabilityReport ReportFor(ArchiveFormat format) =>
        new(format, _registry!.CapabilitiesFor(format));

    private static void EnsureEngine()
    {
        if (_provider is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_provider is not null)
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddNobaArchiveEngine(options =>
            {
                // Headless, and therefore obliged to name a conflict policy: the engine refuses
                // Headless + Ask at construction, because there is nobody to ask. It caught this
                // harness getting it wrong on the first run.
                options.Headless = true;
                options.DefaultConflictPolicy = ConflictPolicy.Fail;
            });

            var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<IArchiveProviderRegistry>();

            _creatable = registry.SupportedFormats
                .Where(f => new FormatCapabilityReport(f, registry.CapabilitiesFor(f)).CanCreate)
                .OrderBy(static f => f.Id, StringComparer.Ordinal)
                .ToList();

            _registry = registry;
            _provider = provider;
        }
    }
}
