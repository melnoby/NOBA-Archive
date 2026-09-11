namespace NOBA.Archive.Core.Formats;

public enum DetectionSource
{
    None = 0,

    /// <summary>Magic bytes. Authoritative.</summary>
    Content,

    /// <summary>File extension. A hint, used only when sniffing is inconclusive.</summary>
    Extension,

    /// <summary>The user chose "Open as...".</summary>
    UserOverride,
}

public sealed record FormatDetection(ArchiveFormat Format, DetectionSource Source, int Confidence)
{
    public static FormatDetection None { get; } = new(ArchiveFormat.Unknown, DetectionSource.None, 0);

    /// <summary>
    /// What the content actually looks like, when it is recognisable but not an archive we support —
    /// "Compound File (encrypted Office document)", say.
    /// </summary>
    /// <remarks>
    /// Worth carrying because "corrupt archive" and "not an archive" are different problems with
    /// different fixes, and only one of them is the user's file being broken.
    /// </remarks>
    public string? IdentifiedAs { get; init; }

    public bool IsKnown => Source != DetectionSource.None && Format != ArchiveFormat.Unknown;

    /// <summary>True when the extension disagrees with the sniffed content - shown in the UI, never hidden.</summary>
    public bool ContradictsExtension(string? fileName)
    {
        if (Source != DetectionSource.Content)
        {
            return false;
        }

        var guess = ArchiveFormat.GuessFromExtension(fileName);
        return guess is not null && guess != Format;
    }
}
