using System.Globalization;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Application.Policies;

/// <summary>
/// The single implementation of Overwrite / Skip / Rename / Ask, including "...All" latching.
/// Duplicating this across extraction and compression is how silent overwrites get shipped.
/// </summary>
public sealed class ConflictResolutionService : IConflictResolutionService
{
    private readonly IConflictPrompt _prompt;
    private readonly Lock _gate = new();
    private ConflictAction? _latched;

    public ConflictResolutionService(IConflictPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _prompt = prompt;
    }

    public async ValueTask<ConflictAction> ResolveAsync(
        ConflictRequest request,
        ConflictPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_latched is { } latched)
            {
                return latched;
            }
        }

        switch (policy)
        {
            case ConflictPolicy.Overwrite:
                return ConflictAction.Overwrite;
            case ConflictPolicy.Skip:
                return ConflictAction.Skip;
            case ConflictPolicy.Rename:
                return ConflictAction.Rename;
            case ConflictPolicy.Fail:
                return ConflictAction.Cancel;
            case ConflictPolicy.Ask:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown conflict policy.");
        }

        var decision = await _prompt.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision.ApplyToAll)
        {
            lock (_gate)
            {
                _latched ??= decision.Action;
            }
        }

        return decision.Action;
    }

    /// <summary>Produces <c>report (2).pdf</c>, <c>report (3).pdf</c>, ... - never an unbounded loop.</summary>
    public string BuildRenamedPath(string destinationPath, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(exists);

        var directory = Path.GetDirectoryName(destinationPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);

        for (var i = 2; i <= 10_000; i++)
        {
            var candidate = Path.Combine(
                directory,
                string.Create(CultureInfo.InvariantCulture, $"{stem} ({i}){extension}"));
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find a free name for '{destinationPath}' after 10,000 attempts.");
    }

    public void ResetLatchedDecision()
    {
        lock (_gate)
        {
            _latched = null;
        }
    }
}
