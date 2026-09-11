using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <summary>Thin wrapper over a native <c>IOutArchive</c> vtable.</summary>
public sealed unsafe class OutArchive : IDisposable
{
    private nint _handle;

    public OutArchive(nint handle) => _handle = handle;

    /// <summary>
    /// Compresses <paramref name="itemCount"/> items into <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// One call for the whole archive. 7-Zip drives from here: it asks the callback for each item's
    /// properties, then for its bytes, in whatever order suits the compressor. Nothing is written
    /// until this returns, which is what lets an abandoned build leave no valid archive behind.
    /// </remarks>
    public int UpdateItems(OutStreamWrapper output, uint itemCount, UpdateCallback callback)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(callback);

        var fn = (delegate* unmanaged<void*, void*, uint, void*, int>)NativeCall.Slot(_handle, 3);
        return fn((void*)_handle, (void*)output.Pointer, itemCount, (void*)callback.Pointer);
    }

    /// <summary>
    /// Applies compression settings through <c>ISetProperties</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is 7-Zip's own configuration surface — the same strings the command line takes, so "x=9"
    /// here means exactly what <c>-mx=9</c> means there. Passing a name the handler does not know is an
    /// error rather than a silent no-op, which is the behaviour we want: an option the product offers
    /// and the backend ignores is worse than one it refuses.
    /// </para>
    /// <para>
    /// The names are <c>wchar_t*</c>, which is two bytes on Windows and four on Linux, so they are
    /// built through <see cref="BStr"/> rather than marshalled as UTF-16.
    /// </para>
    /// </remarks>
    public int SetProperties(IReadOnlyList<SevenZipProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Count == 0)
        {
            return Hresult.S_OK;
        }

        void* setProperties;
        var iid = Iid.ISetProperties;
        var queryInterface = (delegate* unmanaged<void*, Guid*, void**, int>)NativeCall.Slot(_handle, 0);
        if (queryInterface((void*)_handle, &iid, &setProperties) != Hresult.S_OK || setProperties is null)
        {
            // A handler with no ISetProperties cannot be configured at all, so accepting the request
            // silently would mean shipping the default while reporting the user's choice.
            return Hresult.E_NOINTERFACE;
        }

        var names = new nint[properties.Count];

        // Built by hand at the native stride rather than as a PropVariant[]. See PropVariant.NativeSize:
        // a managed array would be indexed at 24 bytes on a platform that expects 16, and every
        // property after the first would be read from the wrong place.
        var stride = PropVariant.NativeSize;
        var values = (byte*)NativeMemory.AllocZeroed((nuint)(stride * properties.Count));

        try
        {
            for (var i = 0; i < properties.Count; i++)
            {
                names[i] = BStr.Allocate(properties[i].Name);

                var slot = values + (i * stride);
                *(ushort*)slot = properties[i].Value.Vt;
                *(ulong*)(slot + PropVariant.UnionOffset) = properties[i].Value.UInt64Value;
            }

            fixed (nint* namePointers = names)
            {
                var fn = (delegate* unmanaged<void*, nint*, byte*, uint, int>)
                    NativeCall.Slot((nint)setProperties, 3);
                return fn(setProperties, namePointers, values, (uint)properties.Count);
            }
        }
        finally
        {
            foreach (var name in names)
            {
                if (name != 0)
                {
                    BStr.Free(name);
                }
            }

            foreach (var property in properties)
            {
                if (property.Value.Vt == PropVariant.VT_BSTR && property.Value.Pointer != 0)
                {
                    BStr.Free(property.Value.Pointer);
                }
            }

            NativeMemory.Free(values);
            NativeCall.Release((nint)setProperties);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeCall.Release(_handle);
            _handle = 0;
        }
    }
}

/// <summary>One <c>ISetProperties</c> name/value pair, in 7-Zip's own vocabulary.</summary>
public readonly record struct SevenZipProperty(string Name, PropVariant Value)
{
    public static SevenZipProperty Number(string name, uint value) =>
        new(name, new PropVariant { Vt = PropVariant.VT_UI4, UInt64Value = value });

    public static SevenZipProperty Flag(string name, bool value) =>
        new(name, new PropVariant { Vt = PropVariant.VT_BOOL, BoolValue = value ? (short)-1 : (short)0 });

    public static SevenZipProperty Text(string name, string value) =>
        new(name, new PropVariant { Vt = PropVariant.VT_BSTR, Pointer = BStr.Allocate(value) });
}
