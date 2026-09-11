namespace NOBA.Archive.Spike.SevenZip;

public sealed record ArchiveItem(uint Index, string Path, bool IsDirectory, ulong Size, bool IsEncrypted);

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
            var dirValue = GetProperty(i, PropId.IsDir);
            var encValue = GetProperty(i, PropId.Encrypted);

            items.Add(new ArchiveItem(
                i,
                pathValue.Vt == PropVariant.VT_BSTR ? BStr.Read(pathValue.Pointer) ?? string.Empty : string.Empty,
                dirValue.Vt == PropVariant.VT_BOOL && dirValue.BoolValue != 0,
                sizeValue.Vt == PropVariant.VT_UI8 ? sizeValue.UInt64Value : 0,
                encValue.Vt == PropVariant.VT_BOOL && encValue.BoolValue != 0));
        }

        return items;
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
