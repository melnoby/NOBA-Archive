using System.Text;

namespace NOBA.Archive.Formats.Managed;

/// <summary>
/// Single-byte legacy code pages, carried in-repo rather than taken from a dependency.
/// </summary>
/// <remarks>
/// <para>
/// A ZIP entry name is bytes. Bit 11 of the general-purpose flag says "these are UTF-8"; without it the
/// specification says CP437, but in practice every tool wrote whatever ANSI code page the machine
/// happened to be using. An archive made on an Arabic Windows box carries CP1256, and decoding it as
/// anything else produces mojibake - which this product would then write to disk as a filename.
/// </para>
/// <para>
/// .NET Core ships only UTF-8, UTF-16, ASCII and Latin1; the rest live in the
/// <c>System.Text.Encoding.CodePages</c> package. Taking a dependency for a handful of 128-entry
/// lookup tables is a poor trade against DEPENDENCIES.md, and it would also mean the offline build
/// profile could not decode Arabic names at all. So the tables are here, generated from the Unicode
/// mappings and checked in.
/// </para>
/// <para>
/// If the host has registered <c>CodePagesEncodingProvider</c>, that is preferred - it covers the
/// multi-byte CJK code pages (932, 936, 949, 950) which are deliberately <b>not</b> implemented here.
/// </para>
/// </remarks>
internal static class LegacyCodePages
{
    /// <summary>MS-DOS Latin US - the code page the ZIP specification actually names.</summary>
    private const string Cp437 =
        "\u00C7\u00FC\u00E9\u00E2\u00E4\u00E0\u00E5\u00E7\u00EA\u00EB\u00E8\u00EF\u00EE\u00EC\u00C4\u00C5\u00C9\u00E6\u00C6\u00F4\u00F6\u00F2\u00FB\u00F9\u00FF\u00D6\u00DC\u00A2\u00A3\u00A5\u20A7\u0192\u00E1\u00ED\u00F3\u00FA\u00F1\u00D1\u00AA\u00BA\u00BF\u2310\u00AC\u00BD\u00BC\u00A1\u00AB\u00BB\u2591\u2592\u2593\u2502\u2524\u2561\u2562\u2556\u2555\u2563\u2551\u2557\u255D\u255C\u255B\u2510\u2514\u2534\u252C\u251C\u2500\u253C\u255E\u255F\u255A\u2554\u2569\u2566\u2560\u2550\u256C\u2567\u2568\u2564\u2565\u2559\u2558\u2552\u2553\u256B\u256A\u2518\u250C\u2588\u2584\u258C\u2590\u2580\u03B1\u00DF\u0393\u03C0\u03A3\u03C3\u00B5\u03C4\u03A6\u0398\u03A9\u03B4\u221E\u03C6\u03B5\u2229\u2261\u00B1\u2265\u2264\u2320\u2321\u00F7\u2248\u00B0\u2219\u00B7\u221A\u207F\u00B2\u25A0\u00A0";

    /// <summary>MS-DOS Latin 1 - common in older Western European archives.</summary>
    private const string Cp850 =
        "\u00C7\u00FC\u00E9\u00E2\u00E4\u00E0\u00E5\u00E7\u00EA\u00EB\u00E8\u00EF\u00EE\u00EC\u00C4\u00C5\u00C9\u00E6\u00C6\u00F4\u00F6\u00F2\u00FB\u00F9\u00FF\u00D6\u00DC\u00F8\u00A3\u00D8\u00D7\u0192\u00E1\u00ED\u00F3\u00FA\u00F1\u00D1\u00AA\u00BA\u00BF\u00AE\u00AC\u00BD\u00BC\u00A1\u00AB\u00BB\u2591\u2592\u2593\u2502\u2524\u00C1\u00C2\u00C0\u00A9\u2563\u2551\u2557\u255D\u00A2\u00A5\u2510\u2514\u2534\u252C\u251C\u2500\u253C\u00E3\u00C3\u255A\u2554\u2569\u2566\u2560\u2550\u256C\u00A4\u00F0\u00D0\u00CA\u00CB\u00C8\u0131\u00CD\u00CE\u00CF\u2518\u250C\u2588\u2584\u00A6\u00CC\u2580\u00D3\u00DF\u00D4\u00D2\u00F5\u00D5\u00B5\u00FE\u00DE\u00DA\u00DB\u00D9\u00FD\u00DD\u00AF\u00B4\u00AD\u00B1\u2017\u00BE\u00B6\u00A7\u00F7\u00B8\u00B0\u00A8\u00B7\u00B9\u00B3\u00B2\u25A0\u00A0";

