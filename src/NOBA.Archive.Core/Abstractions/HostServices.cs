using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Abstractions;

public sealed record PasswordRequest(string ArchiveDisplayName, string? EntryPath, bool IsRetry);

/// <summary>
/// Supplies passwords. The desktop UI prompts; a headless host (the CLI, NobaOS) reads stdin or fails
/// fast. Backends never prompt for themselves.
/// </summary>
public interface IPasswordProvider
{
    /// <summary>Returns null when no password is available. The caller owns and disposes the result.</summary>
    ValueTask<Password?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken);
}

public sealed record ConflictRequest(string DestinationPath, ByteSize ExistingSize, ByteSize IncomingSize);

/// <summary>Asks what to do about an existing file. A fixed-policy implementation is used headless.</summary>
public interface IConflictPrompt
{
    ValueTask<ConflictDecision> ResolveAsync(ConflictRequest request, CancellationToken cancellationToken);
}

/// <summary>The single implementation of Overwrite/Skip/Rename/Ask, including "...All" latching.</summary>
public interface IConflictResolutionService
{
    ValueTask<ConflictAction> ResolveAsync(ConflictRequest request, ConflictPolicy policy, CancellationToken cancellationToken);

    /// <summary>Produces a non-colliding name, e.g. <c>report (2).pdf</c>.</summary>
    string BuildRenamedPath(string destinationPath, Func<string, bool> exists);

    void ResetLatchedDecision();
}

/// <summary>Injected so tests are deterministic and nothing calls <c>DateTime.Now</c> directly.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Where settings, logs, and temp workspaces live. Lets NobaOS relocate all state.</summary>
public interface IAppDataPathProvider
{
    string AppDataRoot { get; }

    string SettingsFilePath { get; }

    string LogDirectory { get; }

    string TempRoot { get; }
}

/// <summary>A per-operation scratch directory that cleans itself up.</summary>
public interface ITempWorkspace : IAsyncDisposable
{
    OperationId OwnerOperation { get; }

    string RootPath { get; }

    string CreateFilePath(string suggestedName);
}

public interface ITempWorkspaceFactory
{
    ITempWorkspace Create(OperationId operationId);

    /// <summary>Removes workspaces left by processes that no longer exist. Runs at startup.</summary>
    int SweepOrphans();
}

/// <summary>Update seam with no URL anywhere in the codebase. Disabled in 1.0 (THREAT_MODEL.md T12).</summary>
public interface IUpdateService
{
    bool IsEnabled { get; }

    ValueTask<bool> CheckForUpdateAsync(CancellationToken cancellationToken);
}
