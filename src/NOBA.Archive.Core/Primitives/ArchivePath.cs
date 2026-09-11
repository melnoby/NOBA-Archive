using System.Diagnostics.CodeAnalysis;

namespace NOBA.Archive.Core.Primitives;

/// <summary>
/// A normalised, validated path <em>inside</em> an archive: forward slashes, relative, no traversal,
/// no alternate-data-stream marker, no control characters.
/// </summary>
/// <remarks>
/// Instances can only be produced through <see cref="TryCreate"/>, so possessing one is proof the
/// path passed structural validation. It is <em>not</em> proof that writing it to a given destination
/// is safe - that is <see cref="SafeLocalPath"/>'s job, and it requires a destination root.
/// </remarks>
public readonly record struct ArchivePath
{
    public const int MaxDepth = 64;
    public const int MaxLength = 4096;

    private readonly string? _value;

    private ArchivePath(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public string[] Segments => Value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    public string FileName
    {
        get
        {
            var v = Value;
            var i = v.LastIndexOf('/');
            return i < 0 ? v : v[(i + 1)..];
        }
    }

    public string DirectoryPath
    {
        get
        {
            var v = Value;
            var i = v.LastIndexOf('/');
            return i < 0 ? string.Empty : v[..i];
        }
    }

    /// <summary>The extension as Windows would resolve it, lower-cased, including the dot ("" when none).</summary>
    public string Extension
    {
        get
        {
            var name = FileName;
            var i = name.LastIndexOf('.');
            return i <= 0 || i == name.Length - 1 ? string.Empty : name[i..].ToLowerInvariant();
        }
    }

    public static bool TryCreate(string? raw, [NotNullWhen(true)] out ArchivePath path, out PathRejection? rejection)
    {
        path = default;
        rejection = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            rejection = new PathRejection(PathRejectionReason.Empty, raw ?? string.Empty, "The entry path is empty.");
            return false;
        }

        if (raw.Length > MaxLength)
        {
            rejection = new PathRejection(PathRejectionReason.TooLong, raw, $"The entry path exceeds {MaxLength} characters.");
            return false;
        }

        foreach (var c in raw)
        {
            if (char.IsControl(c))
            {
                rejection = new PathRejection(
                    PathRejectionReason.ControlCharacter,
                    raw,
                    $"The entry path contains the control character U+{(int)c:X4}.");
                return false;
            }
        }

        var normalised = raw.Replace('\\', '/');

        if (normalised.StartsWith("//", StringComparison.Ordinal))
        {
            rejection = new PathRejection(PathRejectionReason.UncPath, raw, "The entry path is a UNC path.");
            return false;
        }

        if (normalised.StartsWith('/'))
        {
            rejection = new PathRejection(PathRejectionReason.RootedPath, raw, "The entry path is rooted.");
            return false;
        }

        if (normalised.Contains("//./", StringComparison.Ordinal) || normalised.StartsWith("//?/", StringComparison.Ordinal))
        {
            rejection = new PathRejection(PathRejectionReason.DevicePath, raw, "The entry path is a Win32 device path.");
            return false;
        }

        // Any colon is refused. A drive letter (C:\...) and an NTFS alternate data stream
        // (readme.txt:payload.ps1) are both spelled with one, and neither is ever legitimate
        // inside an archive entry. CVE-2025-8088 is the ADS case.
        var colon = normalised.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var reason = colon == 1 && char.IsLetter(normalised[0])
                ? PathRejectionReason.DriveRelativePath
                : PathRejectionReason.AlternateDataStream;
            var detail = reason == PathRejectionReason.DriveRelativePath
                ? "The entry path carries a drive letter."
                : "The entry path contains an NTFS alternate data stream marker (':').";
            rejection = new PathRejection(reason, raw, detail);
            return false;
        }

        var segments = new List<string>();
        var depth = 0;
        foreach (var segment in normalised.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            // '..' is rejected, never resolved. Resolving is how traversal bugs are written.
            if (segment == ".." || segment.All(static c => c == '.'))
            {
                rejection = new PathRejection(
                    PathRejectionReason.TraversalSegment,
                    raw,
                    $"The entry path contains the traversal segment '{segment}'.");
                return false;
            }

            segments.Add(segment);
            if (++depth > MaxDepth)
            {
                rejection = new PathRejection(
                    PathRejectionReason.TooDeep,
                    raw,
                    $"The entry path is deeper than {MaxDepth} levels.");
                return false;
            }
        }

        if (segments.Count == 0)
        {
            rejection = new PathRejection(PathRejectionReason.Empty, raw, "The entry path has no usable segments.");
            return false;
        }

        path = new ArchivePath(string.Join('/', segments));
        return true;
    }

    public override string ToString() => Value;
}
