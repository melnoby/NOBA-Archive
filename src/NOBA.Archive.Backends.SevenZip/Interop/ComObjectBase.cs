using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <summary>
/// A managed object that 7-Zip can call through a native vtable.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-rolled rather than <c>[ComImport]</c>: built-in COM interop is Windows-only, while
/// 7-Zip's own COM-lite (its <c>MyCom.h</c>) is just IUnknown-shaped vtables and works identically on
/// both platforms. Function-pointer vtables therefore run everywhere, are AOT-friendly, and — the point
/// of this spike — let the callbacks be implemented in managed code so <b>we</b> own the destination.
/// </para>
/// <para>
/// Memory layout of the native object we hand out:
/// <code>
/// offset 0: void** vtable
/// offset 8: GCHandle back to the managed instance
/// </code>
/// </para>
/// </remarks>
public abstract unsafe class ComObjectBase : IDisposable
{
    private nint _self;
    private nint _vtable;
    private GCHandle _handle;
    private int _refCount = 1;
    private bool _disposed;

    /// <summary>Number of methods including the three IUnknown slots.</summary>
    protected abstract int MethodCount { get; }

    /// <summary>Fills slots 3..n-1; the base fills QueryInterface/AddRef/Release.</summary>
    protected abstract void FillVtable(nint* vtable);

    protected abstract bool Supports(in Guid iid);

    /// <summary>
    /// Returns a pointer to a <em>different</em> object implementing <paramref name="iid"/>, or 0.
    /// </summary>
    /// <remarks>
    /// COM only requires pointer identity for <c>IUnknown</c>; every other interface may be served by a
    /// separate object with its own vtable. That is not an optimisation here, it is a correctness
    /// requirement: <c>ICryptoGetTextPassword</c> has a different vtable shape from
    /// <c>IArchiveExtractCallback</c>, so returning the same pointer for both would send 7-Zip into the
    /// wrong slot - a silent, memory-corrupting mismatch rather than a clean failure.
    /// </remarks>
    protected virtual nint QueryOtherInterface(in Guid iid) => 0;

    public nint Pointer
    {
        get
        {
            EnsureBuilt();
            return _self;
        }
    }

    private void EnsureBuilt()
    {
        if (_self != 0)
        {
            return;
        }

        _handle = GCHandle.Alloc(this, GCHandleType.Normal);
        _vtable = Marshal.AllocHGlobal(sizeof(nint) * MethodCount);
        var vtable = (nint*)_vtable;

        vtable[0] = (nint)(delegate* unmanaged<void*, Guid*, void**, int>)&Thunks.QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged<void*, uint>)&Thunks.AddRef;
        vtable[2] = (nint)(delegate* unmanaged<void*, uint>)&Thunks.Release;
        FillVtable(vtable);

        _self = Marshal.AllocHGlobal(sizeof(nint) * 2);
        ((nint*)_self)[0] = _vtable;
        ((nint*)_self)[1] = GCHandle.ToIntPtr(_handle);
    }

    internal static T Resolve<T>(void* self)
        where T : ComObjectBase
    {
        var handle = GCHandle.FromIntPtr(((nint*)self)[1]);
        return (T)handle.Target!;
    }

    internal int QueryInterfaceCore(Guid* iid, void** result)
    {
        if (result is null)
        {
            return Hresult.E_INVALIDARG;
        }

        if (*iid == Iid.IUnknown || Supports(*iid))
        {
            *result = (void*)Pointer;
            Interlocked.Increment(ref _refCount);
            return Hresult.S_OK;
        }

        var other = QueryOtherInterface(*iid);
        if (other != 0)
        {
            *result = (void*)other;
            return Hresult.S_OK;
        }

        *result = null;
        return Hresult.E_NOINTERFACE;
    }

    internal uint AddRefCore() => (uint)Interlocked.Increment(ref _refCount);

    /// <summary>
    /// Refcount reaching zero does not free anything: the managed side owns this object's lifetime and
    /// releases it in <see cref="Dispose"/>. 7-Zip only needs the count to be coherent.
    /// </summary>
    internal uint ReleaseCore() => (uint)Math.Max(0, Interlocked.Decrement(ref _refCount));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore();

        if (_self != 0)
        {
            Marshal.FreeHGlobal(_self);
            _self = 0;
        }

        if (_vtable != 0)
        {
            Marshal.FreeHGlobal(_vtable);
            _vtable = 0;
        }

        if (_handle.IsAllocated)
        {
            _handle.Free();
        }

        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore()
    {
    }

    private static class Thunks
    {
        [UnmanagedCallersOnly]
        public static int QueryInterface(void* self, Guid* iid, void** result)
        {
            try
            {
                return Resolve<ComObjectBase>(self).QueryInterfaceCore(iid, result);
            }
            catch
            {
                return Hresult.E_FAIL;
            }
        }

        [UnmanagedCallersOnly]
        public static uint AddRef(void* self)
        {
            try
            {
                return Resolve<ComObjectBase>(self).AddRefCore();
            }
            catch
            {
                return 1;
            }
        }

        [UnmanagedCallersOnly]
        public static uint Release(void* self)
        {
            try
            {
                return Resolve<ComObjectBase>(self).ReleaseCore();
            }
            catch
            {
                return 1;
            }
        }
    }
}

/// <summary>Calls into a native 7-Zip object's vtable.</summary>
public static unsafe class NativeCall
{
    public static nint Slot(nint obj, int index) => ((nint**)obj)[0][index];

    public static uint Release(nint obj)
    {
        var fn = (delegate* unmanaged<void*, uint>)Slot(obj, 2);
        return fn((void*)obj);
    }
}
