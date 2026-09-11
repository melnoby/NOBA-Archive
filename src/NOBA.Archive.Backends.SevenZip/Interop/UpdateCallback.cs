using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <summary>One entry to be written, described before its bytes exist.</summary>
/// <param name="Open">
/// Null for a directory. Otherwise called at compression time; the stream it returns is disposed as
/// soon as 7-Zip has finished reading it, so a thousand-file archive holds one handle at a time.
/// </param>
public sealed record UpdateItem(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified,
    Func<CancellationToken, ValueTask<Stream>>? Open);

/// <summary>
/// <c>IArchiveUpdateCallback</c> — the writer's half of the inversion.
/// </summary>
/// <remarks>
/// <para>
/// Reading inverts control at extraction (<c>GetStream</c> asks where entry N goes); writing inverts it
/// at compression (<c>GetProperty</c> and <c>GetStream</c> ask what entry N <em>is</em>). Both mean the
/// same thing for this codebase: 7-Zip never learns a path on the filesystem, in either direction. The
/// only paths it sees here are the names that will live inside the archive.
/// </para>
/// <para>
/// Every thunk swallows exceptions and returns an HRESULT: a managed exception unwinding through the
/// native compressor's frames is undefined behaviour, not an error report. The first failure is
/// remembered and rethrown by the caller once <c>UpdateItems</c> has returned.
/// </para>
/// </remarks>
public sealed unsafe class UpdateCallback : ComObjectBase
{
    private readonly IReadOnlyList<UpdateItem> _items;
    private readonly char[]? _password;
    private readonly CancellationToken _cancellationToken;
    private readonly List<InStreamWrapper> _openStreams = [];

    private PasswordProvider2? _passwordProvider;

    public UpdateCallback(
        IReadOnlyList<UpdateItem> items,
        char[]? password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        _password = password;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The first exception a callback raised, or null.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The last result 7-Zip reported for an item, or null if it reported none.</summary>
    public int? LastOperationResult { get; private set; }

    protected override int MethodCount => 9;

    protected override bool Supports(in Guid iid) =>
        iid == Iid.IArchiveUpdateCallback || iid == Iid.IProgress;

    protected override nint QueryOtherInterface(in Guid iid)
    {
        if (iid != Iid.ICryptoGetTextPassword2 && iid != Iid.ICryptoGetTextPassword)
        {
            return 0;
        }

        // No password means no encryption, and saying so is not the same as saying "empty password":
        // ICryptoGetTextPassword2 has a passwordIsDefined flag precisely so the two can be told apart.
        // Refusing the interface entirely would also work, but answering it lets 7-Zip stop asking.
        _passwordProvider ??= new PasswordProvider2(_password);
        return _passwordProvider.Pointer;
    }

    protected override void FillVtable(nint* vtable)
    {
        vtable[3] = (nint)(delegate* unmanaged<void*, ulong, int>)&SetTotalThunk;
        vtable[4] = (nint)(delegate* unmanaged<void*, ulong*, int>)&SetCompletedThunk;
        vtable[5] = (nint)(delegate* unmanaged<void*, uint, int*, int*, uint*, int>)&GetUpdateItemInfoThunk;
        vtable[6] = (nint)(delegate* unmanaged<void*, uint, uint, PropVariant*, int>)&GetPropertyThunk;
        vtable[7] = (nint)(delegate* unmanaged<void*, uint, void**, int>)&GetStreamThunk;
        vtable[8] = (nint)(delegate* unmanaged<void*, int, int>)&SetOperationResultThunk;
    }

    protected override void DisposeCore()
    {
        foreach (var stream in _openStreams)
        {
            stream.Dispose();
        }

        _openStreams.Clear();
        _passwordProvider?.Dispose();
    }

    [UnmanagedCallersOnly]
    private static int SetTotalThunk(void* self, ulong total) => Hresult.S_OK;

    [UnmanagedCallersOnly]
    private static int SetCompletedThunk(void* self, ulong* completed) => Hresult.S_OK;

    /// <summary>
    /// Every item is new. There is no existing archive to update from — creation only.
    /// </summary>
    /// <remarks>
    /// <c>indexInArchive</c> is <c>0xFFFFFFFF</c> for "not present in the original", which is the whole
    /// story here: this backend advertises <c>Create</c> and <c>AddEntries</c>, and does not advertise
    /// <c>RemoveEntries</c> or <c>RenameEntries</c>, because updating an existing 7z is not implemented.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static int GetUpdateItemInfoThunk(
        void* self,
        uint index,
        int* newData,
        int* newProperties,
        uint* indexInArchive)
    {
        try
        {
            if (newData is not null)
            {
                *newData = 1;
            }

            if (newProperties is not null)
            {
                *newProperties = 1;
            }

            if (indexInArchive is not null)
            {
                *indexInArchive = 0xFFFFFFFF;
            }

            return Hresult.S_OK;
        }
        catch (Exception ex)
        {
            return Remember(self, ex);
        }
    }

