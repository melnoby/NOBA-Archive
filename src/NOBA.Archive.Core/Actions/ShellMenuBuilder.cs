using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Core.Actions;

/// <summary>Why an offered action is not available right now.</summary>
public enum ShellBlocker
{
    None = 0,

    /// <summary>The format holds one member and more than one thing is selected.</summary>
    SingleMemberFormat,
}

/// <summary>
/// One line of the NOBA submenu.
/// </summary>
/// <param name="Format">The format this line writes, for <see cref="ArchiveAction.CompressTo"/>. Null otherwise.</param>
/// <param name="IsEnabled">
/// False means shown and greyed with <paramref name="Blocker"/> as the reason. An action that cannot
/// be performed and cannot be explained is simply absent instead — a menu is not the place to teach
/// somebody that the product is arbitrary.
/// </param>
public sealed record ShellMenuEntry(
    ArchiveAction Action,
    ArchiveFormat? Format,
    bool IsEnabled,
    ShellBlocker Blocker)
{
    public static ShellMenuEntry Enabled(ArchiveAction action, ArchiveFormat? format = null) =>
        new(action, format, IsEnabled: true, ShellBlocker.None);

    public static ShellMenuEntry Disabled(ArchiveAction action, ArchiveFormat? format, ShellBlocker blocker) =>
        new(action, format, IsEnabled: false, blocker);
}

/// <summary>
/// Turns an Explorer selection into the NOBA submenu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every line comes from <see cref="FormatCapabilityReport"/>.</b> There is no second matrix, no
/// list of format names, and no <c>if (extension == ".rar")</c>. RAR loses "Compress to RAR" because
/// its provider does not report <see cref="ArchiveCapabilities.Create"/> — the same fact that makes
/// the Create screen omit it and the CLI refuse it. BZIP2 and XZ are absent because no provider
/// claims them, so their report is unavailable. Adding a provider changes this menu without changing
/// this file, which is the entire point of asking rather than knowing.
/// </para>
/// <para>
/// <b>Nothing here touches the disk.</b> No archive is opened, no folder walked, no size computed.
/// The only file-derived fact used is the extension, which the caller already holds, and it is used
/// to pick a format — never to decide a capability. This runs inside a right-click; work that takes
/// time starts after the person has chosen a line, not before they have seen one.
/// </para>
/// </remarks>
public static class ShellMenuBuilder
{
    /// <summary>
    /// The submenu for a selection.
    /// </summary>
    /// <param name="selection">What Explorer had selected.</param>
    /// <param name="reportFor">
    /// The capability report for a format — the engine's registry in production, a fixture in tests.
    /// Passed in rather than resolved here so that <c>Core</c> keeps its "no dependencies" property.
    /// </param>
    /// <param name="creatableFormats">
    /// Formats to consider offering, in menu order. The caller supplies the build's own list; this
    /// filters it by capability rather than trusting it.
    /// </param>
    public static IReadOnlyList<ShellMenuEntry> Build(
        ShellSelection selection,
        Func<ArchiveFormat, FormatCapabilityReport> reportFor,
        IReadOnlyList<ArchiveFormat> creatableFormats)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(reportFor);
        ArgumentNullException.ThrowIfNull(creatableFormats);

        var entries = new List<ShellMenuEntry>();
        if (selection.IsEmpty)
        {
            return entries;
        }

        // Archive actions first: with an archive selected, opening it is what the person came for.
        if (selection.SingleArchive is { Format: { } format } archive)
        {
            var report = reportFor(format);

            if (report.CanOpen)
            {
                entries.Add(ShellMenuEntry.Enabled(ArchiveAction.OpenInApp, format));
            }

            if (report.CanTest)
            {
                entries.Add(ShellMenuEntry.Enabled(ArchiveAction.TestArchive, format));
            }

            if (report.CanExtract)
            {
                entries.Add(ShellMenuEntry.Enabled(ArchiveAction.ExtractHere, format));
                entries.Add(ShellMenuEntry.Enabled(ArchiveAction.ExtractTo, format));
            }

            _ = archive;
        }

        // Then compression, which applies to anything at all - archives included, since re-packing a
        // zip inside a 7z is a legitimate thing to want.
        foreach (var candidate in creatableFormats)
        {
            var report = reportFor(candidate);
            if (!report.CanCreate)
            {
                continue;
            }

            // Members, not clicks (D7). A single folder is one selection and many members, so a
            // single-member format is withheld from it rather than offered and then refused.
            entries.Add(report.PermitsMemberCount(selection.MemberCount)
                ? ShellMenuEntry.Enabled(ArchiveAction.CompressTo, candidate)
                : ShellMenuEntry.Disabled(ArchiveAction.CompressTo, candidate, ShellBlocker.SingleMemberFormat));
        }

        // "Create archive…" is worth offering whenever anything at all can be written; it is the way
        // to reach the options the quick actions deliberately do not ask about.
        if (creatableFormats.Any(f => reportFor(f).CanCreate))
        {
            entries.Add(ShellMenuEntry.Enabled(ArchiveAction.CreateArchive));
        }

        return entries;
    }

    /// <summary>
    /// Turns a chosen menu line into the request the engine will be given.
    /// </summary>
    /// <remarks>
    /// The destination is decided here for the two actions that fix it, and left null for the two that
    /// ask. "Extract here" means the folder the archive is in — not the working directory, which for a
    /// shell-launched process is somewhere the user never chose.
    /// </remarks>
    public static ArchiveActionRequest? RequestFor(ShellMenuEntry entry, ShellSelection selection)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(selection);

        if (!entry.IsEnabled || selection.IsEmpty)
        {
            return null;
        }

        return entry.Action switch
        {
            // The items travel, not just their paths. Handing over what the selection already holds
            // is what stops a later caller re-deriving it and getting it wrong (SHELL-075).
            ArchiveAction.OpenInApp or ArchiveAction.TestArchive or ArchiveAction.ExtractTo =>
                selection.SingleArchive is { } one
                    ? new ArchiveActionRequest
                    {
                        Action = entry.Action,
                        Inputs = [one],
                        Format = one.Format,
                    }
                    : null,

            ArchiveAction.ExtractHere =>
                selection.SingleArchive is { } archive
                    ? new ArchiveActionRequest
                    {
                        Action = ArchiveAction.ExtractHere,
                        Inputs = [archive],
                        Destination = ContainingFolder(archive.Path),
                        Format = archive.Format,
                    }
                    : null,

            ArchiveAction.CompressTo =>
                entry.Format is { } target
                    ? new ArchiveActionRequest
                    {
                        Action = ArchiveAction.CompressTo,
                        Inputs = selection.Items,
                        Destination = ContainingFolder(selection.Items[0].Path),
                        Format = target,
                    }
                    : null,

            ArchiveAction.CreateArchive => new ArchiveActionRequest
            {
                Action = ArchiveAction.CreateArchive,
                Inputs = selection.Items,
            },

            _ => null,
        };
    }

    /// <summary>
    /// The folder a path sits in.
    /// </summary>
    /// <remarks>
    /// String work, not a disk query, and it tolerates a trailing separator on a folder — which is how
    /// Explorer hands some selections over, and which <see cref="Path.GetDirectoryName(string?)"/>
    /// would otherwise read as "the folder itself".
    /// </remarks>
    public static string? ContainingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            return null;
        }

        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        if (cut <= 0)
        {
            return null;
        }

        var folder = trimmed[..cut];
        return folder.EndsWith(':') ? folder + '\\' : folder;
    }
}
