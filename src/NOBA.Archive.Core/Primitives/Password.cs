using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NOBA.Archive.Core.Primitives;

/// <summary>
/// An archive password held in pinned memory and zeroed on disposal.
/// </summary>
/// <remarks>
/// <see cref="ToString"/> is sealed and returns <c>***</c>, so a password cannot leak through string
/// interpolation, a logging call, or a debugger watch expression. <see cref="System.Security.SecureString"/>
/// is deliberately not used: Microsoft no longer recommends it for new development and it provides no
/// real protection on modern .NET.
/// </remarks>
public sealed class Password : IDisposable
{
    private byte[]? _utf8;
    private GCHandle _pin;

    private Password(byte[] utf8)
    {
        _utf8 = utf8;
        _pin = GCHandle.Alloc(utf8, GCHandleType.Pinned);
    }

    public bool IsDisposed => _utf8 is null;

    public int ByteLength => _utf8?.Length ?? 0;

    public static Password FromChars(ReadOnlySpan<char> value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        var buffer = new byte[count];
        Encoding.UTF8.GetBytes(value, buffer);
        return new Password(buffer);
    }

    public static Password FromUtf8(ReadOnlySpan<byte> value) => new(value.ToArray());

    /// <summary>Hands the raw bytes to a backend callback without ever materialising a string.</summary>
    public T Use<T>(Func<ReadOnlyMemory<byte>, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ObjectDisposedException.ThrowIf(_utf8 is null, this);
        return reader(_utf8);
    }

    /// <summary>Constant-time comparison, used for the confirm-password field.</summary>
    public bool Matches(Password other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ObjectDisposedException.ThrowIf(_utf8 is null, this);
        ObjectDisposedException.ThrowIf(other._utf8 is null, other);
        return CryptographicOperations.FixedTimeEquals(_utf8, other._utf8);
    }

    public void Dispose()
    {
        if (_utf8 is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_utf8);
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }

        _utf8 = null;
    }

    /// <summary>Always <c>***</c>. Sealed so no subclass can undo it.</summary>
    public override string ToString() => "***";
}
