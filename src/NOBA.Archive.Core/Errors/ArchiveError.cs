namespace NOBA.Archive.Core.Errors;

/// <summary>
/// The closed set of failure reasons. Providers translate every backend exception into one of these.
/// </summary>
/// <remarks>
/// <b>DROP 12 correction:</b> this used to claim the UI maps the code to a localised message and shows
/// <see cref="ArchiveError.TechnicalDetail"/> only behind a "Details" panel. No such two-tier mapping
/// exists, in the CLI or in WinUI — both show <c>TechnicalDetail</c> directly as the only message there
/// is (`CliApplication.Fail`, `CreateViewModel`'s failure text). Checked rather than assumed: every
/// <c>TechnicalDetail</c> string in this codebase is already written in plain language ("There is not
/// enough space on the destination volume.", not a stack trace), so this is documented as the real
/// design — one message doing both jobs by convention — rather than treated as a gap needing a second
/// field added across every call site for a split that was never actually missing in practice.
/// </remarks>
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

    /// <summary>
    /// The message — shown to the user as-is in both the CLI and WinUI, and also what goes to the log.
    /// Write it in plain language for that reason; there is no second, more-technical field it hides
    /// behind.
    /// </summary>
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