    /// <summary>Windows Central European.</summary>
    private const string Cp1250 =
        "\u20AC\uFFFD\u201A\uFFFD\u201E\u2026\u2020\u2021\uFFFD\u2030\u0160\u2039\u015A\u0164\u017D\u0179\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\uFFFD\u2122\u0161\u203A\u015B\u0165\u017E\u017A\u00A0\u02C7\u02D8\u0141\u00A4\u0104\u00A6\u00A7\u00A8\u00A9\u015E\u00AB\u00AC\u00AD\u00AE\u017B\u00B0\u00B1\u02DB\u0142\u00B4\u00B5\u00B6\u00B7\u00B8\u0105\u015F\u00BB\u013D\u02DD\u013E\u017C\u0154\u00C1\u00C2\u0102\u00C4\u0139\u0106\u00C7\u010C\u00C9\u0118\u00CB\u011A\u00CD\u00CE\u010E\u0110\u0143\u0147\u00D3\u00D4\u0150\u00D6\u00D7\u0158\u016E\u00DA\u0170\u00DC\u00DD\u0162\u00DF\u0155\u00E1\u00E2\u0103\u00E4\u013A\u0107\u00E7\u010D\u00E9\u0119\u00EB\u011B\u00ED\u00EE\u010F\u0111\u0144\u0148\u00F3\u00F4\u0151\u00F6\u00F7\u0159\u016F\u00FA\u0171\u00FC\u00FD\u0163\u02D9";

    /// <summary>Windows Cyrillic.</summary>
    private const string Cp1251 =
        "\u0402\u0403\u201A\u0453\u201E\u2026\u2020\u2021\u20AC\u2030\u0409\u2039\u040A\u040C\u040B\u040F\u0452\u2018\u2019\u201C\u201D\u2022\u2013\u2014\uFFFD\u2122\u0459\u203A\u045A\u045C\u045B\u045F\u00A0\u040E\u045E\u0408\u00A4\u0490\u00A6\u00A7\u0401\u00A9\u0404\u00AB\u00AC\u00AD\u00AE\u0407\u00B0\u00B1\u0406\u0456\u0491\u00B5\u00B6\u00B7\u0451\u2116\u0454\u00BB\u0458\u0405\u0455\u0457\u0410\u0411\u0412\u0413\u0414\u0415\u0416\u0417\u0418\u0419\u041A\u041B\u041C\u041D\u041E\u041F\u0420\u0421\u0422\u0423\u0424\u0425\u0426\u0427\u0428\u0429\u042A\u042B\u042C\u042D\u042E\u042F\u0430\u0431\u0432\u0433\u0434\u0435\u0436\u0437\u0438\u0439\u043A\u043B\u043C\u043D\u043E\u043F\u0440\u0441\u0442\u0443\u0444\u0445\u0446\u0447\u0448\u0449\u044A\u044B\u044C\u044D\u044E\u044F";

