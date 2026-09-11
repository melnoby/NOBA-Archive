using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <summary>
/// <c>IArchiveExtractCallback</c> plus <c>ICryptoGetTextPassword</c>.
/// </summary>
/// <remarks>
/// <c>GetStream</c> is the question this whole spike exists to answer: 7-Zip asks "where do I put entry
/// N?", and the only thing it accepts back is an <c>ISequentialOutStream</c>. There is no path in the
/// signature. If this works for plain, solid, and encrypted archives, then the backend genuinely cannot
/// choose a destination, and the hardened extraction writer of ADR-0005 is implementable.
/// </remarks>
public sealed unsafe class ExtractCallback : ComObjectBase
{
    private readonly Func<uint, Stream?> _streamFactory;
    private readonly Action<uint, int>? _onEntryFinished;
    private readonly char[]? _password;
    private readonly List<OutStreamWrapper> _issued = [];

    public ExtractCallback(
        Func<uint, Stream?> streamFactory,
        char[]? password = null,
        Action<uint, int>? onEntryFinished = null)
    {
        _streamFactory = streamFactory;
        _password = password;
        _onEntryFinished = onEntryFinished;
    }

    public ulong TotalReported { get; private set; }

    /// <summary>Per-entry operation results, keyed by the archive's own entry index.</summary>
    public Dictionary<uint, int> Results { get; } = [];

    public int GetStreamCalls { get; private set; }

    public int PasswordRequests { get; private set; }

    /// <summary>
    /// The entry 7-Zip is currently working on.
    /// </summary>
    /// <remarks>
    /// <c>SetOperationResult</c> carries no index — it reports on whatever <c>GetStream</c> was last
    /// asked about. The spike keyed results by <c>Results.Count</c>, which agrees with the entry index
    /// only while every entry is extracted in order; the moment one is skipped by policy or a subset is
    /// selected, every result lands against the wrong entry. Remembering the index is the fix.
    /// </remarks>
    private uint _currentIndex;

    protected override int MethodCount => 8;

    protected override bool Supports(in Guid iid) =>
        iid == Iid.IArchiveExtractCallback || iid == Iid.IProgress;

    protected override nint QueryOtherInterface(in Guid iid)
    {
        if (iid != Iid.ICryptoGetTextPassword || _password is null)
        {
            return 0;
        }

        _passwordProvider ??= new PasswordProvider(_password, () => PasswordRequests++);
        return _passwordProvider.Pointer;
    }

    protected override void FillVtable(nint* vtable)
    {
        // IProgress
        vtable[3] = (nint)(delegate* unmanaged<void*, ulong, int>)&SetTotalThunk;
        vtable[4] = (nint)(delegate* unmanaged<void*, ulong*, int>)&SetCompletedThunk;

        // IArchiveExtractCallback
        vtable[5] = (nint)(delegate* unmanaged<void*, uint, void**, int, int>)&GetStreamThunk;
        vtable[6] = (nint)(delegate* unmanaged<void*, int, int>)&PrepareOperationThunk;
        vtable[7] = (nint)(delegate* unmanaged<void*, int, int>)&SetOperationResultThunk;
    }

    protected override void DisposeCore()
    {
        foreach (var wrapper in _issued)
        {
            wrapper.Dispose();
        }

        _issued.Clear();
        _passwordProvider?.Dispose();
    }

    private PasswordProvider? _passwordProvider;

    [UnmanagedCallersOnly]
    private static int SetTotalThunk(void* self, ulong total)
    {
        try
        {
            Resolve<ExtractCallback>(self).TotalReported = total;
            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int SetCompletedThunk(void* self, ulong* value) => Hresult.S_OK;

    [UnmanagedCallersOnly]
    private static int GetStreamThunk(void* self, uint index, void** outStream, int askMode)
    {
        try
        {
            var instance = Resolve<ExtractCallback>(self);
            instance.GetStreamCalls++;
            instance._currentIndex = index;

            if (outStream is null)
            {
                return Hresult.E_INVALIDARG;
            }

            *outStream = null;
            if (askMode != AskMode.Extract)
            {
                return Hresult.S_OK;
            }

            var destination = instance._streamFactory(index);
            if (destination is null)
            {
                // Returning S_OK with a null stream is how an entry is skipped - which is precisely how
                // a blocked path would be refused without the backend ever knowing why.
                return Hresult.S_OK;
            }

            var wrapper = new OutStreamWrapper(destination);
            instance._issued.Add(wrapper);
            *outStream = (void*)wrapper.Pointer;
            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int PrepareOperationThunk(void* self, int askMode) => Hresult.S_OK;

    [UnmanagedCallersOnly]
    private static int SetOperationResultThunk(void* self, int result)
    {
        try
        {
            var instance = Resolve<ExtractCallback>(self);
            var index = instance._currentIndex;
            instance.Results[index] = result;

            // Publish or discard this entry now, while 7-Zip is still between entries. Waiting until
            // the whole Extract call returns would mean holding every temporary file open at once -
            // 3200 handles for a 3200-entry archive.
            instance._onEntryFinished?.Invoke(index, result);
            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }
}
