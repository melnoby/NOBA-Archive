using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Engine;

/// <summary>
/// Everything a host can decide about the engine. NobaOS sets these; the desktop app binds them from
/// settings. Nothing here reads an environment variable or the <c>PATH</c>: discovery of the installed
/// product is the installer's contract, never a runtime dependency of the engine.
/// </summary>
public sealed record NobaArchiveEngineOptions
{
    /// <summary>Where settings, logs, and temp workspaces live. Null uses the per-user default.</summary>
    public string? AppDataRoot { get; set; }

    /// <summary>Directory holding native archive backends. Consumed from Phase 3 onward.</summary>
    public string? NativeLibraryDirectory { get; set; }

    public int MaxConcurrentOperations { get; set; } = 1;

    /// <summary>
    /// Default conflict behaviour. A headless host must not choose <see cref="ConflictPolicy.Ask"/>;
    /// that is rejected at configuration time rather than deadlocking at run time.
    /// </summary>
    public ConflictPolicy DefaultConflictPolicy { get; set; } = ConflictPolicy.Ask;

    /// <summary>True for the CLI and NobaOS: no prompt may ever be shown.</summary>
    public bool Headless { get; set; }

    public LinkPolicy LinkPolicy { get; set; } = LinkPolicy.Never;

    public IllegalNamePolicy IllegalNamePolicy { get; set; } = IllegalNamePolicy.Sanitize;

    public void Validate()
    {
        if (MaxConcurrentOperations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentOperations),
                MaxConcurrentOperations,
                "At least one concurrent operation is required.");
        }

        if (Headless && DefaultConflictPolicy == ConflictPolicy.Ask)
        {
            throw new InvalidOperationException(
                "A headless host cannot use ConflictPolicy.Ask: there is nobody to ask. " +
                "Choose Overwrite, Skip, Rename, or Fail.");
        }
    }

    /// <summary>
    /// Where to find <c>7z.dll</c>, when the host knows better than the search order does.
    /// </summary>
    /// <remarks>
    /// A directory or the library itself. NOBA OS sets this from its installation contract rather than
    /// relying on a 7-Zip install being present; PATH is deliberately not consulted anywhere.
    /// </remarks>
    public string? NativeLibraryPath { get; set; }
}