    /// <summary>Windows Western European.</summary>
    private const string Cp1252 =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\uFFFD\u017D\uFFFD\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\uFFFD\u017E\u0178\u00A0\u00A1\u00A2\u00A3\u00A4\u00A5\u00A6\u00A7\u00A8\u00A9\u00AA\u00AB\u00AC\u00AD\u00AE\u00AF\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7\u00B8\u00B9\u00BA\u00BB\u00BC\u00BD\u00BE\u00BF\u00C0\u00C1\u00C2\u00C3\u00C4\u00C5\u00C6\u00C7\u00C8\u00C9\u00CA\u00CB\u00CC\u00CD\u00CE\u00CF\u00D0\u00D1\u00D2\u00D3\u00D4\u00D5\u00D6\u00D7\u00D8\u00D9\u00DA\u00DB\u00DC\u00DD\u00DE\u00DF\u00E0\u00E1\u00E2\u00E3\u00E4\u00E5\u00E6\u00E7\u00E8\u00E9\u00EA\u00EB\u00EC\u00ED\u00EE\u00EF\u00F0\u00F1\u00F2\u00F3\u00F4\u00F5\u00F6\u00F7\u00F8\u00F9\u00FA\u00FB\u00FC\u00FD\u00FE\u00FF";

    /// <summary>Windows Greek.</summary>
    private const string Cp1253 =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\uFFFD\u2030\uFFFD\u2039\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\uFFFD\u2122\uFFFD\u203A\uFFFD\uFFFD\uFFFD\uFFFD\u00A0\u0385\u0386\u00A3\u00A4\u00A5\u00A6\u00A7\u00A8\u00A9\uFFFD\u00AB\u00AC\u00AD\u00AE\u2015\u00B0\u00B1\u00B2\u00B3\u0384\u00B5\u00B6\u00B7\u0388\u0389\u038A\u00BB\u038C\u00BD\u038E\u038F\u0390\u0391\u0392\u0393\u0394\u0395\u0396\u0397\u0398\u0399\u039A\u039B\u039C\u039D\u039E\u039F\u03A0\u03A1\uFFFD\u03A3\u03A4\u03A5\u03A6\u03A7\u03A8\u03A9\u03AA\u03AB\u03AC\u03AD\u03AE\u03AF\u03B0\u03B1\u03B2\u03B3\u03B4\u03B5\u03B6\u03B7\u03B8\u03B9\u03BA\u03BB\u03BC\u03BD\u03BE\u03BF\u03C0\u03C1\u03C2\u03C3\u03C4\u03C5\u03C6\u03C7\u03C8\u03C9\u03CA\u03CB\u03CC\u03CD\u03CE\uFFFD";

    /// <summary>Windows Turkish.</summary>
    private const string Cp1254 =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\uFFFD\uFFFD\uFFFD\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\uFFFD\uFFFD\u0178\u00A0\u00A1\u00A2\u00A3\u00A4\u00A5\u00A6\u00A7\u00A8\u00A9\u00AA\u00AB\u00AC\u00AD\u00AE\u00AF\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7\u00B8\u00B9\u00BA\u00BB\u00BC\u00BD\u00BE\u00BF\u00C0\u00C1\u00C2\u00C3\u00C4\u00C5\u00C6\u00C7\u00C8\u00C9\u00CA\u00CB\u00CC\u00CD\u00CE\u00CF\u011E\u00D1\u00D2\u00D3\u00D4\u00D5\u00D6\u00D7\u00D8\u00D9\u00DA\u00DB\u00DC\u0130\u015E\u00DF\u00E0\u00E1\u00E2\u00E3\u00E4\u00E5\u00E6\u00E7\u00E8\u00E9\u00EA\u00EB\u00EC\u00ED\u00EE\u00EF\u011F\u00F1\u00F2\u00F3\u00F4\u00F5\u00F6\u00F7\u00F8\u00F9\u00FA\u00FB\u00FC\u0131\u015F\u00FF";

