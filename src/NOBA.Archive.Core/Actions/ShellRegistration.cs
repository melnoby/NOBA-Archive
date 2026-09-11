using System.Globalization;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Core.Actions;

/// <summary>
/// What a registered entry may appear on.
/// </summary>
/// <remarks>
/// <para>
/// Expressed as what the product means — archives, files, folders — and not as registry key paths,
/// because the decision is a capability decision and the paths are one platform's spelling of it. The
/// installer translates; it does not decide (D5). A second mechanism (ADR-0006's
/// <c>IExplorerCommand</c>) will translate the same three words differently and must reach the same
/// answer.
/// </para>
/// <para>
/// <see cref="Folders"/> is separate from <see cref="Files"/> for one reason: a folder is
/// <see cref="ArchiveMemberCount.Many"/> members, so a format that cannot hold more than one may not
/// be offered on it at all. That is the same rule <see cref="FormatCapabilityReport.PermitsMemberCount"/>
/// applies everywhere else, asked at registration time because registration is when the shell decides
/// what to draw.
/// </para>
/// </remarks>
[Flags]
public enum ShellScope
{
    None = 0,

    /// <summary>Files this build can open as archives, and only those.</summary>
    Archives = 1,

    /// <summary>Any single file, archive or not.</summary>
    Files = 2,

    /// <summary>A folder.</summary>
    Folders = 4,
}

/// <summary>Which executable an action is carried out by.</summary>
public enum ShellHost
{
    /// <summary>The desktop application, because the action needs a window.</summary>
    Application = 0,

    /// <summary>The CLI, because the action is work with no question to ask.</summary>
    CommandLine,
}

/// <summary>
/// One verb, described once.
/// </summary>
/// <param name="Id">Stable identifier. It appears in the registry, so it must never change casually.</param>
/// <param name="Host">Which executable performs it.</param>
/// <param name="Verb">The CLI verb, or the application switch, that carries it.</param>
/// <param name="TextKey">The localisation key for the menu label. The menu is never English in a registry string.</param>
/// <param name="InterimLabel">
/// The English label the classic registry handler needs, and the only reason it exists.
/// A registry <c>MUIVerb</c> holds one string; ADR-0006's <c>IExplorerCommand</c> handler will supply
/// labels at runtime from <c>LocalizedStrings</c> in whichever language the user is in, and this field
/// dies with the stopgap. <c>SHELL-031</c> asserts it matches the real English text, so the two cannot
/// drift while both exist.
/// </param>
/// <param name="ArchiveOnly">
/// True when the verb needs an archive, so it must not appear on a folder selection. Recorded here
/// rather than re-derived by the installer, which has no capability model to consult.
/// </param>
public sealed record ShellVerb(
    ArchiveAction Action,
    string Id,
    ShellHost Host,
    string Verb,
    string TextKey,
    string InterimLabel,
    bool ArchiveOnly);

/// <summary>
/// Everything Windows needs to be told about NOBA Archive, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Centralised because uninstall is the hard half.</b> Registry strings scattered across an
/// installer script, a shell handler and a repair routine drift, and what drifts is not the install —
/// it is the removal. The failure is silent and permanent: a machine left with a NOBA entry pointing
/// at a deleted executable, which nothing now knows how to clean up because the string that created
/// it lived somewhere the uninstaller never read.
/// </para>
/// <para>
/// So there is one list, both directions read it, and <c>SHELL-023</c> asserts that every key install
/// creates is a key uninstall removes. The registration <em>mechanism</em> — sparse MSIX and an
/// <c>IExplorerCommand</c> server per ADR-0006 — is not here and is not implemented in this drop; what
/// is here is the description that mechanism will consume, so building it later is wiring rather than
/// re-deciding.
/// </para>
/// </remarks>
public static class ShellRegistration
{
    /// <summary>The publisher key everything NOBA writes lives under.</summary>
    public const string RootKey = @"HKLM\SOFTWARE\NOBA\Archive";

    /// <summary>Where the installer records the install directory, per ADR-0006.</summary>
    public const string InstallPathValue = "InstallPath";

    /// <summary>The submenu's own identity in the shell.</summary>
    public const string MenuId = "NOBA.Archive.ContextMenu";

    /// <summary>The submenu title for the interim classic handler. See <see cref="ShellVerb.InterimLabel"/>.</summary>
    public const string InterimMenuTitle = "NOBA Archive";

    /// <summary>
    /// The CLI verb that resolves a registered entry against a selection.
    /// </summary>
    /// <remarks>
    /// The classic registry can substitute the selection and nothing else — it cannot derive a
    /// containing folder or a suggested archive name. Rather than register a command that would be
    /// wrong (D2, D3), the registry invokes this verb with the entry id and the selection, and the
    /// same <c>ShellMenuBuilder</c>/<c>ShellLauncher</c> the tests exercise does the resolving.
    /// </remarks>
    public const string ResolverVerb = "shell";

