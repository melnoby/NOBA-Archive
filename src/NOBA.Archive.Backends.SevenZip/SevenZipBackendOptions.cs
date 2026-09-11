namespace NOBA.Archive.Backends.SevenZip;

/// <summary>Configuration for the native backend.</summary>
/// <remarks>
/// Defined here rather than on the engine's options so the container can construct
/// <see cref="SevenZipBackend"/> by type. A factory registration cannot be deduplicated by
/// <c>TryAddEnumerable</c> — the descriptor's implementation type is only the interface — and a host
/// that calls <c>AddNobaArchiveEngine</c> twice would then load the native library twice.
/// </remarks>
public sealed class SevenZipBackendOptions
{
    /// <summary>
    /// Where to find the native library: a directory, or the file itself. Null uses the search order.
    /// </summary>
    public string? LibraryPath { get; set; }
}
