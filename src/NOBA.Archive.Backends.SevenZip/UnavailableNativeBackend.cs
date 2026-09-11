using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;

namespace NOBA.Archive.Backends.SevenZip;

/// <summary>
/// Stands in for the native backend when <c>7z.dll</c> could not be loaded.
/// </summary>
/// <remarks>
/// <para>
/// The alternative — registering nothing — loses the reason. A user whose 7Z file will not open then
/// gets "no provider supports this format", which is true and useless: it does not say that the format
/// is normally supported, that a native library is required, or where we looked for it.
/// </para>
/// <para>
/// This declares the formats it would have handled with <see cref="ArchiveCapabilities.None"/>, so the
/// capability gate still refuses cleanly and nothing is silently downgraded, while the search
/// diagnostic survives to the point where somebody reads an error.
/// </para>
/// </remarks>
public sealed class UnavailableNativeBackend : IArchiveProvider
{
    private readonly string _diagnostic;

    public UnavailableNativeBackend(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        _diagnostic = diagnostic;
    }

    public string ProviderId => "sevenzip-native-unavailable";

    /// <summary>What was tried, and where. Shown verbatim in the error.</summary>
    public string Diagnostic => _diagnostic;

    public IReadOnlyList<ArchiveFormat> SupportedFormats => [ArchiveFormat.SevenZip, ArchiveFormat.Rar];

    public ArchiveCapabilities GetCapabilities(ArchiveFormat format) => ArchiveCapabilities.None;

    public int PriorityFor(ArchiveFormat format) => int.MinValue;

    public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
        ValueTask.FromResult(FormatDetection.None);

    public ValueTask<IArchiveSession> OpenAsync(
        ArchiveSource source,
        OpenOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken) =>
        throw Unavailable();

    public ValueTask<IArchiveBuilder> CreateAsync(
        Stream output,
        CompressionOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken) =>
        throw Unavailable();

    private ArchiveOperationException Unavailable() =>
        new(ArchiveError.Create(
            ArchiveErrorCode.UnsupportedFormat,
            "7Z and RAR need the 7-Zip native library, which is not available in this installation. " +
            $"Looked in:{Environment.NewLine}{_diagnostic}"));
}