    /// <summary>The application executable, as installed.</summary>
    public const string ApplicationExecutable = "NOBA.Archive.App.exe";

    /// <summary>The CLI executable, as installed (ADR-0009).</summary>
    public const string CommandLineExecutable = "noba-ar.exe";

    // ---------------------------------------------------------------------------------------------
    // MODERN SURFACE (Phase 5). Identity for the packaged IExplorerCommand handler.
    //
    // SHELL-065: identity has ONE home. The sparse manifest, the C++ adapter's Guids.h and the
    // registration script are all GENERATED from these constants, never hand-written alongside them.
    // A CLSID typed twice is a handler that registers and never activates, and the failure is a menu
    // that silently does not appear - which is the hardest kind of defect to find on somebody else's
    // machine.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The COM class the modern context-menu handler is registered under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the Phase 5 E2 probe's CLSID and must never become it.</b> The probe
    /// (<c>17019B66-...</c>) is a harness object that does nothing on invoke; it proved the packaged
    /// <c>com:SurrogateServer</c> activation path and is expected to be deleted. Sharing a CLSID with
    /// it would mean a machine that once ran the harness could activate the harness stub in place of
    /// the product handler.
    /// </para>
    /// <para>
    /// Braced form, because that is what the manifest and the registry both want; the C++ header is
    /// generated from it.
    /// </para>
    /// </remarks>
    public const string ModernHandlerClsid = "{0C5B9F1E-9B4A-4F2D-B3E6-2A7D5C8E1F40}";

    /// <summary>The package identity name for the sparse package that registers the handler.</summary>
    public const string PackageIdentityName = "NOBA.Archive";

    /// <summary>
    /// The verb id the modern submenu root is registered under.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MenuId"/>, which names the classic submenu. The two surfaces coexist
    /// while surface B lives, and giving them one id would make an uninstall of either remove both.
    /// </remarks>
    public const string ModernRootVerbId = "NobaArchive";

    /// <summary>
    /// Every verb the shell may invoke.
    /// </summary>
    /// <remarks>
    /// The split between hosts is a product decision, not a technical one. Testing and extracting to a
    /// known folder are work with nothing to ask, so they run headless and never steal focus. Opening,
    /// choosing a destination and building an archive by hand all need a person, so they raise the
    /// window — and raise the one that already exists where the platform allows it.
    /// </remarks>
    public static IReadOnlyList<ShellVerb> Verbs { get; } =
    [
        new(ArchiveAction.OpenInApp,     "noba.open",        ShellHost.Application, "--open",    "shell.open",          "Open with NOBA Archive", ArchiveOnly: true),
        new(ArchiveAction.TestArchive,   "noba.test",        ShellHost.CommandLine, "test",      "shell.test",          "Test archive",           ArchiveOnly: true),
        new(ArchiveAction.ExtractHere,   "noba.extracthere", ShellHost.CommandLine, "extract",   "shell.extractHere",   "Extract here",           ArchiveOnly: true),
        new(ArchiveAction.ExtractTo,     "noba.extractto",   ShellHost.Application, "--extract", "shell.extractTo",     "Extract to\u2026",        ArchiveOnly: true),
        new(ArchiveAction.CompressTo,    "noba.compress",    ShellHost.CommandLine, "create",    "shell.compressTo",    "Compress to {0}",        ArchiveOnly: false),
        new(ArchiveAction.CreateArchive, "noba.create",      ShellHost.Application, "--create",  "shell.createArchive", "Create archive\u2026",    ArchiveOnly: false),
    ];

    /// <summary>
    /// The interim English text for a key, for callers that cannot reach the string table.
    /// </summary>
    /// <remarks>
    /// The CLI generates the manifest and may not reference the presentation layer — the dependency
    /// test forbids it, correctly. So the interim labels travel with the verbs, and <c>SHELL-031</c>
    /// asserts they equal <c>LocalizedStrings</c> so the two copies cannot drift while both exist.
    /// Both die together when ADR-0006's handler supplies labels at runtime.
    /// </remarks>
    public static string InterimTextFor(string textKey)
    {
        foreach (var verb in Verbs)
        {
            if (string.Equals(verb.TextKey, textKey, StringComparison.Ordinal))
            {
                return verb.InterimLabel;
            }
        }

        return textKey;
    }

    public static ShellVerb? VerbFor(ArchiveAction action)
    {
        foreach (var verb in Verbs)
        {
            if (verb.Action == action)
            {
                return verb;
            }
        }

        return null;
    }

