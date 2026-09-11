using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Core.Formats;

public enum CompressionMethod
{
    Store = 0,
    Deflate,
    Deflate64,
    BZip2,
    Lzma,
    Lzma2,
    Ppmd,
    Xz,
    Zstandard,
}

/// <summary>Named presets the Create dialog offers before any advanced setting is exposed.</summary>
public enum CompressionProfile
{
    Fast = 0,
    Balanced,
    MaximumCompression,
    SmallestSize,
    Custom,
}

public enum EncryptionAlgorithm
{
    None = 0,
    Aes256,
}

/// <summary>Everything needed to create an archive. Providers map these onto backend-specific settings.</summary>
public sealed record CompressionOptions
{
    public required ArchiveFormat Format { get; init; }

    public CompressionProfile Profile { get; init; } = CompressionProfile.Balanced;

    public CompressionLevel Level { get; init; } = CompressionLevel.Normal;

    public CompressionMethod? Method { get; init; }

    public DictionarySize? DictionarySize { get; init; }

    public bool Solid { get; init; }

    public EncryptionAlgorithm Encryption { get; init; } = EncryptionAlgorithm.None;

    public bool EncryptFileNames { get; init; }

    public ByteSize? VolumeSize { get; init; }

    public bool DeleteSourcesAfterVerifiedSuccess { get; init; }

    public int? ThreadCount { get; init; }

    public bool RequiresPassword => Encryption != EncryptionAlgorithm.None;

    /// <summary>Level and method implied by a profile; <see cref="CompressionProfile.Custom"/> leaves them alone.</summary>
    public static CompressionOptions ForProfile(ArchiveFormat format, CompressionProfile profile) => profile switch
    {
        CompressionProfile.Fast => new CompressionOptions
        {
            Format = format,
            Profile = profile,
            Level = CompressionLevel.Fastest,
        },
        CompressionProfile.Balanced => new CompressionOptions
        {
            Format = format,
            Profile = profile,
            Level = CompressionLevel.Normal,
        },
        CompressionProfile.MaximumCompression => new CompressionOptions
        {
            Format = format,
            Profile = profile,
            Level = CompressionLevel.Maximum,
        },
        CompressionProfile.SmallestSize => new CompressionOptions
        {
            Format = format,
            Profile = profile,
            Level = CompressionLevel.Maximum,
            Solid = true,
        },
        CompressionProfile.Custom => new CompressionOptions { Format = format, Profile = profile },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown compression profile."),
    };

    /// <summary>The capabilities these options require of a provider.</summary>
    public ArchiveCapabilities RequiredCapabilities()
    {
        var required = ArchiveCapabilities.Create;
        if (Encryption != EncryptionAlgorithm.None)
        {
            required |= ArchiveCapabilities.Encrypt;
        }

        if (EncryptFileNames)
        {
            required |= ArchiveCapabilities.EncryptFileNames;
        }

        if (Solid)
        {
            required |= ArchiveCapabilities.SolidCompression;
        }

        if (VolumeSize is not null)
        {
            required |= ArchiveCapabilities.SplitVolumes;
        }

        return required;
    }
}
