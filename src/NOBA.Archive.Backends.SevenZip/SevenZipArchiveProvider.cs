using System.Runtime.CompilerServices;
using NOBA.Archive.Backends.SevenZip.Interop;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;

namespace NOBA.Archive.Backends.SevenZip;

/// <summary>
/// The native backend: 7Z, RAR and encrypted ZIP through <c>7z.dll</c>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0005 survives the interop boundary intact, and that is not an accident of implementation — it
/// is a property of 7-Zip's own contract. <c>IArchiveExtractCallback::GetStream</c> asks "where do I
/// put entry N?" and accepts only an <c>ISequentialOutStream</c> in reply. There is no path anywhere in
/// the signature, so the native backend is structurally incapable of choosing a destination. The
/// interop spike measured this directly: scenario 7 confirmed the backend created zero files, on both
/// Linux and Windows.
/// </para>
/// <para>
/// RAR is read-only and always will be (ADR-0004). The unRAR licence forbids using its source to build
/// a RAR-compatible compressor, so <see cref="ArchiveCapabilities.Create"/> is absent for RAR by law
/// rather than by effort, and no amount of later work changes that.
/// </para>
/// </remarks>
public sealed class SevenZipArchiveProvider : IArchiveProvider, IDisposable
{
    /// <summary>7-Zip's own format ids, from <c>Guids.txt</c>.</summary>
    private const byte SevenZipFormatId = 0x07;
    private const byte ZipFormatId = 0x01;
    private const byte RarFormatId = 0x03;
    private const byte Rar5FormatId = 0xCC;

    private readonly SevenZipLibrary _library;
    private readonly IFormatDetector _detector;

