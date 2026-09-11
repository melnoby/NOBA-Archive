namespace NOBA.Archive.Core.Errors;

/// <summary>
/// The closed set of failure reasons. Providers translate every backend exception into one of these;
/// the UI maps the code to a localised message and shows the technical detail only behind "Details".
/// </summary>
public enum ArchiveErrorCode
{
    None = 0,
    PasswordRequired,
    WrongPassword,
    CorruptData,
    UnsupportedFormat,
    UnsupportedMethod,
    UnsupportedOperation,
    AccessDenied,
    DiskFull,
    PathTooLong,
    PathRejected,
    BombSuspected,
    VolumeMissing,
    Cancelled,
    BackendFailure,
    IoFailure,
    InvalidConfiguration,
}

/// <summary>A failure, as data. No compression-library exception ever escapes NOBA.Archive.Formats.</summary>
public sealed record ArchiveError
{
    public required ArchiveErrorCode Code { get; init; }

    /// <summary>Diagnostic text for the Details panel and the log. Never shown as the headline message.</summary>
    public required string TechnicalDetail { get; init; }

    public string? Path { get; init; }

    public Exception? Inner { get; init; }

    public bool IsRetryable => Code is ArchiveErrorCode.AccessDenied or ArchiveErrorCode.IoFailure or ArchiveErrorCode.DiskFull;

    public static ArchiveError Create(ArchiveErrorCode code, string technicalDetail, string? path = null, Exception? inner = null) =>
        new() { Code = code, TechnicalDetail = technicalDetail, Path = path, Inner = inner };

    public override string ToString() => $"{Code}: {TechnicalDetail}";
}

/// <summary>Thrown only across the Application boundary, and only carrying an <see cref="ArchiveError"/>.</summary>
public sealed class ArchiveOperationException : Exception
{
    public ArchiveOperationException(ArchiveError error)
        : base(error?.TechnicalDetail ?? "Archive operation failed.", error?.Inner)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public ArchiveOperationException()
        : this(ArchiveError.Create(ArchiveErrorCode.BackendFailure, "Archive operation failed."))
    {
    }

    public ArchiveOperationException(string message)
        : this(ArchiveError.Create(ArchiveErrorCode.BackendFailure, message))
    {
    }

    public ArchiveOperationException(string message, Exception innerException)
        : this(ArchiveError.Create(ArchiveErrorCode.BackendFailure, message, inner: innerException))
    {
    }

    public ArchiveError Error { get; }
}
