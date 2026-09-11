using NOBA.Archive.Core.Errors;

namespace NOBA.Archive.Cli;

/// <summary>
/// The CLI's stable contract with NobaOS. These values are documented in NOBAOS_INTEGRATION.md and
/// changing one is a major-version event.
/// </summary>
public enum ExitCode
{
    Success = 0,
    UsageError = 1,
    ArchiveNotFound = 2,
    UnsupportedOperation = 3,
    PasswordRequired = 4,
    CorruptArchive = 5,
    BlockedBySecurityPolicy = 6,
    InsufficientDiskSpace = 7,
    Cancelled = 8,
    IoFailure = 9,
    BackendFailure = 10,
}

public static class ExitCodeMapper
{
    /// <summary>Maps the closed error set onto the closed exit-code set. No error falls through unmapped.</summary>
    public static ExitCode FromError(ArchiveErrorCode code) => code switch
    {
        ArchiveErrorCode.None => ExitCode.Success,
        ArchiveErrorCode.PasswordRequired or ArchiveErrorCode.WrongPassword => ExitCode.PasswordRequired,
        ArchiveErrorCode.CorruptData => ExitCode.CorruptArchive,
        ArchiveErrorCode.UnsupportedFormat
            or ArchiveErrorCode.UnsupportedMethod
            or ArchiveErrorCode.UnsupportedOperation => ExitCode.UnsupportedOperation,
        ArchiveErrorCode.AccessDenied => ExitCode.IoFailure,
        ArchiveErrorCode.DiskFull => ExitCode.InsufficientDiskSpace,
        ArchiveErrorCode.PathTooLong
            or ArchiveErrorCode.PathRejected
            or ArchiveErrorCode.BombSuspected => ExitCode.BlockedBySecurityPolicy,
        ArchiveErrorCode.VolumeMissing => ExitCode.CorruptArchive,
        ArchiveErrorCode.Cancelled => ExitCode.Cancelled,
        ArchiveErrorCode.IoFailure => ExitCode.IoFailure,
        ArchiveErrorCode.InvalidConfiguration => ExitCode.UsageError,
        ArchiveErrorCode.BackendFailure => ExitCode.BackendFailure,
        _ => ExitCode.BackendFailure,
    };
}
