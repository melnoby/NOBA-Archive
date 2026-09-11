using System.Text;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.Presentation.Interaction;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The two host dialogs, as logic: <c>HDL-001…008</c>.
/// </summary>
/// <remarks>
/// The XAML is a text box and three radio buttons. Everything that could be wrong about these prompts —
/// what a cancelled dialog means, whether a path can be rendered in an order it does not have, whether
/// a secret survives in memory after use — lives in the adapters, and is therefore testable here rather
/// than only by looking at a screenshot.
/// </remarks>
public sealed class HostDialogTests
{
    private static readonly UiContext Arabic =
        new(LocalizedStrings.For(UiLanguage.Arabic), true, DesignTokens.UiFontFor(UiScript.Arabic));

    private static readonly UiContext English =
        new(LocalizedStrings.For(UiLanguage.English), false, DesignTokens.UiFontFor(UiScript.Latin));

    /// <summary>HDL-001: a cancelled password dialog yields no password.</summary>
    [Fact]
    public async Task A_cancelled_password_dialog_returns_null()
    {
        var provider = new DialogPasswordProvider(new FakePasswordDialog(null), () => English);

        Assert.Null(await provider.GetPasswordAsync(new PasswordRequest("a.7z", null, false), default));
    }

    /// <summary>HDL-002: an empty box is not a password.</summary>
    /// <remarks>
    /// Handing a backend a zero-length key produces a decode failure that reads like a corrupt archive.
    /// "No password available" is both true and the answer the contract has a word for.
    /// </remarks>
    [Fact]
    public async Task An_empty_box_is_not_a_password()
    {
        var provider = new DialogPasswordProvider(new FakePasswordDialog([]), () => English);

        Assert.Null(await provider.GetPasswordAsync(new PasswordRequest("a.7z", null, false), default));
    }

    /// <summary>HDL-003: what was typed becomes the password, and the buffer is zeroed behind it.</summary>
    /// <remarks>
    /// The array the dialog hands over is the only mutable copy of the secret we control. If it were
    /// left populated it would sit in the heap until a collection that may never come.
    /// </remarks>
    [Fact]
    public async Task The_typed_password_is_carried_over_and_the_buffer_is_zeroed()
    {
        var typed = "سرّ-Secret-٢٠٢٦".ToCharArray();
        var dialog = new FakePasswordDialog(typed);
        var provider = new DialogPasswordProvider(dialog, () => Arabic);

        using var password = await provider.GetPasswordAsync(new PasswordRequest("a.7z", null, false), default);

        Assert.NotNull(password);
        Assert.Equal(
            Encoding.UTF8.GetBytes("سرّ-Secret-٢٠٢٦"),
            password!.Use(static bytes => bytes.ToArray()));

        Assert.True(Array.TrueForAll(typed, static c => c == '\0'), "the dialog's buffer was left populated");
    }

    /// <summary>HDL-004: a retry says so, and a first attempt does not.</summary>
    [Fact]
    public async Task A_retry_is_stated_and_a_first_attempt_is_not()
    {
        var dialog = new FakePasswordDialog(null);
        var provider = new DialogPasswordProvider(dialog, () => English);

        await provider.GetPasswordAsync(new PasswordRequest("a.7z", null, false), default);
        Assert.Null(dialog.LastPrompt!.RetryWarning);

        await provider.GetPasswordAsync(new PasswordRequest("a.7z", null, true), default);
        Assert.Equal(English.Text.PasswordRetry, dialog.LastPrompt!.RetryWarning);
    }

