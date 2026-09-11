using System.Buffers;
using System.Globalization;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Security;

/// <summary>
/// The product's trust boundary. Turns an archive-declared path into a
/// <see cref="SafeLocalPath"/> or a reason for refusal.
/// </summary>
/// <remarks>
/// Two classes of bad path are handled differently on purpose:
/// <list type="bullet">
/// <item><b>Escape attempts</b> - traversal segments, rooted/UNC/device paths, alternate data streams -
/// are rejected outright and reported. They are never "repaired", because repairing is how traversal
/// bugs get written.</item>
/// <item><b>Win32-illegal but harmless names</b> - reserved device names such as <c>aux.h</c> from a
/// Linux source tree, trailing dots and spaces - are deterministically renamed and reported. Rejecting
/// them would break legitimate archives, which is a usability defect wearing a security costume.</item>
/// </list>
/// </remarks>
public sealed class PathPolicy : IPathPolicy
{
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    // '<' '>' ':' '"' '/' '\' '|' '?' '*' - illegal in a Windows file name.
    private static readonly char[] IllegalNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly SearchValues<char> IllegalNameCharSet = SearchValues.Create(IllegalNameChars);

    private readonly PathPolicyOptions _options;

    public PathPolicy()
        : this(PathPolicyOptions.Default)
    {
    }

    public PathPolicy(PathPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public PathValidationResult Validate(string rawEntryPath, string destinationRoot, IllegalNamePolicy illegalNamePolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        // Step 1 - structural validation. Rejects the entire escape class.
        if (!ArchivePath.TryCreate(rawEntryPath, out var archivePath, out var rejection))
        {
            return PathValidationResult.Rejected(rejection!);
        }

        var root = NormalizeRoot(destinationRoot);

        // Step 2 - make each segment representable on Windows, recording what changed.
        var sanitizations = new List<PathSanitization>();
        var segments = new List<string>();
        foreach (var segment in archivePath.Segments)
        {
            if (segment.Length > _options.MaxSegmentLength)
            {
                return PathValidationResult.Rejected(new PathRejection(
                    PathRejectionReason.TooLong,
                    rawEntryPath,
                    $"The path segment '{Truncate(segment)}' exceeds {_options.MaxSegmentLength} characters."));
            }

            var safe = SanitizeSegment(segment, out var reason);
            if (!string.Equals(safe, segment, StringComparison.Ordinal))
            {
                if (illegalNamePolicy == IllegalNamePolicy.Skip)
                {
                    return PathValidationResult.Rejected(new PathRejection(
                        PathRejectionReason.InvalidCharacter,
                        rawEntryPath,
                        $"The path segment '{segment}' is not representable on Windows ({reason})."));
                }

                sanitizations.Add(new PathSanitization(segment, safe, reason));
            }

            segments.Add(safe);
        }

        // Step 3 - combine, canonicalise, and prove containment.
        var combined = Path.Combine(root, Path.Combine([.. segments]));
        string full;
        try
        {
            full = Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathValidationResult.Rejected(new PathRejection(
                PathRejectionReason.InvalidCharacter,
                rawEntryPath,
                $"The path could not be canonicalised: {ex.Message}"));
        }

        if (full.Length > _options.MaxPathLength)
        {
            return PathValidationResult.Rejected(new PathRejection(
                PathRejectionReason.TooLong,
                rawEntryPath,
                $"The resolved path exceeds {_options.MaxPathLength} characters."));
        }

        if (!IsInsideDestination(full, root))
        {
            return PathValidationResult.Rejected(new PathRejection(
                PathRejectionReason.EscapesDestination,
                rawEntryPath,
                "The resolved path falls outside the destination folder."));
        }

        return PathValidationResult.Accepted(SafeLocalPath.CreateValidated(full, root), sanitizations);
    }

    /// <summary>
    /// Separator-aware containment. A plain <c>StartsWith</c> would accept <c>C:\out-evil</c> as being
    /// inside <c>C:\out</c>; requiring the boundary to fall on a separator is what closes that.
    /// </summary>
    public bool IsInsideDestination(string candidateFullPath, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var root = NormalizeRoot(destinationRoot);
        var candidate = candidateFullPath;

        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsLinkTargetInsideDestination(string linkTarget, string linkContainingDirectory, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            return false;
        }

        // A link target is attacker data too: the same escape class applies before resolution.
        if (!ArchivePath.TryCreate(linkTarget.Replace('\\', '/'), out _, out _))
        {
            return false;
        }

        var root = NormalizeRoot(destinationRoot);
        if (!IsInsideDestination(NormalizeRoot(linkContainingDirectory), root))
        {
            return false;
        }

        try
        {
            var resolved = Path.GetFullPath(Path.Combine(linkContainingDirectory, linkTarget));
            return IsInsideDestination(resolved, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Makes one path segment representable on Windows without changing its identity beyond need.</summary>
    private string SanitizeSegment(string segment, out string reason)
    {
        reason = string.Empty;
        var result = segment;

        if (result.AsSpan().IndexOfAny(IllegalNameCharSet) >= 0)
        {
            Span<char> buffer = result.Length <= 256 ? stackalloc char[result.Length] : new char[result.Length];
            result.AsSpan().CopyTo(buffer);
            for (var i = 0; i < buffer.Length; i++)
            {
                if (IllegalNameCharSet.Contains(buffer[i]))
                {
                    buffer[i] = _options.SanitizationReplacement;
                }
            }

            result = new string(buffer);
            reason = "illegal character";
        }

        var stem = StemOf(result);
        if (Array.Exists(ReservedDeviceNames, n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase)))
        {
            result = string.Concat(result.AsSpan(0, stem.Length), "_", result.AsSpan(stem.Length));
            reason = string.IsNullOrEmpty(reason) ? "reserved device name" : reason + ", reserved device name";
        }

        var trimmed = result.TrimEnd('.', ' ');
        if (trimmed.Length != result.Length)
        {
            result = trimmed.Length == 0
                ? new string(_options.SanitizationReplacement, 1)
                : trimmed;
            reason = string.IsNullOrEmpty(reason) ? "trailing dot or space" : reason + ", trailing dot or space";
        }

        return result;
    }

    private static string StemOf(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? segment : segment[..dot];
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return full.Length > 3 && full.EndsWith(Path.DirectorySeparatorChar)
            ? full.TrimEnd(Path.DirectorySeparatorChar)
            : full;
    }

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : string.Create(CultureInfo.InvariantCulture, $"{value[..57]}...");
}
