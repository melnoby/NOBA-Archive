namespace NOBA.Archive.Security;

public sealed record PathPolicyOptions
{
    /// <summary>Windows' classic limit. Longer paths need the <c>\\?\</c> prefix and a long-path-aware app.</summary>
    public int MaxPathLength { get; init; } = 32_000;

    public int MaxSegmentLength { get; init; } = 255;

    /// <summary>Character substituted for one Windows cannot represent in a file name.</summary>
    public char SanitizationReplacement { get; init; } = '_';

    public static PathPolicyOptions Default { get; } = new();
}
