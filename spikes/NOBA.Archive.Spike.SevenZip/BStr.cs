using System.Runtime.InteropServices;
using System.Text;

namespace NOBA.Archive.Spike.SevenZip;

/// <summary>
/// 7-Zip's BSTR, which is not the OLE one on Linux.
/// </summary>
/// <remarks>
/// <b>A finding in its own right.</b> 7-Zip's <c>MyWindows.h</c> defines <c>OLECHAR</c> as
/// <c>wchar_t</c>, which is <b>2 bytes on Windows and 4 bytes on Linux</b>. Any interop layer that
/// hard-codes UTF-16 string marshalling will silently produce garbage on one of the two. The product
/// only ships on Windows, so this matters mainly because it means <em>this spike's string handling is
/// the one part that does not transfer</em> — it must be re-verified against <c>7z.dll</c>.
/// </remarks>
public static unsafe class BStr
{
    public static int CharSize { get; } = OperatingSystem.IsWindows() ? 2 : 4;

    /// <summary>Reads a null-terminated string. Width-aware, so it is correct on both platforms.</summary>
    public static string? Read(nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }

        if (CharSize == 2)
        {
            return Marshal.PtrToStringUni(bstr);
        }

        // Each slot is 4 bytes, but 7-Zip's UString holds UTF-16 code units widened to wchar_t - so a
        // non-BMP character arrives as two slots each carrying one surrogate half, not as one code
        // point. Decoding the buffer as UTF-32 mangles exactly those characters (an emoji in a file
        // name is the everyday case). Treating each slot <= 0xFFFF as a UTF-16 code unit handles both
        // that and a genuine code point, so it is correct either way.
        var builder = new StringBuilder();
        for (var i = 0; ; i++)
        {
            var value = Marshal.ReadInt32(bstr, i * 4);
            if (value == 0)
            {
                break;
            }

            if (value <= 0xFFFF)
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append(char.ConvertFromUtf32(value));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Allocates a BSTR the handler will free.
    /// </summary>
    /// <remarks>
    /// <b>The allocator has to match the deallocator, and it differs by platform.</b>
    /// On Windows, 7-Zip uses the real OLE <c>SysAllocStringLen</c>/<c>SysFreeString</c>, so the string
    /// must come from the OLE allocator - <see cref="Marshal.StringToBSTR"/>. Handing it a
    /// <c>malloc</c> block would corrupt the heap the moment 7-Zip freed it.
    /// On Linux, 7-Zip supplies its own implementation in <c>MyWindows.cpp</c>: a 4-byte byte-count
    /// prefix immediately before the character data, allocated with <c>malloc</c> and released with
    /// <c>free</c>. That layout has to be reproduced by hand.
    /// </remarks>
    public static nint Allocate(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            return Marshal.StringToBSTR(value);
        }

        var chars = value.Length;
        var byteSize = (uint)((chars + 1) * CharSize);
        var block = (byte*)NativeMemory.Alloc(byteSize + sizeof(uint));
        *(uint*)block = byteSize;

        var data = block + sizeof(uint);
        if (CharSize == 2)
        {
            var utf16 = Encoding.Unicode.GetBytes(value);
            utf16.CopyTo(new Span<byte>(data, utf16.Length));
            *(ushort*)(data + utf16.Length) = 0;
        }
        else
        {
            var utf32 = Encoding.UTF32.GetBytes(value);
            utf32.CopyTo(new Span<byte>(data, utf32.Length));
            *(uint*)(data + utf32.Length) = 0;
        }

        return (nint)data;
    }

    /// <summary>Reads a fixed-size raw payload carried inside a BSTR (7-Zip ships CLSIDs this way).</summary>
    public static byte[] ReadRaw(nint bstr, int byteCount)
    {
        var bytes = new byte[byteCount];
        Marshal.Copy(bstr, bytes, 0, byteCount);
        return bytes;
    }
}
