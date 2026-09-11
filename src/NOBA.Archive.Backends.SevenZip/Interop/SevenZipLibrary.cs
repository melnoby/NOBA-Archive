using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip.Interop;

public sealed record HandlerInfo(int Index, string Name, Guid ClassId, string Extensions, bool CanUpdate);

/// <summary>
/// Loads the native library and exposes its four plugin entry points.
/// </summary>
/// <remarks>
/// The library is loaded by absolute path. That is not incidental: it is the DLL-hijacking defence from
/// THREAT_MODEL.md T8, and the spike adopts it from the first line rather than retrofitting it later.
/// </remarks>
public sealed unsafe class SevenZipLibrary : IDisposable
{
    private readonly nint _handle;
    private readonly delegate* unmanaged<Guid*, Guid*, void**, int> _createObject;
    private readonly delegate* unmanaged<uint*, int> _getNumberOfFormats;
    private readonly delegate* unmanaged<uint, uint, PropVariant*, int> _getHandlerProperty2;

    public SevenZipLibrary(string libraryPath)
    {
        LibraryPath = Path.GetFullPath(libraryPath);
        if (!File.Exists(LibraryPath))
        {
            throw new FileNotFoundException("The 7-Zip native library was not found.", LibraryPath);
        }

        _handle = NativeLibrary.Load(LibraryPath);
        _createObject = (delegate* unmanaged<Guid*, Guid*, void**, int>)NativeLibrary.GetExport(_handle, "CreateObject");
        _getNumberOfFormats = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(_handle, "GetNumberOfFormats");
        _getHandlerProperty2 = (delegate* unmanaged<uint, uint, PropVariant*, int>)NativeLibrary.GetExport(_handle, "GetHandlerProperty2");
    }

    public string LibraryPath { get; }

    public int FormatCount
    {
        get
        {
            uint count;
            var hr = _getNumberOfFormats(&count);
            return hr == Hresult.S_OK ? (int)count : 0;
        }
    }

    /// <summary>
    /// Enumerates the handlers this build actually contains. FORMAT_SUPPORT.md says the bonus format
    /// tier must come from the shipped binary rather than from memory; this is how that gets answered.
    /// </summary>
    public IReadOnlyList<HandlerInfo> EnumerateHandlers()
    {
        var handlers = new List<HandlerInfo>();
        for (uint i = 0; i < (uint)FormatCount; i++)
        {
            var name = ReadStringProperty(i, HandlerPropId.Name) ?? $"format-{i}";
            var extensions = ReadStringProperty(i, HandlerPropId.Extension) ?? string.Empty;
            var classId = ReadGuidProperty(i, HandlerPropId.ClassId);
            var canUpdate = ReadBoolProperty(i, HandlerPropId.UpdateEnabled);
            handlers.Add(new HandlerInfo((int)i, name, classId, extensions, canUpdate));
        }

        return handlers;
    }

    private string? ReadStringProperty(uint index, uint propId)
    {
        PropVariant value = default;
        if (_getHandlerProperty2(index, propId, &value) != Hresult.S_OK ||
            value.Vt != PropVariant.VT_BSTR ||
            value.Pointer == 0)
        {
            return null;
        }

        return BStr.Read(value.Pointer);
    }

    private Guid ReadGuidProperty(uint index, uint propId)
    {
        PropVariant value = default;
        if (_getHandlerProperty2(index, propId, &value) != Hresult.S_OK ||
            value.Vt != PropVariant.VT_BSTR ||
            value.Pointer == 0)
        {
            return Guid.Empty;
        }

        return new Guid(BStr.ReadRaw(value.Pointer, 16));
    }

    private bool ReadBoolProperty(uint index, uint propId)
    {
        PropVariant value = default;
        return _getHandlerProperty2(index, propId, &value) == Hresult.S_OK &&
               value.Vt == PropVariant.VT_BOOL &&
               value.BoolValue != 0;
    }

    public nint CreateArchiveHandler(Guid classId) => CreateObject(classId, Iid.IInArchive);

    /// <summary>
    /// Creates a handler object for a specific interface.
    /// </summary>
    /// <remarks>
    /// Reading and writing are separate objects in 7-Zip, not two faces of one: the same CLSID
    /// instantiated as <c>IOutArchive</c> is the writer. Asking for the wrong interface fails cleanly
    /// here rather than at the first vtable call, where the failure would be a wrong-slot jump.
    /// </remarks>
    public nint CreateObject(Guid classId, Guid interfaceId)
    {
        void* result;
        var hr = _createObject(&classId, &interfaceId, &result);
        if (hr != Hresult.S_OK || result is null)
        {
            throw new InvalidOperationException(
                $"CreateObject failed for {classId} as {interfaceId} (hr=0x{hr:X8}).");
        }

        return (nint)result;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeLibrary.Free(_handle);
        }
    }
}
