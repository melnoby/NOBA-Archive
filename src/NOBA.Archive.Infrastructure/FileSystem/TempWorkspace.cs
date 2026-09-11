using System.Globalization;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// A per-operation scratch directory that deletes itself, plus a startup sweeper for directories left
/// behind by a process that is no longer running (THREAT_MODEL.md T9).
/// </summary>
public sealed class TempWorkspaceFactory : ITempWorkspaceFactory
{
    private const string OwnerFileName = ".noba-owner";

    private readonly IAppDataPathProvider _paths;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TempWorkspaceFactory> _logger;

    public TempWorkspaceFactory(
        IAppDataPathProvider paths,
        IFileSystem fileSystem,
        ILogger<TempWorkspaceFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public ITempWorkspace Create(OperationId operationId)
    {
        var root = Path.Combine(_paths.TempRoot, operationId.Value);
        _fileSystem.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, OwnerFileName),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return new TempWorkspace(operationId, root, _fileSystem, _logger);
    }

    /// <summary>Removes workspaces whose owning process is gone. Returns how many were reclaimed.</summary>
    public int SweepOrphans()
    {
        if (!_fileSystem.DirectoryExists(_paths.TempRoot))
        {
            return 0;
        }

        var swept = 0;
        foreach (var entry in _fileSystem.EnumerateEntries(_paths.TempRoot, recursive: false, CancellationToken.None))
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            if (IsOwnedByLiveProcess(entry.FullPath))
            {
                continue;
            }

            try
            {
                _fileSystem.DeleteDirectory(entry.FullPath, recursive: true);
                swept++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove the stale temporary workspace {Workspace}.",
                    entry.Name);
            }
        }

        return swept;
    }

    private bool IsOwnedByLiveProcess(string workspacePath)
    {
        var ownerFile = Path.Combine(workspacePath, OwnerFileName);
        if (!_fileSystem.FileExists(ownerFile))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(ownerFile).Trim();
            if (!int.TryParse(text, CultureInfo.InvariantCulture, out var pid))
            {
                return false;
            }

            if (pid == Environment.ProcessId)
            {
                return true;
            }

            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process: the workspace is an orphan.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell; leave it alone rather than delete someone else's data.
            return true;
        }
    }

    private sealed class TempWorkspace : ITempWorkspace
    {
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private int _sequence;
        private int _disposed;

        public TempWorkspace(OperationId ownerOperation, string rootPath, IFileSystem fileSystem, ILogger logger)
        {
            OwnerOperation = ownerOperation;
            RootPath = rootPath;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public OperationId OwnerOperation { get; }

        public string RootPath { get; }

        public string CreateFilePath(string suggestedName)
        {
            var index = Interlocked.Increment(ref _sequence);
            var safe = string.Concat(Path.GetFileName(suggestedName).Where(static c => !Path.GetInvalidFileNameChars().Contains(c)));
            if (safe.Length == 0)
            {
                safe = "item";
            }

            return Path.Combine(RootPath, string.Create(CultureInfo.InvariantCulture, $"{index:D6}-{safe}"));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            try
            {
                _fileSystem.DeleteDirectory(RootPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove the temporary workspace for operation {OperationId}.",
                    OwnerOperation.Value);
            }

            return ValueTask.CompletedTask;
        }
    }
}
