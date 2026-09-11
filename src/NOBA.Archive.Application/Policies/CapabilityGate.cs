using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Application.Policies;

/// <summary>
/// One place that decides whether an operation is allowed for a format. The UI binds command
/// availability to the same answer, so a user is never offered something that will fail on click.
/// </summary>
public static class CapabilityGate
{
    public static bool IsAllowed(ArchiveCapabilities available, ArchiveCapabilities required) =>
        available.Has(required);

    /// <summary>The capabilities that were requested but are missing.</summary>
    public static ArchiveCapabilities Missing(ArchiveCapabilities available, ArchiveCapabilities required) =>
        required & ~available;

    public static ArchiveError? Check(ArchiveCapabilities available, ArchiveCapabilities required, ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (IsAllowed(available, required))
        {
            return null;
        }

        var missing = Missing(available, required);
        return ArchiveError.Create(
            ArchiveErrorCode.UnsupportedOperation,
            $"The {format.DisplayName} backend does not support: {missing}.");
    }

    public static void EnsureAllowed(ArchiveCapabilities available, ArchiveCapabilities required, ArchiveFormat format)
    {
        var error = Check(available, required, format);
        if (error is not null)
        {
            throw new ArchiveOperationException(error);
        }
    }
}