    public SevenZipArchiveProvider(SevenZipLibrary library, IFormatDetector detector)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(detector);
        _library = library;
        _detector = detector;
    }

    public string ProviderId => "sevenzip-native";

    public string LibraryPath => _library.LibraryPath;

    public IReadOnlyList<ArchiveFormat> SupportedFormats =>
        [ArchiveFormat.SevenZip, ArchiveFormat.Rar, ArchiveFormat.Zip];

    public ArchiveCapabilities GetCapabilities(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (format == ArchiveFormat.SevenZip)
        {
            // Create and AddEntries mean "produce a new archive with these entries in it". Updating an
            // existing 7z is not implemented, so RemoveEntries and RenameEntries stay absent - the
            // capability set is the product's promise, and a promise it cannot keep is worse than a
            // missing feature. SplitVolumes is absent for the same reason: writing volumes needs
            // IArchiveUpdateCallback2, which this backend does not implement.
            return ArchiveCapabilities.ReadOnlySet
                 | ArchiveCapabilities.Create
                 | ArchiveCapabilities.AddEntries
                 | ArchiveCapabilities.Encrypt
                 | ArchiveCapabilities.EncryptFileNames
                 | ArchiveCapabilities.SolidCompression
                 | ArchiveCapabilities.ReadSplitVolumes
                 | ArchiveCapabilities.RandomAccessRead

                 // 7z stores a CRC32 per entry, so testing verifies content and not just structure.
                 | ArchiveCapabilities.ContentIntegrityCheck;
        }

        if (format == ArchiveFormat.Rar)
        {
            // List, extract, test. Never Create - see ADR-0004.
            return ArchiveCapabilities.ReadOnlySet
                 | ArchiveCapabilities.SolidCompression
                 | ArchiveCapabilities.ReadSplitVolumes
                 | ArchiveCapabilities.RecoveryRecord

                 // RAR stores a CRC32 (RAR4) or BLAKE2sp (RAR5) per file.
                 | ArchiveCapabilities.ContentIntegrityCheck;
        }

        if (format == ArchiveFormat.Zip)
        {
            return ArchiveCapabilities.ReadOnlySet
                 | ArchiveCapabilities.RandomAccessRead
                 | ArchiveCapabilities.ContentIntegrityCheck;
        }

        return ArchiveCapabilities.None;
    }

    /// <summary>
    /// Outranks the managed backend for 7Z and RAR, and deliberately loses to it for ZIP.
    /// </summary>
    /// <remarks>
    /// The managed ZIP path has no native dependency and is now the better-evidenced of the two — 59
    /// real archives agreed with 7-Zip on it. Ranking the native backend below it for ZIP keeps that
    /// evidence meaningful. The registry still selects this provider for a ZIP when the caller needs a
    /// capability the managed one does not advertise, which is how an encrypted ZIP finds its way here
    /// without changing the default for everything else.
    /// </remarks>
    public int PriorityFor(ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (format == ArchiveFormat.SevenZip || format == ArchiveFormat.Rar)
        {
            return 100;
        }

        return format == ArchiveFormat.Zip ? 5 : int.MinValue;
    }

    public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
        _detector.DetectAsync(probe, fileName, cancellationToken);

    public async ValueTask<IArchiveSession> OpenAsync(
        ArchiveSource source,
        OpenOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwords);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = source.Stream
            ?? throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "This provider reads from a caller-owned stream; open the file through IFileSystem and " +
                "pass ArchiveSource.FromStream.",
                source.FilePath));

        // No default. This provider fronts three handlers that share one entry point, and a wrong guess
        // does not fail as "wrong handler" - 7-Zip answers S_FALSE, which used to be read as "encrypted
        // headers", so a perfectly good RAR came back asking for a password it does not have. The caller
        // has already resolved a format (that is how it chose this provider); it must say which.
        var format = options.ForceFormat
            ?? throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.InvalidConfiguration,
                "The native backend serves several formats and will not guess between them: set " +
                "OpenOptions.ForceFormat to the format the caller resolved.",
                source.DisplayName));

        return await SevenZipSession
            .OpenAsync(_library, format, FormatIdFor(format), stream, source.DisplayName, passwords, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IArchiveBuilder> CreateAsync(
        Stream output,
        CompressionOptions options,
        IPasswordProvider passwords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwords);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Format != ArchiveFormat.SevenZip)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedOperation,
                options.Format == ArchiveFormat.Rar
                    ? "NOBA Archive cannot create RAR archives, and never will: the unRAR licence forbids it."
                    : $"The native backend cannot create {options.Format.DisplayName} archives."));
        }

        if (options.VolumeSize is not null)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.UnsupportedOperation,
                "This build cannot write split volumes. The capability is not advertised, so a caller " +
                "that reaches here asked for it without checking."));
        }

        Password? password = null;
        if (options.RequiresPassword)
        {
            // Asked for here rather than at CompleteAsync: the user chose encryption when they set up
            // the archive, so this is where the prompt belongs, and a headless host fails immediately
            // instead of after compressing everything.
            password = await passwords
                .GetPasswordAsync(new PasswordRequest("new archive", null, IsRetry: false), cancellationToken)
                .ConfigureAwait(false);

            if (password is null)
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.PasswordRequired,
                    "Encryption was requested but no password was supplied."));
            }
        }

        return new SevenZipBuilder(_library, output, options, password);
    }

    internal static byte FormatIdFor(ArchiveFormat format)
    {
        if (format == ArchiveFormat.SevenZip)
        {
            return SevenZipFormatId;
        }

        if (format == ArchiveFormat.Zip)
        {
            return ZipFormatId;
        }

        if (format == ArchiveFormat.Rar)
        {
            // Rar5 first; the session falls back to the RAR4 handler when that fails to open.
            return Rar5FormatId;
        }

        throw new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.UnsupportedFormat,
            $"The native backend has no handler for {format.DisplayName}."));
    }

    internal static byte LegacyRarFormatId => RarFormatId;

    internal static byte SevenZipFormatIdValue => SevenZipFormatId;

    public void Dispose() => _library.Dispose();
}

/// <summary>An archive opened through the native backend.</summary>
internal sealed class SevenZipSession : IArchiveSession
{
    private readonly InArchive _archive;
    private readonly InStreamWrapper _stream;
    private readonly OpenCallback? _openCallback;
    private readonly IReadOnlyList<ArchiveItem> _items;
    private readonly IPasswordProvider _passwords;
    private readonly string? _displayName;
    private Password? _password;
    private bool _disposed;

