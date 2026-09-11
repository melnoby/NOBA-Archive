using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOBA.Archive.Presentation.ViewModels;

namespace NOBA.Archive.App.Views;

/// <summary>
/// The operations screen: everything the queue is doing or has done.
/// </summary>
/// <remarks>
/// <para>
/// A row is not a status line. Each one carries the controls for its own operation, enabled from the
/// queue's state rather than from what this screen believes: an operation that cannot be paused shows
/// a disabled button, and the reason it cannot is that the state machine says so.
/// </para>
/// <para>
/// Rows are rebuilt rather than mutated. The list is short by nature — a person does not run forty
/// extractions — and rebuilding removes a whole class of defect where a row and its operation drift
/// apart. The file table makes the opposite trade for the opposite reason.
/// </para>
/// </remarks>
public sealed partial class OperationsPage : UserControl
{
    private readonly Dictionary<string, OperationRow> _rows = new(StringComparer.Ordinal);
    private ShellViewModel? _shell;
    private int _renderQueued;

    public OperationsPage() => InitializeComponent();

    private OperationsViewModel? Model => _shell?.Operations;

    public void Bind(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        Model!.PropertyChanged += OnOperationsChanged;
    }

    /// <summary>
    /// Redraws, from whichever thread the notification arrived on.
    /// </summary>
    /// <remarks>
    /// The queue reports from a pool thread, so this marshals and coalesces exactly as the shell's
    /// strip does. Two subscribers to the same events must both pay the same cost; neither can assume
    /// the other already did.
    /// </remarks>
    private void OnOperationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Render();
        });
    }

    public void ApplyLanguage(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var text = shell.Text;

        PageTitle.Text = text.NavOperations;
        ClearFinishedButton.Content = text.OperationClearFinished;
        EmptyTitle.Text = text.OperationsEmptyTitle;
        EmptyBody.Text = text.OperationsEmptyBody;

        Render();
    }

    /// <summary>
    /// Refreshes the list, creating a card only for an operation that has not got one.
    /// </summary>
    /// <remarks>
    /// The previous version cleared the list and rebuilt every card on every notification — including
    /// the thirty-a-second progress reports of a running extraction. Cards are now kept and updated in
    /// place, and the panel is only rearranged when the set of operations actually changes.
    /// </remarks>
    public void Render()
    {
        if (_shell is null || Model is null)
        {
            return;
        }

        var text = _shell.Text;
        var all = Model.All;

        EmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFinishedButton.IsEnabled = Model.HasFinished;

        // Drop cards whose operation the queue has forgotten.
        var live = all.Select(static o => o.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _rows.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        var reordered = OperationList.Items.Count != all.Count;
        for (var i = 0; i < all.Count && !reordered; i++)
        {
            reordered = !ReferenceEquals(OperationList.Items[i], Row(all[i]).Element);
        }

        foreach (var operation in all)
        {
            Row(operation).Update(text);
        }

        if (!reordered)
        {
            return;
        }

        OperationList.Items.Clear();
        foreach (var operation in all)
        {
            OperationList.Items.Add(Row(operation).Element);
        }
    }

    private OperationRow Row(OperationViewModel operation)
    {
        if (!_rows.TryGetValue(operation.Id.Value, out var row))
        {
            row = new OperationRow(operation, Model!);
            _rows[operation.Id.Value] = row;
        }

        return row;
    }

    private void OnClearFinished(object sender, RoutedEventArgs e)
    {
        Model?.ClearFinished();
        Render();
    }
}
