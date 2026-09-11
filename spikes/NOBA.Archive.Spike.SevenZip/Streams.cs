using System.Runtime.InteropServices;

namespace NOBA.Archive.Spike.SevenZip;

/// <summary>
/// Presents a managed <see cref="Stream"/> to 7-Zip as an <c>IInStream</c>, so even the archive's own
/// bytes come from a stream the caller owns rather than from a path the backend opened.
/// </summary>
public sealed unsafe class InStreamWrapper : ComObjectBase
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public InStreamWrapper(Stream stream, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    public long BytesRead { get; private set; }

    protected override int MethodCount => 5;

    protected override bool Supports(in Guid iid) =>
        iid == Iid.ISequentialInStream || iid == Iid.IInStream;

    protected override void FillVtable(nint* vtable)
    {
        vtable[3] = (nint)(delegate* unmanaged<void*, void*, uint, uint*, int>)&ReadThunk;
        vtable[4] = (nint)(delegate* unmanaged<void*, long, uint, ulong*, int>)&SeekThunk;
    }

    protected override void DisposeCore()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static int ReadThunk(void* self, void* data, uint size, uint* processed)
    {
        try
        {
            var instance = Resolve<InStreamWrapper>(self);
            if (size == 0)
            {
                if (processed is not null)
                {
                    *processed = 0;
                }

                return Hresult.S_OK;
            }

            var span = new Span<byte>(data, (int)Math.Min(size, int.MaxValue));
            var read = instance._stream.Read(span);
            instance.BytesRead += read;
            if (processed is not null)
            {
                *processed = (uint)read;
            }

            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int SeekThunk(void* self, long offset, uint origin, ulong* newPosition)
    {
        try
        {
            var instance = Resolve<InStreamWrapper>(self);
            var position = instance._stream.Seek(offset, (SeekOrigin)origin);
            if (newPosition is not null)
            {
                *newPosition = (ulong)position;
            }

            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }
}

/// <summary>
/// The heart of the spike: <c>ISequentialOutStream</c> backed by a managed stream the caller chose.
/// If 7-Zip writes decoded bytes here, it never learns a destination path — which is exactly the
/// property ADR-0005 depends on.
/// </summary>
public sealed unsafe class OutStreamWrapper : ComObjectBase
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public OutStreamWrapper(Stream stream, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    public long BytesWritten { get; private set; }

    protected override int MethodCount => 4;

    protected override bool Supports(in Guid iid) => iid == Iid.ISequentialOutStream;

    protected override void FillVtable(nint* vtable) =>
        vtable[3] = (nint)(delegate* unmanaged<void*, void*, uint, uint*, int>)&WriteThunk;

    protected override void DisposeCore()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static int WriteThunk(void* self, void* data, uint size, uint* processed)
    {
        try
        {
            var instance = Resolve<OutStreamWrapper>(self);
            var span = new ReadOnlySpan<byte>(data, (int)Math.Min(size, int.MaxValue));
            instance._stream.Write(span);
            instance.BytesWritten += span.Length;
            if (processed is not null)
            {
                *processed = (uint)span.Length;
            }

            return Hresult.S_OK;
        }
        catch
        {
            return Hresult.E_FAIL;
        }
    }
}
