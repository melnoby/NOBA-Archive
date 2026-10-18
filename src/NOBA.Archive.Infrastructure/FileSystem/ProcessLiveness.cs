namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// Whether the process that stamped its PID on a staging file name or an owner marker is still
/// running — the one check every orphan-cleanup path in this project needs (THREAT_MODEL.md T9),
/// written once rather than duplicated in <see cref="TempWorkspaceFactory"/> and
/// <see cref="ArchivePublisher"/>.
/// </summary>
internal static class ProcessLiveness
{
    /// <summary>
    /// True when <paramref name="pid"/> names this process or another process that has not exited.
    /// Conservative on uncertainty: an inability to tell says "alive," so a sweep never deletes
    /// something it cannot prove is abandoned.
    /// </summary>
    public static bool IsAlive(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process: whatever it left behind is an orphan.
            return false;
        }
        catch (Exception)
        {
            // Cannot tell (permissions, a transient query failure, ...); leave it alone rather than
            // delete something that might still be in use.
            return true;
        }
    }
}
