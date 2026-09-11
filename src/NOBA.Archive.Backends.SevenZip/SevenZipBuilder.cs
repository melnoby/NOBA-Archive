using NOBA.Archive.Backends.SevenZip.Interop;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Backends.SevenZip;

/// <summary>
/// Writes a 7Z archive through <c>IOutArchive</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is described first and compressed last.</b> 7-Zip's writer takes the whole item list
/// in one <c>UpdateItems</c> call and then pulls each entry's properties and bytes in whatever order
/// its compressor wants — a solid block is built from several files at once, so there is no "current
/// entry" to stream into. <c>AddAsync</c> therefore records an item; <c>CompleteAsync</c> is where the
/// archive is actually produced.
/// </para>
/// <para>
/// Nothing is written to the staging stream until <c>CompleteAsync</c>, which means an abandoned build
/// leaves an empty file rather than a truncated archive — and the publisher then discards it. That is
/// the same guarantee the ZIP path gives by writing its central directory last, arrived at differently.
/// </para>
/// <para>
/// ADR-0005 is unaffected in the same way it is during extraction: the only paths 7-Zip is given are
/// the names entries will carry <em>inside</em> the archive. It is handed one output stream, opened by
/// the publisher, and never learns where that stream leads.
/// </para>
/// </remarks>
#pragma warning disable CA2213 // The library and the staging stream are both owned by the caller.
internal sealed class SevenZipBuilder : IArchiveBuilder
{
    private readonly SevenZipLibrary _library;
    private readonly Stream _output;
    private readonly CompressionOptions _options;
    private readonly Password? _password;
    private readonly List<UpdateItem> _items = [];

    private bool _completed;
    private bool _disposed;

    public SevenZipBuilder(
        SevenZipLibrary library,
        Stream output,
        CompressionOptions options,
        Password? password)
    {
        _library = library;
        _output = output;
        _options = options;
        _password = password;
    }

    public ArchiveFormat Format => ArchiveFormat.SevenZip;

    public ArchiveCapabilities Capabilities =>
        ArchiveCapabilities.Create
        | ArchiveCapabilities.AddEntries
        | ArchiveCapabilities.Encrypt
        | ArchiveCapabilities.EncryptFileNames
        | ArchiveCapabilities.SolidCompression;

    public ValueTask AddAsync(
        ArchivePath entryPath,
        EntryContentSource content,
        DateTimeOffset? modified,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        _items.Add(new UpdateItem(
            entryPath.Value,
            IsDirectory: false,
            content.Length,
            modified,
            content.OpenAsync));

        // Reported now rather than as bytes arrive. The compressor reads entries in its own order and
        // interleaves them inside a solid block, so per-entry byte progress during compression would
        // be a fiction; the honest signal here is "this entry is queued".
        bytesProgress?.Report(content.Length);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddDirectoryAsync(ArchivePath entryPath, DateTimeOffset? modified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        _items.Add(new UpdateItem(entryPath.Value, IsDirectory: true, 0, modified, Open: null));
        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
        {
            return;
        }

        if (_items.Count == 0)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "No entries were added, so there is nothing to compress."));
        }

        if (!_output.CanSeek)
        {
            // 7z writes its header after the packed streams and seeks back to finish it. Failing here
            // names the reason; letting UpdateItems fail would produce an HRESULT and no explanation.
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "The 7Z writer needs a seekable output stream: the format's header is written last."));
        }

        var revealed = _password is null ? null : Reveal(_password);
        Exception? failure = null;

        await Task.Run(
            () =>
            {
                using var archive = new OutArchive(
                    _library.CreateObject(Iid.Handler(SevenZipArchiveProvider.SevenZipFormatIdValue), Iid.IOutArchive));

                var hr = archive.SetProperties(BuildProperties());
                if (hr != Hresult.S_OK)
                {
                    failure = new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.InvalidConfiguration,
                        $"The 7Z handler rejected the compression settings (0x{hr:X8})."));
                    return;
                }

                using var output = new OutStreamWrapper(_output);
                using var callback = new UpdateCallback(_items, revealed, cancellationToken);

                hr = archive.UpdateItems(output, (uint)_items.Count, callback);

                // The callback's own failure comes first: it is the specific one, and an HRESULT is
                // usually just its echo.
                failure = callback.Failure;
                if (failure is null && hr != Hresult.S_OK)
                {
                    failure = new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.BackendFailure,
                        $"The archive could not be written (0x{hr:X8})."));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (revealed is not null)
        {
            Array.Clear(revealed);
        }

        if (failure is not null)
        {
            throw failure;
        }

        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>
    /// Maps this product's options onto 7-Zip's own property names — the same ones its command line
    /// takes, so <c>x=9</c> here is <c>-mx=9</c> there.
    /// </summary>
    /// <remarks>
    /// Only settings the format really supports are emitted. Anything the caller asked for that this
    /// backend cannot honour has already been refused by the capability gate; silently dropping it
    /// here would be the failure mode the gate exists to prevent.
    /// </remarks>
    private List<SevenZipProperty> BuildProperties()
    {
        var properties = new List<SevenZipProperty>
        {
            SevenZipProperty.Number("x", (uint)_options.Level.Value),
            // "on"/"off" rather than a VT_BOOL: the boolean form is accepted by the handler and then
            // ignored - an archive requested non-solid came back solid, which is the quiet kind of
            // wrong. This is the same spelling 7-Zip's own -ms= switch uses.
            SevenZipProperty.Text("s", _options.Solid ? "on" : "off"),
        };

        if (_options.Method is { } method)
        {
            properties.Add(SevenZipProperty.Text("0", MethodName(method)));
        }

        if (_options.DictionarySize is { } dictionary)
        {
            properties.Add(SevenZipProperty.Text(
                "d",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{dictionary.Bytes / 1024}k")));
        }

        if (_options.ThreadCount is { } threads and > 0)
        {
            properties.Add(SevenZipProperty.Number("mt", (uint)threads));
        }

        if (_options.EncryptFileNames)
        {
            // Only meaningful alongside a password, and the caller cannot get here without one: the
            // capability gate requires EncryptFileNames, and RequiresPassword is what obtains it.
            properties.Add(SevenZipProperty.Flag("he", true));
        }

        return properties;
    }

    private static string MethodName(CompressionMethod method) => method switch
    {
        CompressionMethod.Store => "Copy",
        CompressionMethod.Lzma => "LZMA",
        CompressionMethod.Lzma2 => "LZMA2",
        CompressionMethod.Ppmd => "PPMD",
        CompressionMethod.BZip2 => "BZip2",
        _ => throw new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.UnsupportedOperation,
            $"7Z cannot be written with the {method} method.")),
    };

    private static char[] Reveal(Password password) =>
        password.Use(static bytes => System.Text.Encoding.UTF8.GetChars(bytes.ToArray()));

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _password?.Dispose();
        _items.Clear();
        return ValueTask.CompletedTask;
    }
}
#pragma warning restore CA2213
