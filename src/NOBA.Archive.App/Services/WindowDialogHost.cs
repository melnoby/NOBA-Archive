using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.App.Dialogs;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Presentation.Interaction;

namespace NOBA.Archive.App.Services;

/// <summary>
/// Shows the host dialogs on the window that owns them.
/// </summary>
/// <remarks>
/// <para>
/// The three things this exists to get right, none of which belong in a view model:
/// </para>
/// <para>
/// <b>Thread.</b> The engine asks for a password from whichever worker thread is decoding. Touching a
/// <see cref="ContentDialog"/> from there is an immediate <c>RPC_E_WRONGTHREAD</c>, so the work is
/// enqueued on the window's dispatcher and awaited through a completion source. This extends to
/// reading <c>Window.Content</c> or <see cref="XamlRoot"/> at all, not only to constructing or showing
/// the dialog: both are XAML properties, and a mere read of either from the calling thread crashed in
/// <c>combase.dll</c> the first time a password-protected archive was opened for real, rather than
/// throwing synchronously where it would have been obvious.
/// </para>
/// <para>
/// <b>Order.</b> WinUI permits exactly one open <see cref="ContentDialog"/> per window; a second one
/// throws. A batch extraction produces conflicts faster than a person answers them, so prompts queue
/// behind one another rather than racing.
/// </para>
/// <para>
/// <b>Lifetime.</b> The window is resolved lazily, because the container that owns these services is
/// built before the window exists. With no window there is nobody to ask, and the answer is the one a
/// cancelled dialog gives.
/// </para>
/// <para>
/// <b>Why the queue is a chain of tasks and not a semaphore.</b> It was a <c>SemaphoreSlim</c>, and
/// <c>CA1001</c> was right to object: that is a disposable field, and this object has no disposal point
/// to hang it on. It is constructed in <c>OnLaunched</c> and captured by the two adapters, so the
/// container never sees it, and the container is itself never disposed. Implementing
/// <see cref="IDisposable"/> would have added a method with no caller — the appearance of a managed
/// lifetime rather than one. Disposing a semaphore that a pending prompt is still waiting on would also
/// have thrown at exactly the wrong moment. Ordering is what was needed, not a resource: each caller
/// takes the tail of the chain, waits for the link in front of it, and completes its own link on the way
/// out. Nothing to dispose, and the order is now first-in-first-out, which a semaphore never promised.
/// </para>
/// </remarks>
internal sealed class WindowDialogHost : IPasswordDialog, IConflictDialog, IExtractDialog
{
    private readonly Func<MainWindow?> _window;
    private readonly object _sync = new();

    /// <summary>The tail of the prompt queue. Guarded by <see cref="_sync"/>.</summary>
    private Task _queue = Task.CompletedTask;

    public WindowDialogHost(Func<MainWindow?> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public async ValueTask<char[]?> ShowAsync(PasswordPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return await ShowDialogAsync<PasswordDialog, char[]>(
                _ => new PasswordDialog(prompt),
                static (dialog, result) => result == ContentDialogResult.Primary ? dialog.TakePassword() : null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ConflictDecision?> ShowAsync(ConflictPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return await ShowDialogAsync<ConflictDialog, ConflictDecision>(
                _ => new ConflictDialog(prompt),
                static (dialog, result) => result == ContentDialogResult.Primary ? dialog.Decision : null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ExtractChoice?> ShowAsync(ExtractPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return await ShowDialogAsync<ExtractDialog, ExtractChoice>(
                window => new ExtractDialog(
                    prompt,
                    WinRT.Interop.WindowNative.GetWindowHandle(window)),
                static (dialog, result) => result == ContentDialogResult.Primary ? dialog.Choice : null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TResult?> ShowDialogAsync<TDialog, TResult>(
        Func<MainWindow, TDialog> create,
        Func<TDialog, ContentDialogResult, TResult?> read,
        CancellationToken cancellationToken)
        where TDialog : ContentDialog
        where TResult : class
    {
        var current = _window();
        if (current is null)
        {
            return null;
        }

        // Copied into a non-nullable local before the lambda captures it: a captured nullable local
        // loses its proven state at the capture boundary, and the alternative is a scatter of ! that
        // says nothing about why.
        MainWindow window = current;

        // Take the tail of the queue and become the new tail, in one step, before waiting on anything.
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task ahead;
        lock (_sync)
        {
            ahead = _queue;
            _queue = mine.Task;
        }

        try
        {
            // Cancellation may leave the queue; it may not jump it. Somebody ahead is looking at an
            // open dialog, and a second one would throw rather than appear.
            await ahead.WaitAsync(cancellationToken).ConfigureAwait(false);

            var completion = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            TDialog? opened = null;

            var enqueued = window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // XamlRoot is a XAML property, read here and nowhere earlier: the caller is
                    // whichever worker thread the engine is decoding on, and touching Content or
                    // XamlRoot from there - even just to read them - is RPC_E_WRONGTHREAD
                    // (0x8001010E). It does not throw synchronously on the read; it surfaces later as
                    // a COM marshalling crash in combase.dll, which is what sent us back here.
                    var root = window.Content?.XamlRoot;
                    if (root is null)
                    {
                        // The window is there but has nothing to anchor a dialog to yet. Nobody is
                        // going to answer.
                        completion.TrySetResult(null);
                        return;
                    }

                    var dialog = create(window);
                    opened = dialog;
                    dialog.XamlRoot = root;

                    // The dialog lives in a popup outside the shell's element tree, so it inherits
                    // nothing from it. Carrying the theme across is what stops a dark window raising a
                    // light modal.
                    dialog.RequestedTheme = window.CurrentTheme;

                    completion.TrySetResult(read(dialog, await dialog.ShowAsync()));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });

            if (!enqueued)
            {
                // The window is going away. Nobody is going to answer.
                return null;
            }

            using var registration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                window.DispatcherQueue.TryEnqueue(() => opened?.Hide());
            });

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            // Every exit releases the caller behind us: returned, threw, or cancelled. Missing this on
            // any one path would stall every later prompt in the operation.
            mine.SetResult();
        }
    }
}
