using Microsoft.Extensions.Logging;

namespace NOBA.Archive.Infrastructure.FileSystem;

/// <summary>
/// Source-generated log messages. Using <c>[LoggerMessage]</c> rather than the extension methods keeps
/// argument evaluation off the hot path when a level is disabled — which matters here, because this
/// class runs once per entry and an archive can hold a million of them.
/// </summary>
public sealed partial class ExtractionWriter
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Renamed '{Original}' to '{Replacement}' ({Reason}).")]
    private static partial void LogSanitized(ILogger logger, string original, string replacement, string reason);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Post-open handle verification is unavailable on this platform ({Platform}); extraction proceeds with canonical-path and reparse-point checks only.")]
    private static partial void LogReducedAssurance(ILogger logger, string platform);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Debug,
        Message = "Could not apply {Aspect} for {Path}.")]
    private static partial void LogMetadataSkipped(ILogger logger, string aspect, string path, Exception exception);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Could not propagate Mark of the Web to {Path}. The file is extracted but unmarked.")]
    private static partial void LogMarkOfTheWebFailed(ILogger logger, string path, Exception exception);
}