    /// <summary>Windows Hebrew.</summary>
    private const string Cp1255 =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\uFFFD\u2039\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\uFFFD\u203A\uFFFD\uFFFD\uFFFD\uFFFD\u00A0\u00A1\u00A2\u00A3\u20AA\u00A5\u00A6\u00A7\u00A8\u00A9\u00D7\u00AB\u00AC\u00AD\u00AE\u00AF\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7\u00B8\u00B9\u00F7\u00BB\u00BC\u00BD\u00BE\u00BF\u05B0\u05B1\u05B2\u05B3\u05B4\u05B5\u05B6\u05B7\u05B8\u05B9\uFFFD\u05BB\u05BC\u05BD\u05BE\u05BF\u05C0\u05C1\u05C2\u05C3\u05F0\u05F1\u05F2\u05F3\u05F4\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\u05D0\u05D1\u05D2\u05D3\u05D4\u05D5\u05D6\u05D7\u05D8\u05D9\u05DA\u05DB\u05DC\u05DD\u05DE\u05DF\u05E0\u05E1\u05E2\u05E3\u05E4\u05E5\u05E6\u05E7\u05E8\u05E9\u05EA\uFFFD\uFFFD\u200E\u200F\uFFFD";

    /// <summary>Windows Arabic - the reason this class exists.</summary>
    private const string Cp1256 =
        "\u20AC\u067E\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0679\u2039\u0152\u0686\u0698\u0688\u06AF\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u06A9\u2122\u0691\u203A\u0153\u200C\u200D\u06BA\u00A0\u060C\u00A2\u00A3\u00A4\u00A5\u00A6\u00A7\u00A8\u00A9\u06BE\u00AB\u00AC\u00AD\u00AE\u00AF\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7\u00B8\u00B9\u061B\u00BB\u00BC\u00BD\u00BE\u061F\u06C1\u0621\u0622\u0623\u0624\u0625\u0626\u0627\u0628\u0629\u062A\u062B\u062C\u062D\u062E\u062F\u0630\u0631\u0632\u0633\u0634\u0635\u0636\u00D7\u0637\u0638\u0639\u063A\u0640\u0641\u0642\u0643\u00E0\u0644\u00E2\u0645\u0646\u0647\u0648\u00E7\u00E8\u00E9\u00EA\u00EB\u0649\u064A\u00EE\u00EF\u064B\u064C\u064D\u064E\u00F4\u064F\u0650\u00F7\u0651\u00F9\u0652\u00FB\u00FC\u200E\u200F\u06D2";

    /// <summary>Windows Baltic.</summary>
    private const string Cp1257 =
        "\u20AC\uFFFD\u201A\uFFFD\u201E\u2026\u2020\u2021\uFFFD\u2030\uFFFD\u2039\uFFFD\u00A8\u02C7\u00B8\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\uFFFD\u2122\uFFFD\u203A\uFFFD\u00AF\u02DB\uFFFD\u00A0\uFFFD\u00A2\u00A3\u00A4\uFFFD\u00A6\u00A7\u00D8\u00A9\u0156\u00AB\u00AC\u00AD\u00AE\u00C6\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7\u00F8\u00B9\u0157\u00BB\u00BC\u00BD\u00BE\u00E6\u0104\u012E\u0100\u0106\u00C4\u00C5\u0118\u0112\u010C\u00C9\u0179\u0116\u0122\u0136\u012A\u013B\u0160\u0143\u0145\u00D3\u014C\u00D5\u00D6\u00D7\u0172\u0141\u015A\u016A\u00DC\u017B\u017D\u00DF\u0105\u012F\u0101\u0107\u00E4\u00E5\u0119\u0113\u010D\u00E9\u017A\u0117\u0123\u0137\u012B\u013C\u0161\u0144\u0146\u00F3\u014D\u00F5\u00F6\u00F7\u0173\u0142\u015B\u016B\u00FC\u017C\u017E\u02D9";

