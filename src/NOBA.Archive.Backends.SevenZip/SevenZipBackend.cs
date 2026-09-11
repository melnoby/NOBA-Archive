using Microsoft.Extensions.Options;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;

namespace NOBA.Archive.Backends.SevenZip;

/// <summary>
/// The registered native backend: a real one when <c>7z.dll</c> loads, an explanatory stand-in when it
/// does not.
/// </summary>
/// <remarks>
/// <para>
/// One concrete type so the container can register it once and deduplicate it, and one place where the
/// "is the native library usable?" question is answered. The alternative — a factory registration —
/// cannot be deduplicated by <c>TryAddEnumerable</c>, which matters because a host may call
/// <c>AddNobaArchiveEngine</c> more than once and would otherwise load the library twice.
/// </para>
/// <para>
/// Absence is a supported state, not an error. A machine with no 7-Zip installed still opens ZIPs; it
/// loses 7Z and RAR, and says so with the list of places that were searched.
/// </para>
/// </remarks>
public sealed class SevenZipBackend : IArchiveProvider, IDisposable
{
    private readonly IArchiveProvider _inner;

    public SevenZipBackend(IFormatDetector detector, IOptions<SevenZipBackendOptions> options)
        : this(detector, options?.Value.LibraryPath)
    {
    }

    public SevenZipBackend(IFormatDetector detector, string? configuredLibraryPath)
    {
        ArgumentNullException.ThrowIfNull(detector);

        var located = SevenZipLibraryLocator.Locate(configuredLibraryPath);
        if (located is null)
        {
            _inner = new UnavailableNativeBackend(SevenZipLibraryLocator.DescribeSearch(configuredLibraryPath));
            return;
        }

        SevenZipLibraryLocator.HardenSearchPath();

        try
        {
            _inner = new SevenZipArchiveProvider(new Interop.SevenZipLibrary(located.Path), detector);
            LibraryPath = located.Path;
            Origin = located.Origin;
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                      or BadImageFormatException
                                      or EntryPointNotFoundException
                                      or FileNotFoundException)
        {
            // Present but unloadable — an x86 library under an x64 process is the usual cause. Same
            // outcome as missing, with the reason kept.
            _inner = new UnavailableNativeBackend($"{located.Path}: {ex.Message}");
        }
    }

    /// <summary>True when 7Z and RAR are actually available in this process.</summary>
    public bool IsAvailable => _inner is SevenZipArchiveProvider;

    public string? LibraryPath { get; }

    public NativeLibraryOrigin Origin { get; } = NativeLibraryOrigin.None;

    public string ProviderId => _inner.ProviderId;

    public IReadOnlyList<ArchiveFormat> SupportedFormats => _inner.SupportedFormats;

    public ArchiveCapabilities GetCapabilities(ArchiveFormat format) => _inner.GetCapabilities(format);

    public int PriorityFor(ArchiveFormat format) => _inner.PriorityFor(format);

    public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
        _inner.DetectAsync(probe, fileName, cancellationToken);

    public ValueTask<IArchiveSession> OpenAsync(
        ArchiveSource source,
        OpenOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken) =>
        _inner.OpenAsync(source, options, passwords, cancellationToken);

    public ValueTask<IArchiveBuilder> CreateAsync(
        Stream output,
        CompressionOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken) =>
        _inner.CreateAsync(output, options, passwords, cancellationToken);

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