    private SevenZipSession(
        ArchiveFormat format,
        InArchive archive,
        InStreamWrapper stream,
        OpenCallback? openCallback,
        IReadOnlyList<ArchiveItem> items,
        IPasswordProvider passwords,
        string? displayName,
        Password? password)
    {
        _password = password;
        Format = format;
        _archive = archive;
        _stream = stream;
        _openCallback = openCallback;
        _items = items;
        _passwords = passwords;
        _displayName = displayName;

        long files = 0, directories = 0, encrypted = 0, uncompressed = 0, compressed = 0;
        foreach (var item in items)
        {
            compressed += (long)item.PackedSize;

            if (item.IsDirectory)
            {
                directories++;
            }
            else
            {
                files++;
                uncompressed += (long)item.Size;
            }

            if (item.IsEncrypted)
            {
                encrypted++;
            }
        }

        Statistics = new ArchiveStatistics
        {
            EntryCount = items.Count,
            FileCount = files,
            DirectoryCount = directories,
            EncryptedEntryCount = encrypted,
            TotalUncompressedSize = ByteSize.FromBytes(uncompressed),

            // Without this the archive-wide expansion-ratio guard silently does nothing on 7Z and RAR:
            // it skips when the compressed total is zero, and the compressed total was never read. The
            // guard failed open, which is the failure mode that leaves no trace.
            TotalCompressedSize = ByteSize.FromBytes(compressed),
        };
    }

    public ArchiveFormat Format { get; }

    public ArchiveCapabilities Capabilities => ArchiveCapabilities.ReadOnlySet;

    public ArchiveStatistics Statistics { get; }

    /// <summary>
    /// 7-Zip hands back UTF-16 entry names, so there is no encoding to guess and nothing to report —
    /// unlike the managed ZIP path, where the encoding is genuinely unknowable from the file.
    /// </summary>
    public EntryNameEncodingInfo? NameEncoding => null;

    /// <summary>
    /// The backend decodes a solid block as a unit, so there is no safe point to stop inside one.
    /// Saying so lets the UI disable the pause button rather than offering one that does nothing.
    /// </summary>
    public bool SupportsPause => false;

    public static async ValueTask<SevenZipSession> OpenAsync(
        SevenZipLibrary library,
        ArchiveFormat format,
        byte formatId,
        Stream stream,
        string? displayName,
        IPasswordProvider passwords,
        CancellationToken cancellationToken)
    {
        var wrapper = new InStreamWrapper(stream);
        OpenCallback? callback = null;
        Password? password = null;
        InArchive? archive = null;

        try
        {
            archive = new InArchive(library.CreateArchiveHandler(Iid.Handler(formatId)));
            var hr = archive.Open(wrapper);

            if (hr != Hresult.S_OK && format == ArchiveFormat.Rar)
            {
                // RAR4 and RAR5 are separate handlers behind one extension, and only trying tells you.
                archive.Dispose();
                stream.Position = 0;
                archive = new InArchive(library.CreateArchiveHandler(
                    Iid.Handler(SevenZipArchiveProvider.LegacyRarFormatId)));
                hr = archive.Open(wrapper);
            }

            if (hr == Hresult.S_FALSE && !CarriesSignatureFor(format, stream))
            {
                // S_FALSE is 7-Zip's answer to "this is not my format" as well as to "the header is
                // encrypted", and the two demand opposite responses. Without this check a file that is
                // not an archive at all was reported as encrypted and the user was asked for a password
                // that does not exist - found on a real .rar that turned out to be an NTFS MFT fragment.
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.CorruptData,
                    $"This file does not carry a {format.DisplayName} signature, so it is not a " +
                    $"{format.DisplayName} archive however it is named.",
                    displayName));
            }

            if (hr == Hresult.S_FALSE)
            {
                // An archive with encrypted headers cannot even be listed without a password, and
                // 7-Zip says so by refusing the Open rather than by asking (spike finding F2). Only
                // now is a prompt justified - asking up front would train users to type a password
                // into every archive they open.
                password = await passwords
                    .GetPasswordAsync(new PasswordRequest(displayName ?? "archive", null, IsRetry: false), cancellationToken)
                    .ConfigureAwait(false);

                if (password is not null)
                {
                    archive.Dispose();
                    stream.Position = 0;
                    callback = new OpenCallback(Reveal(password));
                    archive = new InArchive(library.CreateArchiveHandler(Iid.Handler(formatId)));
                    hr = archive.Open(wrapper, callback);
                }
            }

