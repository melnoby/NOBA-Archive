using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Application.Policies;

public sealed record DiskSpaceEstimate(ByteSize Required, ByteSize Available, bool IsExact)
{
    public bool IsSufficient => Available >= Required;

    public ByteSize Shortfall => IsSufficient ? ByteSize.Zero : ByteSize.FromBytes(Required.Bytes - Available.Bytes);
}

/// <summary>
/// Pre-flight space check. <see cref="DiskSpaceEstimate.IsExact"/> is part of the contract: an estimate
/// derived from archive metadata is a guess, and the UI must not present a guess as a fact.
/// </summary>
public sealed class DiskSpaceEstimator
{
    private readonly IFileSystem _fileSystem;

    public DiskSpaceEstimator(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <summary>Adds a slack margin for filesystem overhead and the staged temp copy.</summary>
    public DiskSpaceEstimate Estimate(string destinationPath, ByteSize declaredUncompressedTotal, bool totalIsTrustworthy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var space = _fileSystem.GetVolumeSpace(destinationPath);
        var margin = Math.Max(declaredUncompressedTotal.Bytes / 100, 16L * 1024 * 1024);
        var required = ByteSize.FromBytes(checked(declaredUncompressedTotal.Bytes + margin));
        return new DiskSpaceEstimate(required, ByteSize.FromBytes(space.AvailableFreeBytes), totalIsTrustworthy);
    }
}
