namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <param name="PackedSize">
/// Bytes this entry occupies in the archive. Zero is a legitimate answer, not just a missing one: in a
/// solid block 7-Zip attributes the whole block's packed size to its first entry and reports zero for
/// the rest. Sums over a whole archive are therefore right even though individual entries are not,
/// which is why the expansion-ratio guard reads it archive-wide.
/// </param>
/// <param name="Modified">
/// Null when the archive carries no timestamp for this entry, which is legal and common. Extraction
/// leaves such a file with the time it was written, because inventing one would be worse.
/// </param>
public sealed record ArchiveItem(
    uint Index,
    string Path,
    bool IsDirectory,
    ulong Size,
    ulong PackedSize,
    bool IsEncrypted,
    DateTimeOffset? Modified);

/// <summary>Thin wrapper over a native <c>IInArchive</c> vtable.</summary>
public sealed unsafe class InArchive : IDisposable
{
    private const uint AllItems = 0xFFFF_FFFF;

    private nint _handle;

    public InArchive(nint handle) => _handle = handle;

    public int Open(InStreamWrapper stream, OpenCallback? openCallback = null, ulong maxCheckStartPosition = 1 << 20)
    {
        var fn = (delegate* unmanaged<void*, void*, ulong*, void*, int>)NativeCall.Slot(_handle, 3);
        var limit = maxCheckStartPosition;
        var callback = openCallback is null ? null : (void*)openCallback.Pointer;
        return fn((void*)_handle, (void*)stream.Pointer, &limit, callback);
    }

    public int Close()
    {
        var fn = (delegate* unmanaged<void*, int>)NativeCall.Slot(_handle, 4);
        return fn((void*)_handle);
    }

    public uint ItemCount
    {
        get
        {
            var fn = (delegate* unmanaged<void*, uint*, int>)NativeCall.Slot(_handle, 5);
            uint count;
            return fn((void*)_handle, &count) == Hresult.S_OK ? count : 0;
        }
    }

    public PropVariant GetProperty(uint index, uint propId)
    {
        var fn = (delegate* unmanaged<void*, uint, uint, PropVariant*, int>)NativeCall.Slot(_handle, 6);
        PropVariant value = default;
        return fn((void*)_handle, index, propId, &value) == Hresult.S_OK ? value : default;
    }

    public IReadOnlyList<ArchiveItem> ListItems()
    {
        var items = new List<ArchiveItem>();
        for (uint i = 0; i < ItemCount; i++)
        {
            var pathValue = GetProperty(i, PropId.Path);
            var sizeValue = GetProperty(i, PropId.Size);
            var packValue = GetProperty(i, PropId.PackSize);
            var dirValue = GetProperty(i, PropId.IsDir);
            var encValue = GetProperty(i, PropId.Encrypted);
            var timeValue = GetProperty(i, PropId.MTime);

            items.Add(new ArchiveItem(
                i,
                pathValue.Vt == PropVariant.VT_BSTR ? BStr.Read(pathValue.Pointer) ?? string.Empty : string.Empty,
                dirValue.Vt == PropVariant.VT_BOOL && dirValue.BoolValue != 0,
                sizeValue.Vt == PropVariant.VT_UI8 ? sizeValue.UInt64Value : 0,
                packValue.Vt == PropVariant.VT_UI8 ? packValue.UInt64Value : 0,
                encValue.Vt == PropVariant.VT_BOOL && encValue.BoolValue != 0,
                ToTimestamp(timeValue)));
        }

        return items;
    }

    /// <summary>
    /// A <c>VT_FILETIME</c> PROPVARIANT as an instant, or null.
    /// </summary>
    /// <remarks>
    /// FILETIME is 100-nanosecond ticks since 1601 UTC — unambiguous, unlike the DOS local time a ZIP
    /// carries — so there is no timezone question here and nothing to convert. Values outside the range
    /// <see cref="DateTime"/> accepts are treated as absent: a hostile or corrupt archive must not be
    /// able to throw out of a listing.
    /// </remarks>
    private static DateTimeOffset? ToTimestamp(PropVariant value)
    {
        if (value.Vt != PropVariant.VT_FILETIME || value.UInt64Value == 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime((long)value.UInt64Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Extracts every entry, or the listed indices, through the caller's callback.</summary>
    public int Extract(ExtractCallback callback, uint[]? indices = null, bool testMode = false)
    {
        var fn = (delegate* unmanaged<void*, uint*, uint, int, void*, int>)NativeCall.Slot(_handle, 7);

        if (indices is null)
        {
            return fn((void*)_handle, null, AllItems, testMode ? 1 : 0, (void*)callback.Pointer);
        }

        fixed (uint* p = indices)
        {
            return fn((void*)_handle, p, (uint)indices.Length, testMode ? 1 : 0, (void*)callback.Pointer);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            Close();
            NativeCall.Release(_handle);
            _handle = 0;
        }
    }
}
