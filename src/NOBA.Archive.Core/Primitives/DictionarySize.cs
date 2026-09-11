namespace NOBA.Archive.Core.Primitives;

/// <summary>
/// A dictionary size in bytes: always a power of two within the range a compressor can accept.
/// Memory budgeting uses this, so it must never come straight from archive metadata.
/// </summary>
public readonly record struct DictionarySize
{
    public const long MinBytes = 64L * 1024;
    public const long MaxBytes = 1536L * 1024 * 1024;

    public long Bytes { get; }

    private DictionarySize(long bytes) => Bytes = bytes;

    public static DictionarySize Default => new(16L * 1024 * 1024);

    public static bool TryCreate(long bytes, out DictionarySize size)
    {
        if (bytes < MinBytes || bytes > MaxBytes || !IsPowerOfTwo(bytes))
        {
            size = default;
            return false;
        }

        size = new DictionarySize(bytes);
        return true;
    }

    /// <summary>Rounds down to the nearest legal power of two inside the accepted range.</summary>
    public static DictionarySize Clamp(long bytes)
    {
        var clamped = Math.Clamp(bytes, MinBytes, MaxBytes);
        long result = MinBytes;
        while (result * 2 <= clamped)
        {
            result *= 2;
        }

        return new DictionarySize(result);
    }

    private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;

    public ByteSize AsByteSize() => ByteSize.FromBytes(Bytes);

    public override string ToString() => ByteSize.FromBytes(Bytes).ToString();
}
