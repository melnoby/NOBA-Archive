using System.Runtime.InteropServices;

namespace NOBA.Archive.Backends.SevenZip.Interop;

/// <summary>
/// 7-Zip's PROPVARIANT.
/// </summary>
/// <remarks>
/// <b>The size differs by platform, and getting it wrong corrupts the stack silently.</b>
/// <list type="bullet">
/// <item>On <b>Windows x64</b> the real OLE <c>PROPVARIANT</c> is <b>24 bytes</b>: 8 bytes of header
/// (<c>VARTYPE</c> plus three reserved <c>WORD</c>s) followed by a union whose largest members are
/// counted-array structs such as <c>CAUB</c> (<c>{ ULONG cElems; UCHAR* pElems; }</c> = 4 + 4 padding
/// + 8 = 16 bytes).</item>
/// <item>On <b>Linux</b>, 7-Zip supplies a simplified struct in <c>MyWindows.h</c> whose union holds
/// only scalars, so it is <b>16 bytes</b>.</item>
/// </list>
/// Declaring 16 on Windows means <c>GetHandlerProperty2</c> writes 24 bytes into a 16-byte stack slot -
/// no exception at the call, just corrupted neighbouring stack and an access violation later, somewhere
/// unrelated. Declaring 24 is correct on both: Linux simply leaves the last 8 bytes untouched.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public nint Pointer;
    [FieldOffset(8)] public ulong UInt64Value;
    [FieldOffset(8)] public short BoolValue;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI4 = 19;
    public const ushort VT_UI8 = 21;
    public const ushort VT_FILETIME = 64;

    /// <summary>
    /// The size of one <c>PROPVARIANT</c> <em>as the native side lays them out in an array</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This struct is declared 24 bytes because that is the Windows size and over-declaring is safe for
    /// a <em>single</em> value: Linux simply leaves the last eight bytes alone. An <b>array</b> is a
    /// different question, and the two answers must not be confused. <c>ISetProperties</c> takes
    /// <c>const PROPVARIANT *values</c> and indexes it with the native stride — 24 on Windows, 16 on
    /// Linux — so handing it an array of 24-byte structs on Linux means every element after the first
    /// is read from the wrong offset.
    /// </para>
    /// <para>
    /// It does not crash. It reads a plausible-looking <c>vt</c> out of the middle of the previous
    /// element and quietly ignores the property: an archive asked for non-solid came back solid, with
    /// the first property (compression level) working perfectly and every later one silently dropped.
    /// </para>
    /// </remarks>
    public static int NativeSize => OperatingSystem.IsWindows() ? 24 : 16;

    /// <summary>Offset of the union within the struct. The 8-byte header is the same on both.</summary>
    public const int UnionOffset = 8;
}

public static class PropId
{
    public const uint Path = 3;
    public const uint IsDir = 6;
    public const uint Size = 7;
    public const uint PackSize = 8;
    public const uint Attrib = 9;

    // The order in 7-Zip's PropID.h is what fixes these numbers, and they are easy to get wrong by
    // one: ...Attrib=9, CTime=10, ATime=11, MTime=12, Solid=13, Commented=14, Encrypted=15... An
    // off-by-eight here (MTime read as Type) does not fail - it silently produces archives with no
    // timestamps at all, which is exactly how it was found.
    public const uint CTime = 10;
    public const uint ATime = 11;
    public const uint MTime = 12;
    public const uint Encrypted = 15;
    public const uint Crc = 19;
    public const uint Method = 22;
    public const uint SymLink = 63;
}

/// <summary>NArchive::NHandlerPropID from IArchive.h.</summary>
public static class HandlerPropId
{
    public const uint Name = 0;
    public const uint ClassId = 1;
    public const uint Extension = 2;
    public const uint UpdateEnabled = 4;
    public const uint Flags = 11;
}

public static class AskMode
{
    public const int Extract = 0;
    public const int Test = 1;
    public const int Skip = 2;
}

public static class OperationResult
{
    public const int Ok = 0;
    public const int UnsupportedMethod = 1;
    public const int DataError = 2;
    public const int CrcError = 3;

    public static string Describe(int value) => value switch
    {
        Ok => "OK",
        UnsupportedMethod => "unsupported method",
        DataError => "data error",
        CrcError => "CRC error",
        _ => $"result {value}",
    };
}

public static class Hresult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
}

/// <summary>
/// 7-Zip interface identifiers. They all share the prefix 23170F69-40C1-278A; the last eight bytes
/// encode the interface group and offset.
/// </summary>
public static class Iid
{
    public static Guid Make(uint group, uint offset) =>
        new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, (byte)group, 0x00, (byte)offset, 0x00, 0x00);

    public static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");

    public static readonly Guid ISequentialInStream = Make(3, 0x01);
    public static readonly Guid ISequentialOutStream = Make(3, 0x02);
    public static readonly Guid IInStream = Make(3, 0x03);
    public static readonly Guid IStreamGetSize = Make(3, 0x06);

    public static readonly Guid IOutStream = Make(3, 0x04);

    public static readonly Guid ICryptoGetTextPassword = Make(5, 0x10);
    public static readonly Guid ICryptoGetTextPassword2 = Make(5, 0x11);

    public static readonly Guid IProgress = Make(0, 0x05);
    public static readonly Guid IArchiveOpenCallback = Make(6, 0x10);
    public static readonly Guid IArchiveExtractCallback = Make(6, 0x20);
    public static readonly Guid IInArchive = Make(6, 0x60);
    public static readonly Guid ISetProperties = Make(6, 0x03);
    public static readonly Guid IArchiveUpdateCallback = Make(6, 0x80);
    public static readonly Guid IArchiveUpdateCallback2 = Make(6, 0x82);
    public static readonly Guid IOutArchive = Make(6, 0xA0);

    /// <summary>Handler CLSID: 23170F69-40C1-278A-1000-000110{formatId}0000.</summary>
    public static Guid Handler(byte formatId) =>
        new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, formatId, 0x00, 0x00);
}