            if (hr != Hresult.S_OK)
            {
                var needsPassword = hr == Hresult.S_FALSE;
                throw new ArchiveOperationException(ArchiveError.Create(
                    needsPassword ? ArchiveErrorCode.PasswordRequired : ArchiveErrorCode.CorruptData,
                    needsPassword
                        ? "This archive's file list is encrypted, and the password supplied did not open it."
                        : $"The archive could not be opened (0x{hr:X8}).",
                    displayName));
            }

            var items = archive.ListItems();
            var session = new SevenZipSession(format, archive, wrapper, callback, items, passwords, displayName, password);
            archive = null;
            callback = null;
            password = null;
            return session;
        }
        finally
        {
            archive?.Dispose();
            callback?.Dispose();
            password?.Dispose();
            if (archive is not null || callback is not null || password is not null)
            {
                wrapper.Dispose();
            }
        }
    }

    /// <summary>
    /// Does the stream actually begin with this format's signature?
    /// </summary>
    /// <remarks>
    /// Deliberately a local check rather than a call into the detector: this project references Core
    /// only (so that a backend structurally cannot reach a filesystem), and the question here is
    /// narrower anyway — not "what is this file" but "did the handler we just used have any business
    /// with it". Encrypted headers do not hide the signature in either 7Z or RAR; the whole container
    /// is still identifiable, which is what makes the distinction decidable at all.
    /// </remarks>
    private static bool CarriesSignatureFor(ArchiveFormat format, Stream stream)
    {
        if (!stream.CanSeek)
        {
            // Cannot look without consuming. Assume the caller resolved the format correctly rather
            // than refusing an archive we might well be able to open.
            return true;
        }

        Span<byte> header = stackalloc byte[8];
        var position = stream.Position;
        stream.Position = 0;
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = position;

        ReadOnlySpan<byte> signature =
            format == ArchiveFormat.SevenZip ? [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C] :
            format == ArchiveFormat.Rar ? [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07] :
            format == ArchiveFormat.Zip ? [0x50, 0x4B] :
            default;

        // An unknown format has no signature to check, so it cannot be ruled out on this evidence.
        return signature.IsEmpty || (read >= signature.Length && header[..signature.Length].SequenceEqual(signature));
    }

    /// <summary>
    /// Materialises the plaintext for exactly as long as the BSTR handoff needs it.
    /// </summary>
    /// <remarks>
    /// <see cref="Password"/> will not produce a string, which is correct — strings cannot be wiped.
    /// The characters live in an array the callback clears on disposal.
    /// </remarks>
    private static char[] Reveal(Password password) =>
        password.Use(static bytes => System.Text.Encoding.UTF8.GetChars(bytes.ToArray()));

    public async IAsyncEnumerable<ArchiveEntry> ListEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Names come off the archive untrusted, exactly as in the managed path. Validation is the
            // security layer's job; the provider only reports what it read.
            var accepted = ArchivePath.TryCreate(item.Path, out var path, out var rejection);

            yield return new ArchiveEntry
            {
                RawPath = item.Path,
                Path = accepted ? path : null,
                Rejection = accepted ? null : rejection,
                Kind = item.IsDirectory ? EntryKind.Directory : EntryKind.File,
                UncompressedSize = ByteSize.FromBytes((long)item.Size),
                CompressedSize = ByteSize.FromBytes((long)item.PackedSize),
                IsEncrypted = item.IsEncrypted,

                // Without this every 7Z and RAR entry extracted with the time it was written rather
                // than the time it was archived — silently, because a plausible timestamp looks like
                // a real one. The managed ZIP path has carried this since Phase 2.
                Modified = item.Modified,
            };

            await Task.Yield();
        }
    }

    public async ValueTask ExtractToAsync(
        ArchiveEntry entry,
        Stream destination,
        IProgress<long>? bytesProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        var index = IndexOf(entry);
        if (entry.IsEncrypted && _password is null)
        {
            _password = await _passwords
                .GetPasswordAsync(
                    new PasswordRequest(_displayName ?? "archive", entry.DisplayPath, IsRetry: false),
                    cancellationToken)
                .ConfigureAwait(false);

            if (_password is null)
            {
                throw new ArchiveOperationException(ArchiveError.Create(
                    ArchiveErrorCode.PasswordRequired,
                    $"'{entry.DisplayPath}' is encrypted and no password was supplied.",
                    entry.RawPath));
            }
        }

        using var callback = new ExtractCallback(
            i => i == index ? destination : null,
            _password is null ? null : Reveal(_password));

        var hr = _archive.Extract(callback, [index]);
        if (hr != Hresult.S_OK)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                ArchiveErrorCode.CorruptData,
                $"Entry '{entry.DisplayPath}' could not be extracted (0x{hr:X8}).",
                entry.RawPath));
        }

        if (callback.Results.TryGetValue(index, out var result) && result != OperationResult.Ok)
        {
            throw new ArchiveOperationException(ArchiveError.Create(
                result == OperationResult.CrcError ? ArchiveErrorCode.CorruptData : ArchiveErrorCode.UnsupportedOperation,
                $"Entry '{entry.DisplayPath}': {OperationResult.Describe(result)}.",
                entry.RawPath));
        }
    }

    /// <summary>
    /// One pass over the archive for the whole selection — the fix for R18.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A solid block decodes as a unit, so the old per-entry path re-decoded the block once per entry:
    /// measured at 13.1 s for 3200 entries against 7-Zip's 1.2 s, growing ×7.7 for ×4 entries. One
    /// <c>Extract</c> call decodes each block once.
    /// </para>
    /// <para>
    /// ADR-0005 is untouched, and visibly so: this method never sees a path. It asks the caller for a
    /// stream per entry and is told null for the ones it may not have, which is exactly how a refused
    /// entry is declined without the backend learning that it happened.
    /// </para>
    /// <para>
    /// The native call runs on a thread-pool thread on purpose. 7-Zip's callbacks are synchronous and
    /// must block on the caller's async work; doing that on a UI thread would deadlock, and doing it
    /// where no synchronization context exists cannot.
    /// </para>
    /// </remarks>
    public bool SupportsBatchExtraction => true;

    public async ValueTask ExtractManyAsync(
        IReadOnlyList<ArchiveEntry> entries,
        Func<ArchiveEntry, CancellationToken, ValueTask<Stream?>> open,
        Func<ArchiveEntry, bool, CancellationToken, ValueTask> close,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);
        cancellationToken.ThrowIfCancellationRequested();

        if (entries.Count == 0)
        {
            return;
        }

        var byIndex = new Dictionary<uint, ArchiveEntry>();
        foreach (var entry in entries)
        {
            byIndex[IndexOf(entry)] = entry;
        }

        var indices = byIndex.Keys.OrderBy(static i => i).ToArray();

        if (byIndex.Values.Any(static e => e.IsEncrypted) && _password is null)
        {
            _password = await _passwords
                .GetPasswordAsync(new PasswordRequest(_displayName ?? "archive", null, IsRetry: false), cancellationToken)
                .ConfigureAwait(false);
        }

        var revealed = _password is null ? null : Reveal(_password);
        Exception? callbackFailure = null;

        await Task.Run(
            () =>
            {
                using var callback = new ExtractCallback(
                    index =>
                    {
                        if (callbackFailure is not null || !byIndex.TryGetValue(index, out var entry))
                        {
                            return null;
                        }

                        try
                        {
                            return open(entry, cancellationToken).AsTask().GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException ex)
                        {
                            // Cancellation is the one failure that must stop the archive rather than
                            // merely stop writing to it. Declining every remaining entry would keep
                            // the bytes off the disk, but 7-Zip would still decode the rest of a
                            // 950 MB solid block with nowhere to put it - seconds of work after the
                            // user pressed Cancel, with the operation still Running. Rethrowing lets
                            // the callback answer with a failure HRESULT, which ends Extract here.
                            callbackFailure ??= ex;
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // A throw here would unwind through native frames. Remember it, decline
                            // the entry, and rethrow once 7-Zip has finished unwinding its own loop.
                            callbackFailure ??= ex;
                            return null;
                        }
                    },
                    revealed,
                    (index, result) =>
                    {
                        if (!byIndex.TryGetValue(index, out var entry))
                        {
                            return;
                        }

                        try
                        {
                            close(entry, result == OperationResult.Ok, cancellationToken)
                                .AsTask().GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            callbackFailure ??= ex;
                        }

                        if (result == OperationResult.Ok)
                        {
                            return;
                        }

                        // 7-Zip reports a per-entry failure here and still returns S_OK from Extract:
                        // its own CLI prints the errors and exits non-zero. Reporting it only through
                        // close(ok: false) discarded it — the batch path returned success having
                        // written nothing, while the single-entry path threw for the same archive.
                        // Found on a real RAR whose codec this build of 7-Zip does not carry.
                        callbackFailure ??= new ArchiveOperationException(ArchiveError.Create(
                            result == OperationResult.CrcError
                                ? ArchiveErrorCode.CorruptData
                                : ArchiveErrorCode.UnsupportedOperation,
                            $"Entry '{entry.DisplayPath}': {OperationResult.Describe(result)}.",
                            entry.RawPath));
                    });

                var hr = _archive.Extract(callback, indices);
                if (hr != Hresult.S_OK && callbackFailure is null)
                {
                    // A cancelled extraction is not a corrupt one. The abort reaches 7-Zip as a failed
                    // write callback, so the HRESULT alone cannot tell the two apart - and reporting
                    // "the archive could not be extracted" to someone who just pressed Cancel would be
                    // a lie about their own archive.
                    cancellationToken.ThrowIfCancellationRequested();

                    callbackFailure = new ArchiveOperationException(ArchiveError.Create(
                        ArchiveErrorCode.CorruptData,
                        $"The archive could not be extracted (0x{hr:X8}).",
                        _displayName));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (revealed is not null)
        {
            Array.Clear(revealed);
        }

        if (callbackFailure is not null)
        {
            throw callbackFailure;
        }
    }

    public async ValueTask<ArchiveTestResult> TestAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An archive whose entries are encrypted but whose header is not opens without a password, so
        // by the time anyone tests it there is still none in hand. Extraction asks at this point;
        // testing did not, and reported "unsupported method" for every entry - 7-Zip's answer when it
        // cannot build a decoder because no key was supplied. Found by testing an archive this build
        // had just created.
        if (_password is null && _items.Any(static i => i.IsEncrypted))
        {
            _password = await _passwords
                .GetPasswordAsync(new PasswordRequest(_displayName ?? "archive", null, IsRetry: false), cancellationToken)
                .ConfigureAwait(false);
        }

        // Test mode: 7-Zip decodes everything and verifies CRCs without ever asking for a destination
        // stream, so nothing can be written anywhere even by mistake.
        using var callback = new ExtractCallback(static _ => null, _password is null ? null : Reveal(_password));
        var hr = _archive.Extract(callback, indices: null, testMode: true);

        var findings = new List<ArchiveTestFinding>();
        foreach (var (index, result) in callback.Results)
        {
            if (result == OperationResult.Ok)
            {
                continue;
            }

            var name = index < _items.Count ? _items[(int)index].Path : $"entry {index}";
            findings.Add(new ArchiveTestFinding(name, OperationResult.Describe(result), IsError: true));
        }

        // "Corrupted" is a claim about the archive; "unsupported method" is a statement about this
        // build of the library. Reporting the first when the second is true tells a user to throw away
        // a file that is perfectly intact - they need a codec, not a backup.
        var onlyUnsupported = findings.Count > 0
            && callback.Results.Values.All(static r => r == OperationResult.Ok || r == OperationResult.UnsupportedMethod);

        var status = hr == Hresult.S_OK && findings.Count == 0 ? ArchiveTestStatus.Valid
            : hr == Hresult.S_OK && onlyUnsupported ? ArchiveTestStatus.Unsupported
            : ArchiveTestStatus.Corrupted;

        return new ArchiveTestResult
        {
            Status = status,
            EntriesTested = _items.Count,
            Findings = findings,
        };
    }

    private uint IndexOf(ArchiveEntry entry)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Path, entry.RawPath, StringComparison.Ordinal))
            {
                return _items[i].Index;
            }
        }

        throw new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.IoFailure,
            $"'{entry.RawPath}' is not in this archive.",
            entry.RawPath));
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Order matters and is load-bearing: the archive holds a reference to the input stream, so
            // releasing the stream first leaves 7-Zip calling Release on freed memory. That was spike
            // finding F1, and it presented as a SIGSEGV a long way from the cause.
            _archive.Dispose();
            _openCallback?.Dispose();
            _stream.Dispose();
            _password?.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
