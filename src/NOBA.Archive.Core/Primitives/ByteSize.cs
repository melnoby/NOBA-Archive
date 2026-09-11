using System.Globalization;

namespace NOBA.Archive.Core.Primitives;

/// <summary>A non-negative quantity of bytes. Formatting lives here, never in the UI.</summary>
public readonly record struct ByteSize : IComparable<ByteSize>
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public long Bytes { get; }

    public ByteSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Bytes = bytes;
    }

    public static ByteSize Zero => new(0);

    public static ByteSize FromBytes(long bytes) => new(bytes);

    public static ByteSize FromKibibytes(long value) => new(checked(value * 1024L));

    public static ByteSize FromMebibytes(long value) => new(checked(value * 1024L * 1024L));

    public static ByteSize FromGibibytes(long value) => new(checked(value * 1024L * 1024L * 1024L));

    public bool IsZero => Bytes == 0;

    public int CompareTo(ByteSize other) => Bytes.CompareTo(other.Bytes);

    public static bool operator <(ByteSize left, ByteSize right) => left.Bytes < right.Bytes;

    public static bool operator >(ByteSize left, ByteSize right) => left.Bytes > right.Bytes;

    public static bool operator <=(ByteSize left, ByteSize right) => left.Bytes <= right.Bytes;

    public static bool operator >=(ByteSize left, ByteSize right) => left.Bytes >= right.Bytes;

    public static ByteSize operator +(ByteSize left, ByteSize right) => new(checked(left.Bytes + right.Bytes));

    public ByteSize Add(ByteSize other) => this + other;

    /// <summary>Human-readable form, e.g. <c>1.4 GB</c>. Culture-aware by design (Arabic UI included).</summary>
    public string ToDisplayString(IFormatProvider? formatProvider = null)
    {
        formatProvider ??= CultureInfo.CurrentCulture;
        if (Bytes < 1024)
        {
            return string.Create(formatProvider, $"{Bytes} {Units[0]}");
        }

        double value = Bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var decimals = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return string.Create(formatProvider, $"{Math.Round(value, decimals)} {Units[unit]}");
    }

    public override string ToString() => ToDisplayString(CultureInfo.InvariantCulture);
}
