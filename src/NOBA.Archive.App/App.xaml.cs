using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using NOBA.Archive.App.Services;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Actions;
using NOBA.Archive.Engine;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.Presentation.Interaction;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.App;

/// <summary>
/// The application host.
/// </summary>
/// <remarks>
/// <para>
/// The engine is composed exactly as the CLI composes it — one <c>AddNobaArchiveEngine</c> call — so the
/// desktop app and <c>noba-ar.exe</c> are two surfaces over one engine rather than two implementations
/// that happen to agree. The parity suite exists to keep that true.
/// </para>
/// <para>
/// The base type is written out in full deliberately. This file's namespace is
/// <c>NOBA.Archive.App</c>, so C# resolves an unqualified <c>Application</c> by walking the enclosing
/// namespaces before it ever looks at a <c>using</c> — and the first thing it finds is the sibling
/// namespace <c>NOBA.Archive.Application</c>, our own application layer. The result is CS0118,
/// "'Application' is a namespace but is used like a type", and behind it a cascade of MSB3073 from the
/// XAML compiler. A <c>using</c> alias at the top of the file does not fix it either: aliases declared
/// at compilation-unit level are consulted at the global step, after <c>NOBA.Archive</c> has already
/// won. Full qualification is the one form that does not depend on that ordering.
/// </para>
/// </remarks>
public partial class App : Microsoft.UI.Xaml.Application
{
    private ServiceProvider? _services;
    private MainWindow? _window;

    public App() => InitializeComponent();

    /// <summary>
    /// The main window's HWND.
    /// </summary>
    /// <remarks>
    /// An unpackaged WinUI app has to hand its owning window to every picker; without it the picker
    /// throws at the moment the user clicks Open, which is the least helpful moment available.
    /// </remarks>
    public nint MainWindowHandle => _window is null
        ? nint.Zero
        : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    /// <summary>
    /// The host that shows the extract dialog.
    /// </summary>
    /// <remarks>
    /// Exposed rather than injected because the caller is a XAML page, which the container does not
    /// construct. The type behind it is an interface declared in the presentation layer, so the page
    /// still names no dialog implementation — and <c>ARC-055</c> still holds, because nothing about a
    /// format is decided on either side of it.
    /// </remarks>
    public IExtractDialog ExtractDialog => _dialogs ?? throw new InvalidOperationException(
        "The dialog host does not exist until the application has launched.");

    /// <summary>The host that asks for confirmation before a destructive, un-undoable action.</summary>
    public IConfirmDialog ConfirmDialog => _dialogs ?? throw new InvalidOperationException(
        "The dialog host does not exist until the application has launched.");

    /// <summary>The host that asks for a new name for one entry.</summary>
    public IRenameDialog RenameDialog => _dialogs ?? throw new InvalidOperationException(
        "The dialog host does not exist until the application has launched.");

    private WindowDialogHost? _dialogs;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Resolved lazily: the container is built before the window exists, and the dialogs are only
        // ever shown after it does.
        var dialogs = new WindowDialogHost(() => _window);
        _dialogs = dialogs;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNobaArchiveEngine(options =>
        {
            // A desktop app has a user, so it may prompt. Conflicts and passwords are asked, not assumed.
            options.Headless = false;
        });

        // The engine registers a password provider and a conflict prompt only for a headless host, and
        // that is a refusal rather than an omission: no library can invent a way to ask a person a
        // question. Supplying them is this host's half of the contract, and the half we once did not
        // supply — the container threw here, the exception was unhandled, and the process exited before
        // drawing a pixel. DHC-001…004 now fail on Linux rather than the window failing on Windows.
        services.AddSingleton<IPasswordProvider>(new DialogPasswordProvider(dialogs, CurrentUiContext));
        services.AddSingleton<IConflictPrompt>(new DialogConflictPrompt(dialogs, CurrentUiContext));

        services.AddSingleton<IArchiveWorkspace, EngineWorkspace>();

        _services = services.BuildServiceProvider();
        _window = new MainWindow(_services.GetRequiredService<IArchiveWorkspace>());

        ApplyShellRequest(_window);

        _window.Activate();
    }

    /// <summary>
    /// Loads a request that came from the Explorer menu, if this launch carried one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied before <c>Activate</c> so the window draws once, already showing the right screen. A
    /// window that appears on Browse and then jumps to Create is the user watching the application
    /// change its mind.
    /// </para>
    /// <para>
    /// The first element of <see cref="Environment.GetCommandLineArgs"/> is the executable path, so it
    /// is skipped. Everything after it is handed to <see cref="ShellCommandLine"/>, which recognises
    /// only this application's own verbs — an ordinary double-click parses to null and gets the
    /// ordinary window.
    /// </para>
    /// <para>
    /// <b>This is not single-instance.</b> A second right-click starts a second process, and this
    /// build has no lifecycle to redirect it into the first (see SHELL-L3). The seam is here: when
    /// activation redirection is added, it hands the same argument list to the same parser and the
    /// rest of this path does not change.
    /// </para>
    /// </remarks>
    private static void ApplyShellRequest(MainWindow window)
    {
        string[] raw;
        try
        {
            raw = Environment.GetCommandLineArgs();
        }
        catch (NotSupportedException)
        {
            return;
        }

        if (raw.Length < 2)
        {
            return;
        }

        var request = ShellCommandLine.Parse(raw[1..]);
        if (request is null)
        {
            return;
        }

        var outcome = ShellRequestSeed.Apply(window.Shell, request);
        if (outcome != SeedOutcome.Ignored)
        {
            window.RenderShellRequest(outcome, request);
        }
    }

    /// <summary>
    /// What the shell looks like right now, read at the moment a dialog is raised.
    /// </summary>
    /// <remarks>
    /// Read late, never captured: an operation started in English can raise its first conflict prompt
    /// after the person has switched the window to Arabic, and the prompt belongs to the window they are
    /// looking at. Before the window exists the system culture decides, which is the same rule the shell
    /// itself starts from.
    /// </remarks>
    private UiContext CurrentUiContext()
    {
        if (_window?.Shell is { } shell)
        {
            return new UiContext(shell.Text, shell.IsRightToLeft, shell.UiFontFamily);
        }

        var language = UiLanguage.ForCulture(CultureInfo.CurrentUICulture);
        return new UiContext(
            LocalizedStrings.For(language),
            language.IsRightToLeft,
            DesignTokens.UiFontFor(language.Script));
    }
}
