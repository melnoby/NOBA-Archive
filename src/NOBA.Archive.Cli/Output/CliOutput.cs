using System.Text.Json;
using System.Text.Json.Serialization;

namespace NOBA.Archive.Cli.Output;

/// <summary>
/// Keeps the two output channels honest: machine-readable JSON on stdout, human text on stderr, so a
/// pipe stays clean no matter what the tool has to say.
/// </summary>
public sealed class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _json;

    public CliOutput(TextWriter stdout, TextWriter stderr, bool json)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        _stdout = stdout;
        _stderr = stderr;
        _json = json;
    }

    public bool IsJson => _json;

    /// <summary>The single machine-readable document. Only ever written to stdout, and only in JSON mode.</summary>
    public void WriteResult<T>(T payload)
    {
        if (_json)
        {
            _stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    /// <summary>Human text. Goes to stdout in text mode and to stderr in JSON mode.</summary>
    public void WriteLine(string text)
    {
        if (_json)
        {
            _stderr.WriteLine(text);
        }
        else
        {
            _stdout.WriteLine(text);
        }
    }

    public void WriteError(string text) => _stderr.WriteLine(text);
}

public sealed record CliErrorPayload(string Error, string Detail, ExitCode ExitCode);

public sealed record CliFormatPayload(
    string Id,
    string DisplayName,
    string PrimaryExtension,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Capabilities);

public sealed record CliFormatsPayload(string Product, string Version, IReadOnlyList<CliFormatPayload> Formats);

public sealed record CliVersionPayload(string Product, string Version, string Runtime, string Phase, string SourceDrop);

public sealed record CliEntryPayload(
    string Path,
    bool IsDirectory,
    long UncompressedSize,
    long CompressedSize,
    DateTimeOffset? Modified,
    bool IsEncrypted,
    string? BlockedReason);

public sealed record CliListPayload(
    string Archive,
    string Format,
    int Count,
    IReadOnlyList<CliEntryPayload> Entries,
    string? NameEncoding = null,
    bool NameEncodingIsGuess = false);

public sealed record CliSearchPayload(
    string Archive,
    string Format,
    long EntriesScanned,
    int MatchCount,
    IReadOnlyList<CliEntryPayload> Matches);

public sealed record CliInfoPayload(
    string Archive,
    string Format,
    long EntryCount,
    long FileCount,
    long DirectoryCount,
    long EncryptedEntryCount,
    long TotalUncompressedSize,
    long TotalCompressedSize,
    double? CompressionRatio);

public sealed record CliFindingPayload(string EntryPath, string Message, bool IsError);

public sealed record CliTestPayload(
    string Archive,
    string Status,
    long EntriesTested,
    IReadOnlyList<CliFindingPayload> Findings);

public sealed record CliBlockedPayload(string RawPath, string Reason, string Detail);

public sealed record CliExtractPayload(
    string OperationId,
    string Archive,
    string Destination,
    long EntriesWritten,
    long EntriesSkipped,
    long DirectoriesCreated,
    long BytesWritten,
    IReadOnlyList<CliBlockedPayload> Blocked);

public sealed record CliCreatePayload(
    string OperationId,
    string ArchivePath,
    string Format,
    long EntriesAdded,
    long BytesRead,
    long ArchiveSize);

public sealed record CliManagePayload(
    string OperationId,
    string ArchivePath,
    long EntriesKept,
    long EntriesRemoved,
    long EntriesRenamed,
    long EntriesAdded,
    long ArchiveSize);