    /// <summary>
    /// HDL-005: the archive name and entry path reach the dialog pinned left to right.
    /// </summary>
    /// <remarks>
    /// This is the same control as <c>BIDI-003</c>, applied at the point it matters most. The person is
    /// about to type a secret into a window because of what this line says the archive is; a line whose
    /// segments the bidirectional algorithm has reordered is not that.
    /// </remarks>
    [Fact]
    public async Task The_archive_and_entry_lines_are_pinned_left_to_right()
    {
        var dialog = new FakePasswordDialog(null);
        var provider = new DialogPasswordProvider(dialog, () => Arabic);

        await provider.GetPasswordAsync(new PasswordRequest(@"مستندات\..\..\evil.7z", "مجلد/secret.txt", false), default);

        Assert.Equal(BidiText.IsolateAsLtr(@"مستندات\..\..\evil.7z"), dialog.LastPrompt!.ArchiveLine);
        Assert.Equal(BidiText.IsolateAsLtr("مجلد/secret.txt"), dialog.LastPrompt.EntryLine);
        Assert.Equal(Arabic.Text.PasswordEntryBody, dialog.LastPrompt.Body);
        Assert.True(dialog.LastPrompt.IsRightToLeft);
    }

    /// <summary>
    /// HDL-006: a dismissed conflict dialog cancels. It never skips.
    /// </summary>
    /// <remarks>
    /// The requirement in one assertion. Skip is the only conflict outcome a person cannot detect
    /// afterwards — the extraction reports success and the file is simply absent — so it is the one
    /// outcome that may never be inferred from silence.
    /// </remarks>
    [Fact]
    public async Task A_dismissed_conflict_dialog_cancels_rather_than_skipping()
    {
        var prompt = new DialogConflictPrompt(new FakeConflictDialog(null), () => English);

        var decision = await prompt.ResolveAsync(Conflict(), default);

        Assert.Equal(ConflictAction.Cancel, decision.Action);
        Assert.False(decision.ApplyToAll);
    }

    /// <summary>HDL-007: the person's choice, including "apply to all", is passed through untouched.</summary>
    [Fact]
    public async Task The_choice_and_its_latch_are_passed_through()
    {
        var chosen = new ConflictDecision(ConflictAction.Rename, true);
        var prompt = new DialogConflictPrompt(new FakeConflictDialog(chosen), () => Arabic);

        Assert.Equal(chosen, await prompt.ResolveAsync(Conflict(), default));
    }

    /// <summary>
    /// HDL-008: both dialogs speak both languages, and carry the direction and font of each.
    /// </summary>
    /// <remarks>
    /// Arabic is not a later localisation pass: there is no state in which a prompt has Arabic text and
    /// Latin metrics, because the text, the direction and the font stack are read from one context.
    /// </remarks>
    [Fact]
    public async Task Both_dialogs_speak_both_languages_with_matching_direction()
    {
        var conflictDialog = new FakeConflictDialog(null);

        foreach (var ui in new[] { Arabic, English })
        {
            var context = ui;
            await new DialogConflictPrompt(conflictDialog, () => context).ResolveAsync(Conflict(), default);

            var shown = conflictDialog.LastPrompt!;
            Assert.Equal(ui.Text.ConflictTitle, shown.Title);
            Assert.Equal(ui.Text.ConflictOverwrite, shown.OverwriteLabel);
            Assert.Equal(ui.IsRightToLeft, shown.IsRightToLeft);
            Assert.Equal(ui.FontFamily, shown.FontFamily);
            Assert.Equal(BidiText.IsolateAsLtr(@"C:\out\تقرير.pdf"), shown.Path);
        }

        Assert.NotEqual(Arabic.Text.ConflictTitle, English.Text.ConflictTitle);
    }

    private static ConflictRequest Conflict() =>
        new(@"C:\out\تقرير.pdf", ByteSize.FromKibibytes(4), ByteSize.FromKibibytes(9));

    private sealed class FakePasswordDialog(char[]? answer) : IPasswordDialog
    {
        public PasswordPrompt? LastPrompt { get; private set; }

        public ValueTask<char[]?> ShowAsync(PasswordPrompt prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return ValueTask.FromResult(answer);
        }
    }

    private sealed class FakeConflictDialog(ConflictDecision? answer) : IConflictDialog
    {
        public ConflictPrompt? LastPrompt { get; private set; }

        public ValueTask<ConflictDecision?> ShowAsync(ConflictPrompt prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return ValueTask.FromResult(answer);
        }
    }
}
