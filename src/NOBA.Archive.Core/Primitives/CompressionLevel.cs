namespace NOBA.Archive.Core.Primitives;

/// <summary>A 0-9 compression level, clamped at construction and mapped per format by the provider.</summary>
public readonly record struct CompressionLevel : IComparable<CompressionLevel>
{
    public const int MinValue = 0;
    public const int MaxValue = 9;

    public int Value { get; }

    public CompressionLevel(int value) => Value = Math.Clamp(value, MinValue, MaxValue);

    public static CompressionLevel Store => new(0);

    public static CompressionLevel Fastest => new(1);

    public static CompressionLevel Normal => new(5);

    public static CompressionLevel Maximum => new(9);

    public bool IsStoreOnly => Value == MinValue;

    public int CompareTo(CompressionLevel other) => Value.CompareTo(other.Value);

    public static bool operator <(CompressionLevel left, CompressionLevel right) => left.Value < right.Value;

    public static bool operator >(CompressionLevel left, CompressionLevel right) => left.Value > right.Value;

    public static bool operator <=(CompressionLevel left, CompressionLevel right) => left.Value <= right.Value;

    public static bool operator >=(CompressionLevel left, CompressionLevel right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