    [UnmanagedCallersOnly]
    private static int GetPropertyThunk(void* self, uint index, uint propId, PropVariant* value)
    {
        try
        {
            var instance = Resolve<UpdateCallback>(self);
            if (value is null)
            {
                return Hresult.E_INVALIDARG;
            }

            *value = default;
            if (index >= instance._items.Count)
            {
                return Hresult.E_INVALIDARG;
            }

            var item = instance._items[(int)index];

            switch (propId)
            {
                case PropId.Path:
                    value->Vt = PropVariant.VT_BSTR;
                    value->Pointer = BStr.Allocate(item.Path);
                    return Hresult.S_OK;

                case PropId.IsDir:
                    value->Vt = PropVariant.VT_BOOL;
                    value->BoolValue = item.IsDirectory ? (short)-1 : (short)0;
                    return Hresult.S_OK;

                case PropId.Size:
                    value->Vt = PropVariant.VT_UI8;
                    value->UInt64Value = (ulong)Math.Max(0, item.Size);
                    return Hresult.S_OK;

                case PropId.Attrib:
                    // FILE_ATTRIBUTE_DIRECTORY / FILE_ATTRIBUTE_ARCHIVE. Nothing else is copied from
                    // the source file: hidden, system and read-only are the source machine's business,
                    // and carrying them into an archive that will be opened elsewhere is a surprise.
                    value->Vt = PropVariant.VT_UI4;
                    value->UInt64Value = item.IsDirectory ? 0x10u : 0x20u;
                    return Hresult.S_OK;

                case PropId.MTime when item.Modified is { } modified:
                    value->Vt = PropVariant.VT_FILETIME;
                    value->UInt64Value = (ulong)modified.UtcDateTime.ToFileTimeUtc();
                    return Hresult.S_OK;

                default:
                    // VT_EMPTY: "no opinion". 7-Zip fills in its own default, which is what should
                    // happen for a property this product does not model.
                    return Hresult.S_OK;
            }
        }
        catch (Exception ex)
        {
            return Remember(self, ex);
        }
    }

    [UnmanagedCallersOnly]
    private static int GetStreamThunk(void* self, uint index, void** inStream)
    {
        try
        {
            var instance = Resolve<UpdateCallback>(self);
            if (inStream is null)
            {
                return Hresult.E_INVALIDARG;
            }

            *inStream = null;
            if (index >= instance._items.Count)
            {
                return Hresult.E_INVALIDARG;
            }

            var item = instance._items[(int)index];
            if (item.IsDirectory || item.Open is null)
            {
                // S_OK with a null stream is how a directory entry is declared: it has a header and no
                // content.
                return Hresult.S_OK;
            }

            instance._cancellationToken.ThrowIfCancellationRequested();

            // Blocking on the caller's async open is safe here and only here: UpdateItems is invoked on
            // a thread-pool thread with no synchronization context, so there is no context to deadlock
            // against. The alternative — opening every source file up front — is what runs a real
            // folder out of handles.
            var source = item.Open(instance._cancellationToken).AsTask().GetAwaiter().GetResult();
            var wrapper = new InStreamWrapper(source, ownsStream: true);
            instance._openStreams.Add(wrapper);
            *inStream = (void*)wrapper.Pointer;
            return Hresult.S_OK;
        }
        catch (Exception ex)
        {
            return Remember(self, ex);
        }
    }

    [UnmanagedCallersOnly]
    private static int SetOperationResultThunk(void* self, int operationResult)
    {
        try
        {
            var instance = Resolve<UpdateCallback>(self);
            instance.LastOperationResult = operationResult;

            // Unlike extraction, 7-Zip's writer reports a single result per item and keeps going. A
            // non-zero result here means the item did not make it into the archive, and an archive
            // that is missing a file it was asked to hold is a failure however cleanly it was written.
            if (operationResult != OperationResult.Ok)
            {
                instance.Failure ??= new IOException(
                    $"7-Zip could not compress an entry: {OperationResult.Describe(operationResult)}.");
            }

            return Hresult.S_OK;
        }
        catch (Exception ex)
        {
            return Remember(self, ex);
        }
    }

    private static int Remember(void* self, Exception ex)
    {
        try
        {
            var instance = Resolve<UpdateCallback>(self);
            instance.Failure ??= ex;
        }
        catch
        {
            // Nothing left to report through.
        }

        return Hresult.E_FAIL;
    }
}

/// <summary>
/// <c>ICryptoGetTextPassword2</c>, which differs from its predecessor by one crucial output.
/// </summary>
/// <remarks>
/// The reader's interface answers "here is the password". The writer's answers "is there one at all",
/// and that flag is what decides whether the archive is encrypted. Returning an empty string with the
/// flag set would produce an archive encrypted with an empty password — worse than an unencrypted one,
/// because it looks protected.
/// </remarks>
public sealed unsafe class PasswordProvider2 : ComObjectBase
{
    private readonly char[]? _password;

    public PasswordProvider2(char[]? password) => _password = password;

    protected override int MethodCount => 4;

    protected override bool Supports(in Guid iid) =>
        iid == Iid.ICryptoGetTextPassword2 || iid == Iid.ICryptoGetTextPassword;

    protected override void FillVtable(nint* vtable) =>
        vtable[3] = (nint)(delegate* unmanaged<void*, int*, nint*, int>)&GetTextPassword2Thunk;

    protected override void DisposeCore()
    {
        if (_password is not null)
        {
            Array.Clear(_password);
        }
    }

    [UnmanagedCallersOnly]
    private static int GetTextPassword2Thunk(void* self, int* passwordIsDefined, nint* password)
    {
        try
        {
            var instance = Resolve<PasswordProvider2>(self);
            var defined = instance._password is { Length: > 0 };

            if (passwordIsDefined is not null)
            {
                *passwordIsDefined = defined ? 1 : 0;
            }

            if (password is not null)
            {
                *password = defined ? BStr.Allocate(new string(instance._password!)) : 0;
            }

            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }
}
