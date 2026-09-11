using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Core.Abstractions;

/// <summary>Resolves (format, required capability) to the best available provider.</summary>
public interface IArchiveProviderRegistry
{
    IReadOnlyList<IArchiveProvider> Providers { get; }

    /// <summary>Every format any registered provider can open.</summary>
    IReadOnlyList<ArchiveFormat> SupportedFormats { get; }

    /// <summary>The union of what any provider can do with a format - what the UI may offer at all.</summary>
    ArchiveCapabilities CapabilitiesFor(ArchiveFormat format);

    /// <summary>Highest-priority provider satisfying every requested capability, or null.</summary>
    IArchiveProvider? Resolve(ArchiveFormat format, ArchiveCapabilities required);
}

/// <summary>Identifies a format from its content. Extension is a hint only.</summary>
public interface IFormatDetector
{
    ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken);

    FormatDetection DetectFromHeader(ReadOnlySpan<byte> header, string? fileName);
}
