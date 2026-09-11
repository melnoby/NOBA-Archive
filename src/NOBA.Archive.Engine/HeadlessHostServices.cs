using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Engine;

/// <summary>
/// Password provider for a host with no user present. It fails cleanly instead of blocking forever,
/// so a NobaOS-driven operation on an encrypted archive returns exit code 4 rather than hanging.
/// </summary>
public sealed class FailFastPasswordProvider : IPasswordProvider
{
    public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.PasswordRequired,
            $"'{request.ArchiveDisplayName}' is encrypted and no password was supplied."));
    }
}

/// <summary>A password supplied once by the host, e.g. from the CLI's stdin.</summary>
public sealed class StaticPasswordProvider : IPasswordProvider, IDisposable
{
    private readonly Password _password;

    public StaticPasswordProvider(Password password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _password = password;
    }

    public ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A retry means the password we hold is wrong; asking again with the same value would only
        // burn attempts against an archive that may be rate-limited by its own KDF.
        return ValueTask.FromResult(request.IsRetry ? null : _password);
    }

    public void Dispose() => _password.Dispose();
}

/// <summary>Applies a fixed policy. Registered whenever the host is headless.</summary>
public sealed class FixedConflictPrompt : IConflictPrompt
{
    private readonly ConflictAction _action;

    public FixedConflictPrompt(ConflictPolicy policy)
    {
        _action = policy switch
        {
            ConflictPolicy.Overwrite => ConflictAction.Overwrite,
            ConflictPolicy.Skip => ConflictAction.Skip,
            ConflictPolicy.Rename => ConflictAction.Rename,
            ConflictPolicy.Fail => ConflictAction.Cancel,
            ConflictPolicy.Ask => throw new InvalidOperationException(
                "ConflictPolicy.Ask cannot be resolved without a user."),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown conflict policy."),
        };
    }

    public ValueTask<ConflictDecision> ResolveAsync(ConflictRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(ConflictDecision.All(_action));
    }
}
