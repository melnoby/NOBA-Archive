using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Abstractions;

/// <summary>Outcome of validating one archive-declared path against a destination root.</summary>
public sealed record PathValidationResult
{
    public SafeLocalPath? Path { get; init; }

    public PathRejection? Rejection { get; init; }

    public IReadOnlyList<PathSanitization> Sanitizations { get; init; } = [];

    public bool IsAccepted => Rejection is null && Path is not null;

    public bool WasSanitized => Sanitizations.Count > 0;

    public static PathValidationResult Accepted(SafeLocalPath path, IReadOnlyList<PathSanitization>? sanitizations = null) =>
        new() { Path = path, Sanitizations = sanitizations ?? [] };

    public static PathValidationResult Rejected(PathRejection rejection) => new() { Rejection = rejection };
}

/// <summary>
/// The trust boundary of the product (THREAT_MODEL.md TB2). Everything crossing it is hostile until
/// this says otherwise.
/// </summary>
public interface IPathPolicy
{
    /// <summary>
    /// Validates an archive-declared path against a destination root and returns a
    /// <see cref="SafeLocalPath"/> or a reason for refusal. Rooted paths, traversal segments, UNC and
    /// device paths, and alternate-data-stream names are refused. Win32-illegal but harmless names are
    /// sanitized or skipped per <paramref name="illegalNamePolicy"/>.
    /// </summary>
    PathValidationResult Validate(string rawEntryPath, string destinationRoot, IllegalNamePolicy illegalNamePolicy);

    /// <summary>
    /// Validates a link target. Returns false unless the target provably resolves inside
    /// <paramref name="destinationRoot"/>.
    /// </summary>
    bool IsLinkTargetInsideDestination(string linkTarget, string linkContainingDirectory, string destinationRoot);

    /// <summary>Containment check on already-canonical absolute paths, with a separator-aware boundary.</summary>
    bool IsInsideDestination(string candidateFullPath, string destinationRoot);
}
