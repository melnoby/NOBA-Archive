namespace NOBA.Archive.Core.Formats;

/// <summary>
/// The one place that decides which formats this build can write.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because the G.1 bridge reproduced a derivation instead of consuming one.</b>
/// <see cref="FormatCapabilityReport.CanCreate"/> was never duplicated - that single expression has
/// always had one home. What was duplicated is the step above it: turning the registry's supported
/// formats into the ordered list of writable ones. The same five lines appeared in
/// <c>CliApplication</c> twice and again in <c>harness/phase5-g1/bridge/BridgeExports.cs</c>:
/// </para>
/// <code>
/// registry.SupportedFormats
///     .Where(f =&gt; ReportFor(f).CanCreate)
///     .OrderBy(static f =&gt; f.Id, StringComparer.Ordinal)
///     .ToList();
/// </code>
/// <para>
/// Three copies of a rule is how <c>shell-register.ps1</c> drifted from <c>ShellLauncher</c> and
/// produced defects D2 and D3. The Phase 5 shell adapter would have been the fourth copy, in a
/// process nobody watches, and a drift there shows up as a wrong menu rather than as a failing test.
/// Recorded as a Phase 5 architecture requirement at the G.1 review: <i>there must be ONE
/// authoritative capability derivation, and the bridge must consume it rather than reproduce it.</i>
/// </para>
/// <para>
/// <b>The ordering is part of the derivation, not decoration.</b> All three copies ordered by
/// <c>Id</c> ordinally, and they had to: the menu, the registration entries and the CLI's format
/// list are compared against each other by <c>SHELL-064</c>, and a list that is merely a set cannot
/// be compared. Ordinal rather than culture-aware because a registry key ordered by the operator's
/// locale is a machine-dependent registration.
/// </para>
/// <para>
/// <b>Core keeps its no-dependencies property.</b> The registry lives in another layer, so the
/// supported formats and the report function are passed in - the same shape
/// <see cref="Actions.ShellMenuBuilder"/> already uses. Callers hold the registry; this holds the
/// rule.
/// </para>
/// <para>
/// <see cref="Actions.ShellMenuBuilder"/> filters by <see cref="FormatCapabilityReport.CanCreate"/>
/// again on the list it is handed, and that stays. It is not a second derivation - it is the builder
/// declining to trust a caller-supplied list, which is a different thing and a good habit.
/// </para>
/// </remarks>
public static class CreatableFormats
{
    /// <summary>
    /// The formats this build can write, in the order every consumer must use.
    /// </summary>
    /// <param name="supported">
    /// Every format the provider registry knows about, writable or not.
    /// </param>
    /// <param name="reportFor">
    /// The capability report for a format - the engine's registry in production, a fixture in tests.
    /// </param>
    /// <returns>
    /// The writable formats, ordered by <c>Id</c> ordinally. Never null; empty is a legitimate
    /// answer for a build with no writing provider, and callers must treat it as one rather than as
    /// a failure.
    /// </returns>
    public static IReadOnlyList<ArchiveFormat> From(
        IEnumerable<ArchiveFormat> supported,
        Func<ArchiveFormat, FormatCapabilityReport> reportFor)
    {
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(reportFor);

        return supported
            .Where(f => reportFor(f).CanCreate)
            .OrderBy(static f => f.Id, StringComparer.Ordinal)
            .ToList();
    }
}
