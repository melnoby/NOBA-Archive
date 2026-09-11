using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>Why an action is not on offer, in terms a screen can render without naming a format.</summary>
public enum FormatBlocker
{
    /// <summary>Nothing is stopping it.</summary>
    None = 0,

    /// <summary>The product knows this format, but nothing in this installation can handle it.</summary>
    ProviderUnavailable,

    /// <summary>A provider is present and this particular thing is not something the format can do.</summary>
    NotSupportedByFormat,
}

/// <summary>
/// One format, and everything a screen may say about it.
/// </summary>
/// <remarks>
/// The display name comes from the format itself and is not translated: 7Z, ZIP, RAR, TAR and GZIP are
/// names, not words, and they are written the same in every language. Everything around them is
/// localised.
/// </remarks>
public sealed record FormatDescription(
    ArchiveFormat Format,
    FormatCapabilityReport Report,
    string DisplayName,
    string Extensions)
{
    public bool IsAvailable => Report.IsAvailable;
}

/// <summary>
/// The product's format vocabulary, as the UI is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// Every screen that wants to know what this build can do asks here, and what comes back is already
/// decided. This is the piece that was missing: <c>ARC-055</c> has stopped the WinUI project from
/// deciding capabilities since DROP 02, and the view models have derived their lists from the registry
/// since then — but a disabled button with nothing beside it is only half of an honest answer. A person
/// whose 7Z file will not open is owed the sentence "the engine component this needs is not installed",
/// not an empty format list.
/// </para>
/// <para>
/// Nothing here knows a format by name. <see cref="Reason"/> distinguishes "this installation cannot"
/// from "this format cannot", and both come from the capability report.
/// </para>
/// </remarks>
public sealed class FormatCatalogViewModel
{
    private readonly IArchiveWorkspace _workspace;

    public FormatCatalogViewModel(IArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    /// <summary>Every format some provider claims, whether or not it works in this installation.</summary>
    public IReadOnlyList<FormatDescription> All =>
        [.. _workspace.AvailableFormats
            .Select(Describe)
            .OrderBy(static d => d.DisplayName, StringComparer.Ordinal)];

    /// <summary>The formats that can actually be used here.</summary>
    public IReadOnlyList<FormatDescription> Usable => [.. All.Where(static d => d.IsAvailable)];

    /// <summary>
    /// Known to the product, unusable in this installation.
    /// </summary>
    /// <remarks>
    /// Normally empty. It is non-empty exactly when a backend registered its formats and then could not
    /// load — which is the case worth explaining rather than hiding.
    /// </remarks>
    public IReadOnlyList<FormatDescription> Unavailable => [.. All.Where(static d => !d.IsAvailable)];

    public FormatDescription Describe(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return new FormatDescription(
            format,
            _workspace.ReportFor(format),
            format.DisplayName,
            string.Join(" ", format.Extensions));
    }

    /// <summary>
    /// Why one operation is unavailable for one format.
    /// </summary>
    /// <remarks>
    /// The order matters. "No provider" outranks "the format cannot" because it is the more useful
    /// answer: telling somebody that RAR cannot be created is right, but telling them that 7Z cannot be
    /// created on a machine that is merely missing a library would send them looking for the wrong
    /// problem.
    /// </remarks>
    public static FormatBlocker Reason(FormatCapabilityReport report, FormatOperation operation)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!report.IsAvailable)
        {
            return FormatBlocker.ProviderUnavailable;
        }

        return Permits(report, operation) ? FormatBlocker.None : FormatBlocker.NotSupportedByFormat;
    }

    public static bool Permits(FormatCapabilityReport report, FormatOperation operation)
    {
        ArgumentNullException.ThrowIfNull(report);

        return operation switch
        {
            FormatOperation.Open => report.CanOpen,
            FormatOperation.Browse => report.CanBrowse,
            FormatOperation.Test => report.CanTest,
            FormatOperation.Extract => report.CanExtract,
            FormatOperation.Create => report.CanCreate,
            FormatOperation.Update => report.CanUpdate,
            _ => false,
        };
    }

    /// <summary>One sentence saying why, or null when nothing is wrong.</summary>
    public static string? Explain(
        FormatCapabilityReport report,
        FormatOperation operation,
        LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(text);

        return Reason(report, operation) switch
        {
            FormatBlocker.ProviderUnavailable => string.Format(
                text.Language.Culture,
                text.FormatProviderUnavailable,
                report.Format.DisplayName),

            FormatBlocker.NotSupportedByFormat => string.Format(
                text.Language.Culture,
                OperationPhrase(operation, text),
                report.Format.DisplayName),

            _ => null,
        };
    }

    private static string OperationPhrase(FormatOperation operation, LocalizedStrings text) => operation switch
    {
        FormatOperation.Create => text.FormatCannotCreate,
        FormatOperation.Test => text.FormatCannotTest,
        FormatOperation.Extract => text.FormatCannotExtract,
        FormatOperation.Update => text.FormatCannotUpdate,
        _ => text.FormatCannotOpen,
    };
}

/// <summary>The things a screen offers to do with an archive.</summary>
public enum FormatOperation
{
    Open = 0,
    Browse,
    Test,
    Extract,
    Create,
    Update,
}
