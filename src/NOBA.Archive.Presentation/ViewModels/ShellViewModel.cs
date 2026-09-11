using System.Globalization;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.ViewModels;

public enum ShellSection
{
    Browse = 0,
    Create,
    Operations,
    Settings,
}

/// <summary>
/// The window: language, theme, navigation, and the metrics that follow from the script.
/// </summary>
/// <remarks>
/// Language changes here and nowhere else, and everything script-dependent is derived rather than set:
/// flow direction, font stack, line heights and row height are all functions of
/// <see cref="Language"/>. That is what makes Arabic a first-class mode rather than a translation —
/// there is no code path in which the UI is in Arabic but laid out for Latin.
/// </remarks>
public sealed class ShellViewModel : ObservableObject
{
    private UiLanguage _language;
    private ThemeMode _theme = ThemeMode.Dark;
    private ShellSection _section = ShellSection.Browse;
    private bool _isSidebarExpanded = true;

    public ShellViewModel(IArchiveWorkspace workspace, UiLanguage? language = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _language = language ?? UiLanguage.ForCulture(CultureInfo.CurrentUICulture);
        Browse = new BrowseViewModel(workspace) { Culture = _language.Culture };
        // Operations belong to the shell, not to a screen: an extraction started from Browse outlives
        // the moment the button was pressed, and navigating away has not cancelled anything.
        Operations = new OperationsViewModel(workspace);
        Browse.UseOperations(Operations);

        // Built after Operations so a creation lands in the same history as an extraction: one queue,
        // one list, whichever screen started the work.
        Create = new CreateViewModel(workspace, Operations);
    }

    public BrowseViewModel Browse { get; }

    public CreateViewModel Create { get; }

    public OperationsViewModel Operations { get; }

    // Static, and bound as such: the language list is a property of the product, not of a window.
    public static IReadOnlyList<UiLanguage> Languages => UiLanguage.Supported;

    public UiLanguage Language
    {
        get => _language;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Set(ref _language, value))
            {
                return;
            }

            Browse.Culture = value.Culture;

            Raise(nameof(Text));
            Raise(nameof(Flow));
            Raise(nameof(IsRightToLeft));
            Raise(nameof(Script));
            Raise(nameof(UiFontFamily));
            Raise(nameof(FileListRowHeight));
        }
    }

    public ThemeMode Theme
    {
        get => _theme;
        set
        {
            if (Set(ref _theme, value))
            {
                Raise(nameof(Colors));
            }
        }
    }

    public ShellSection Section
    {
        get => _section;
        set => Set(ref _section, value);
    }

    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set
        {
            if (Set(ref _isSidebarExpanded, value))
            {
                Raise(nameof(SidebarWidth));
            }
        }
    }

    public double SidebarWidth =>
        _isSidebarExpanded ? DesignTokens.SidebarWidth : DesignTokens.SidebarCollapsedWidth;

    /// <summary>The UI's text in the current language. Rebuilt, not mutated, when the language changes.</summary>
    public LocalizedStrings Text => LocalizedStrings.For(_language);

    public UiScript Script => _language.Script;

    public FlowDirection Flow => _language.Flow;

    public bool IsRightToLeft => _language.IsRightToLeft;

    public string UiFontFamily => DesignTokens.UiFontFor(_language.Script);

    public double FileListRowHeight => DesignTokens.FileListRowHeight(_language.Script);

    public IReadOnlyDictionary<string, TokenColor> Colors => DesignTokens.Colors(_theme);

    public void ToggleTheme() => Theme = _theme == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;

    public void ToggleLanguage() =>
        Language = _language == UiLanguage.Arabic ? UiLanguage.English : UiLanguage.Arabic;
}