    /// <summary>
    /// One line the shell will actually show, with its format already chosen and its label finished.
    /// </summary>
    /// <param name="Id">
    /// Stable and unique. A per-format action carries the format in its id — <c>noba.compress.7z</c> —
    /// because the registry needs one key per visible entry and those keys are what uninstall names.
    /// </param>
    /// <param name="Label">
    /// Resolved, never a template. A registry <c>MUIVerb</c> is shown verbatim, so a <c>{0}</c>
    /// reaching this field reaches the user (D1).
    /// </param>
    /// <param name="Template">The command shape, from <see cref="ShellInvocations"/> and nowhere else.</param>
    /// <param name="Scope">
    /// What the entry may appear on, decided by the same capability model the menu uses. The installer
    /// spells these as registry keys; it does not choose them.
    /// </param>
    public sealed record ShellRegisteredAction(
        string Id,
        ArchiveAction Action,
        ArchiveFormat? Format,
        ShellHost Host,
        string Label,
        bool ArchiveOnly,
        ShellInvocationTemplate Template,
        ShellScope Scope);

    /// <summary>
    /// Every entry the shell should show, expanded per format and resolved per language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where the format dimension lives</b>, and losing it was D1. The action model emits
    /// one <c>CompressTo</c> per creatable format; the registration must too, or the single verb it
    /// does emit carries a label with a hole in it and a command with no format in it.
    /// </para>
    /// <para>
    /// <paramref name="textFor"/> is supplied rather than resolved here because <c>Core</c> has no
    /// dependencies and the string table lives in the presentation layer. The caller passes its
    /// localiser; this decides what to ask it for and what to do with the answer.
    /// </para>
    /// <para>
    /// Capability is consulted, not assumed (D5). A format that cannot be created yields no entry,
    /// whatever the caller offered.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ShellRegisteredAction> Entries(
        Func<ArchiveFormat, FormatCapabilityReport> reportFor,
        IReadOnlyList<ArchiveFormat> creatableFormats,
        Func<string, string> textFor)
    {
        ArgumentNullException.ThrowIfNull(reportFor);
        ArgumentNullException.ThrowIfNull(creatableFormats);
        ArgumentNullException.ThrowIfNull(textFor);

        var entries = new List<ShellRegisteredAction>();

        foreach (var verb in Verbs)
        {
            if (ShellInvocations.For(verb.Action) is not { } template)
            {
                continue;
            }

            if (verb.Action != ArchiveAction.CompressTo)
            {
                entries.Add(new ShellRegisteredAction(
                    verb.Id, verb.Action, Format: null, verb.Host,
                    textFor(verb.TextKey), verb.ArchiveOnly, template,
                    verb.ArchiveOnly ? ShellScope.Archives : ShellScope.Files | ShellScope.Folders));
                continue;
            }

            // One entry per writable format, each naming its own.
            foreach (var format in creatableFormats)
            {
                var report = reportFor(format);
                if (!report.CanCreate)
                {
                    continue;
                }

                // A folder is Many members; a single-member format may not be offered on one. Same
                // rule, same method, asked at registration time (D5/D7).
                var scope = ShellScope.Files;
                if (report.PermitsMemberCount(ArchiveMemberCount.Many))
                {
                    scope |= ShellScope.Folders;
                }

                entries.Add(new ShellRegisteredAction(
                    $"{verb.Id}.{format.Id}",
                    verb.Action,
                    format,
                    verb.Host,
                    string.Format(CultureInfo.InvariantCulture, textFor(verb.TextKey), format.DisplayName),
                    verb.ArchiveOnly,
                    template,
                    scope));
            }
        }

        return entries;
    }

    /// <summary>
    /// Every registry key installation creates, in creation order.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Verbs"/> rather than written out, so a verb cannot be added without its
    /// key appearing here — and therefore in the uninstall list too.
    /// </remarks>
    public static IReadOnlyList<string> KeysCreatedOnInstall =>
    [
        RootKey,
        $@"{RootKey}\Shell",
        .. Verbs.Select(v => $@"{RootKey}\Shell\{v.Id}"),
    ];

    /// <summary>
    /// Every key installation creates for a concrete build, expanded per format.
    /// </summary>
    /// <remarks>
    /// <see cref="KeysCreatedOnInstall"/> is the verb-level shape and stays for the metadata tests.
    /// This is what an installer actually writes, and it grows a key per creatable format — so a
    /// build with four writable formats registers four Compress entries and removes four.
    /// </remarks>
    public static IReadOnlyList<string> KeysForEntries(IReadOnlyList<ShellRegisteredAction> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return
        [
            RootKey,
            $@"{RootKey}\Shell",
            .. entries.Select(e => $@"{RootKey}\Shell\{e.Id}"),
        ];
    }

    /// <summary>
    /// Every registry key uninstallation removes.
    /// </summary>
    /// <remarks>
    /// Deepest first: a key with children cannot be deleted, and an uninstaller that fails halfway
    /// through leaves exactly the orphaned entry this list exists to prevent.
    /// </remarks>
    public static IReadOnlyList<string> KeysRemovedOnUninstall =>
        [.. KeysCreatedOnInstall.Reverse()];
}
