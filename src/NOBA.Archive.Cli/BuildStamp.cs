namespace NOBA.Archive.Cli;

/// <summary>
/// Which source drop this binary was built from.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a stale source tree has now silently passed validation twice. Both times every
/// phase reported PASS and the version banner read exactly as expected — because neither says anything
/// about <em>which</em> source produced them. A Phase 3 run against an un-extracted Phase 2 tree, and a
/// RAR-fix run against the tree before the fix, are both indistinguishable from the real thing in a
/// transcript unless something in that transcript names the drop.
/// </para>
/// <para>
/// Bump <see cref="Value"/> with every source drop. <c>scripts/validate.ps1</c> reads this file before
/// building and prints it in the environment fingerprint, so the transcript identifies the tree even
/// when the build fails.
/// </para>
/// </remarks>
internal static class BuildStamp
{
    /// <summary>Drop identifier. Format: date, then what the drop was about.</summary>
    public const string Value = "2026-10-19-drop-13-followup-l40-l45";
}