    /// <summary>Windows Vietnamese.</summary>
    private const string Cp1258 =
        "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\uFFFD\u2039\u0152\uFFFD\uFFFD\uFFFD\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\uFFFD\u203A\u0153\uFFFD\uFFFD\u0178\u00A0\u00A1\u00A2\u00A3\u00A4\u00A5\u00A6\u00A7\u00A8\u00A9\u00AA\u00AB\u00AC\u00AD\u00AE\u00AF\u00B0\u00B1\u00B2\u00B3\u00B4\u00B5\u00B6\u00B7\u00B8\u00B9\u00BA\u00BB\u00BC\u00BD\u00BE\u00BF\u00C0\u00C1\u00C2\u0102\u00C4\u00C5\u00C6\u00C7\u00C8\u00C9\u00CA\u00CB\u0300\u00CD\u00CE\u00CF\u0110\u00D1\u0309\u00D3\u00D4\u01A0\u00D6\u00D7\u00D8\u00D9\u00DA\u00DB\u00DC\u01AF\u0303\u00DF\u00E0\u00E1\u00E2\u0103\u00E4\u00E5\u00E6\u00E7\u00E8\u00E9\u00EA\u00EB\u0301\u00ED\u00EE\u00EF\u0111\u00F1\u0323\u00F3\u00F4\u01A1\u00F6\u00F7\u00F8\u00F9\u00FA\u00FB\u00FC\u01B0\u20AB\u00FF";

    /// <summary>Code pages this class can decode without any external provider.</summary>
    public static IReadOnlyList<int> BuiltIn { get; } =
        [437, 850, 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258];

    /// <summary>
    /// Resolves a code page, preferring a registered provider and falling back to the built-in tables.
    /// </summary>
    public static bool TryGetEncoding(int codePage, out Encoding encoding)
    {
        if (codePage == 65001)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return true;
        }

        try
        {
            // Present on .NET Framework, and on .NET Core when the host registered the provider.
            encoding = Encoding.GetEncoding(codePage);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Expected on .NET Core without the CodePages provider; fall through to our own tables.
        }

        var table = HighHalf(codePage);
        if (table is null)
        {
            encoding = Encoding.Latin1;
            return false;
        }

        encoding = new SingleByteEncoding(codePage, table);
        return true;
    }

    private static string? HighHalf(int codePage) => codePage switch
    {
        437 => Cp437,
        850 => Cp850,
        1250 => Cp1250,
        1251 => Cp1251,
        1252 => Cp1252,
        1253 => Cp1253,
        1254 => Cp1254,
        1255 => Cp1255,
        1256 => Cp1256,
        1257 => Cp1257,
        1258 => Cp1258,
        _ => null,
    };
}

/// <summary>
/// An <see cref="Encoding"/> over a 128-entry high-half table. Bytes 0x00-0x7F are ASCII in every code
/// page implemented here, which the generator asserts.
/// </summary>
internal sealed class SingleByteEncoding : Encoding
{
    private const char Replacement = '\uFFFD';

    private readonly string _highHalf;
    private readonly Dictionary<char, byte> _reverse;

    public SingleByteEncoding(int codePage, string highHalf)
    {
        CodePageNumber = codePage;
        _highHalf = highHalf;

        _reverse = new Dictionary<char, byte>(256);
        for (var i = 0; i < 0x80; i++)
        {
            _reverse[(char)i] = (byte)i;
        }

        for (var i = 0; i < highHalf.Length; i++)
        {
            // Unmapped slots decode to U+FFFD; they must not become a reverse mapping, or encoding
            // would silently turn a replacement character into a real byte.
            var c = highHalf[i];
            if (c != Replacement)
            {
                _reverse.TryAdd(c, (byte)(0x80 + i));
            }
        }
    }

    public int CodePageNumber { get; }

    public override int CodePage => CodePageNumber;

    public override string EncodingName => $"Legacy code page {CodePageNumber}";

    public override int GetByteCount(char[] chars, int index, int count) => count;

    public override int GetCharCount(byte[] bytes, int index, int count) => count;

    public override int GetMaxByteCount(int charCount) => charCount;

    public override int GetMaxCharCount(int byteCount) => byteCount;

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(bytes);

        for (var i = 0; i < charCount; i++)
        {
            bytes[byteIndex + i] = _reverse.TryGetValue(chars[charIndex + i], out var b) ? b : (byte)'?';
        }

        return charCount;
    }

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(chars);

        for (var i = 0; i < byteCount; i++)
        {
            var b = bytes[byteIndex + i];
            chars[charIndex + i] = b < 0x80 ? (char)b : _highHalf[b - 0x80];
        }

        return byteCount;
    }
}
