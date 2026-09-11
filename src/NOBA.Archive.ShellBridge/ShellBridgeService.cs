using NOBA.Archive.Core.Actions;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.ShellBridge;

/// <summary>
/// The facts about a build that the bridge needs, and nothing else.
/// </summary>
/// <remarks>
/// Passed in rather than resolved here, so every rule in <see cref="ShellBridgeService"/> is testable
/// without an engine, a registry, a process or a platform. That is the whole reason this type exists:
/// the shell adapter's decisions must be falsifiable on a build machine, not only inside a right-click
/// on somebody's desktop.
/// </remarks>
/// <param name="ReportFor">The capability report for a format.</param>
/// <param name="CreatableFormats">
/// The writable formats, from <see cref="Core.Formats.CreatableFormats.From"/> and nowhere else.
/// </param>
/// <param name="TextFor">The localiser, keyed by <c>ShellVerb.TextKey</c>.</param>
/// <param name="MenuTitle">The submenu root's label.</param>
public sealed record ShellBridgeCatalog(
    Func<ArchiveFormat, FormatCapabilityReport> ReportFor,
    IReadOnlyList<ArchiveFormat> CreatableFormats,
    Func<string, string> TextFor,
    string MenuTitle);

/// <summary>
/// Everything the modern shell surface decides, and the only place it is decided.
/// </summary>
/// <remarks>
/// <para>
/// <b>This layer owns no rules of its own.</b> Availability comes from
/// <see cref="ShellMenuBuilder"/>; ids and labels come from <see cref="ShellRegistration.Entries"/>;
/// the command comes from <see cref="ShellLauncher.PlanFor"/>. What lives here is the <i>join</i>
/// between them, and the JSON shape. If a rule ever appears in this file, the adapter is doing
/// something plan section B.2 forbids.
/// </para>
/// <para>
/// <b>Why the response enumerates REGISTERED entries rather than menu entries.</b> The submenu is one
/// sub-command object per registered entry (plan section D), and the ids must be stable across draws
/// so <c>Invoke</c> can name one. <see cref="ShellMenuBuilder"/> answers a different question - what
/// is available for <i>this</i> selection - and answers it by omission. Joining the two on
/// (action, format) is what turns "absent from the menu" into an explicit <c>hidden</c>, which is the
/// third state the classic surface never had.
/// </para>
/// </remarks>
public static class ShellBridgeService
{
    /// <summary>The submenu for a selection.</summary>
    public static ShellMenuResult Menu(ShellSelection selection, ShellBridgeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);

        var registered = ShellRegistration.Entries(catalog.ReportFor, catalog.CreatableFormats, catalog.TextFor);
        var available = ShellMenuBuilder.Build(selection, catalog.ReportFor, catalog.CreatableFormats);

        var results = new List<ShellMenuEntryResult>(registered.Count);
        foreach (var entry in registered)
        {
            var match = MatchFor(available, entry);
            var state = match is null ? ShellEntryState.Hidden
                      : match.IsEnabled ? ShellEntryState.Enabled
                      : ShellEntryState.Disabled;

            results.Add(new ShellMenuEntryResult(
                entry.Id,
                entry.Label,
                Name(state),
                state == ShellEntryState.Disabled ? match!.Blocker.ToString() : null));
        }

        // Hidden when every child is hidden, so an empty submenu never appears. Note this is asked of
        // the RESULTS, not of the selection: a selection that yields nothing and a build that can
        // write nothing produce the same empty menu, and the root must disappear for both.
        var rootVisible = results.Exists(static r => !string.Equals(r.State, "hidden", StringComparison.Ordinal));

        return new ShellMenuResult(
            Name(rootVisible ? ShellEntryState.Enabled : ShellEntryState.Hidden),
            catalog.MenuTitle,
            results);
    }

    /// <summary>
    /// The launch plan for a clicked entry, or null if it may not be invoked.
    /// </summary>
    /// <remarks>
    /// <b>Re-asked, not trusted (plan section E.3).</b> The selection can change between the draw and
    /// the click, so availability is recomputed rather than assumed to still hold. An entry that is
    /// hidden or disabled now returns null however it looked when the menu was drawn, and the adapter
    /// turns that into <c>E_ACCESSDENIED</c> and starts nothing.
    /// </remarks>
    public static ShellLaunchPlan? Invoke(string entryId, ShellSelection selection, ShellBridgeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);

        var registered = ShellRegistration.Entries(catalog.ReportFor, catalog.CreatableFormats, catalog.TextFor);
        var target = registered.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
        if (target is null)
        {
            return null;
        }

        var available = ShellMenuBuilder.Build(selection, catalog.ReportFor, catalog.CreatableFormats);
        var match = MatchFor(available, target);
        if (match is null || !match.IsEnabled)
        {
            return null;
        }

        var request = ShellMenuBuilder.RequestFor(match, selection);
        return request is null ? null : ShellLauncher.PlanFor(request);
    }

    /// <summary>
    /// Which menu entry, if any, is the same line as a registered entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two sides do not carry format the same way, and that asymmetry is the whole subtlety
    /// here.</b> A registered entry names a format only when the format is part of its identity -
    /// <c>noba.compress.zip</c> and <c>noba.compress.7z</c> are different registry entries because
    /// they write different things. The archive actions have ONE registered entry each, with
    /// <c>Format: null</c>, because <i>Open with NOBA Archive</i> is one line whatever the archive
    /// turns out to be. A menu entry, by contrast, always reports the format it resolved to.
    /// </para>
    /// <para>
    /// So format is matched <b>only when the registration names one</b>. Requiring equality both ways
    /// hid every archive action: the menu said <c>OpenInApp/7z</c>, the registration said
    /// <c>OpenInApp/null</c>, nothing matched, and all four archive lines came back
    /// <c>hidden</c> on a selection that plainly was an archive. Caught by
    /// <c>An_archive_selection_offers_the_archive_actions</c> before any C++ existed, which is the
    /// entire reason the rules live on this side of the boundary.
    /// </para>
    /// <para>
    /// Ignoring format altogether is equally wrong in the other direction: every compress line shares
    /// one action, so grey would follow whichever came first - defect D1 in a new place.
    /// </para>
    /// </remarks>
    private static ShellMenuEntry? MatchFor(
        IReadOnlyList<ShellMenuEntry> available,
        ShellRegistration.ShellRegisteredAction registered) =>
        available.FirstOrDefault(m =>
            m.Action == registered.Action &&
            (registered.Format is null || SameFormat(m.Format, registered.Format)));

    private static bool SameFormat(ArchiveFormat? left, ArchiveFormat? right) =>
        (left, right) switch
        {
            (null, null) => true,
            ({ } l, { } r) => string.Equals(l.Id, r.Id, StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>
    /// The wire name for a state. Lower-case and fixed: the C++ compares these literally, and a
    /// culture-sensitive or renamed value would be a menu that silently stops appearing.
    /// </summary>
    internal static string Name(ShellEntryState state) => state switch
    {
        ShellEntryState.Enabled => "enabled",
        ShellEntryState.Disabled => "disabled",
        _ => "hidden",
    };
}
