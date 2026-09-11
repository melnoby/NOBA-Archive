namespace NOBA.Archive.Core.Primitives;

/// <summary>
/// An absolute local path that has been proven to sit inside a declared destination root.
/// Only <c>NOBA.Archive.Security</c> can mint one, and only the extraction writer consumes it.
/// </summary>
public readonly record struct SafeLocalPath
{
    private readonly string? _full;
    private readonly string? _root;

    private SafeLocalPath(string full, string root)
    {
        _full = full;
        _root = root;
    }

    public string FullPath => _full ?? string.Empty;

    public string DestinationRoot => _root ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_full);

    /// <summary>
    /// Creates the value. Intentionally not part of the public validation flow: callers must go through
    /// <c>IPathPolicy</c>, which performs the canonicalisation and containment checks first.
    /// </summary>
    public static SafeLocalPath CreateValidated(string fullPath, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return new SafeLocalPath(fullPath, destinationRoot);
    }

    public override string ToString() => FullPath;
}
