using System.Globalization;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using Xunit;

namespace NOBA.Archive.Core.Tests;

public sealed class ByteSizeTests
{
    [Fact]
    public void Rejects_negative_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ByteSize(-1).Bytes);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1073741824L, "1 GB")]
    public void Formats_human_readable_sizes(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.FromBytes(bytes).ToDisplayString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Compares_and_adds()
    {
        var small = ByteSize.FromKibibytes(1);
        var large = ByteSize.FromMebibytes(1);

        Assert.True(small < large);
        Assert.True(large >= small);
        Assert.Equal(1024L * 1024 + 1024, small.Add(large).Bytes);
    }
}

public sealed class OperationIdTests
{
    [Fact]
    public void Formats_the_documented_shape()
    {
        var id = OperationId.Create(OperationType.Extract, new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero), 142);

        Assert.Equal("EX-20260819-000142", id.Value);
    }

    [Theory]
    [InlineData(OperationType.Extract, "EX")]
    [InlineData(OperationType.Create, "CR")]
    [InlineData(OperationType.Test, "TS")]
    [InlineData(OperationType.Search, "SR")]
    public void Uses_a_distinct_prefix_per_type(OperationType type, string expected)
    {
        Assert.Equal(expected, OperationId.Prefix(type));
    }

    [Fact]
    public void Round_trips_through_parsing()
    {
        var id = OperationId.Create(OperationType.Create, DateTimeOffset.UnixEpoch, 7);

        Assert.True(OperationId.TryParse(id.Value, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("EX-2026-1")]
    [InlineData("")]
    public void Refuses_malformed_identifiers(string candidate)
    {
        Assert.False(OperationId.TryParse(candidate, out _));
    }
}

public sealed class PasswordTests
{
    /// <summary>The whole point of the type: it cannot leak through interpolation or a log call.</summary>
    [Fact]
    public void Never_reveals_itself_through_ToString()
    {
        using var password = Password.FromChars("correct horse battery staple");

        Assert.Equal("***", password.ToString());
        Assert.DoesNotContain("horse", $"{password}");
    }

    [Fact]
    public void Compares_in_constant_time()
    {
        using var a = Password.FromChars("hunter2");
        using var b = Password.FromChars("hunter2");
        using var c = Password.FromChars("hunter3");

        Assert.True(a.Matches(b));
        Assert.False(a.Matches(c));
    }

    [Fact]
    public void Zeroes_its_buffer_on_disposal()
    {
        var password = Password.FromChars("secret");
        var lengthBefore = password.ByteLength;
        password.Dispose();

        Assert.True(lengthBefore > 0);
        Assert.True(password.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => password.Use(static bytes => bytes.Length));
    }

    [Fact]
    public void Handles_non_ascii_passwords()
    {
        using var password = Password.FromChars("كلمة المرور");

        Assert.True(password.ByteLength > "كلمة المرور".Length);
    }
}

public sealed class CompressionPrimitiveTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(99, 9)]
    public void Clamps_compression_levels(int input, int expected)
    {
        Assert.Equal(expected, new CompressionLevel(input).Value);
    }

    [Fact]
    public void Accepts_only_powers_of_two_for_dictionary_sizes()
    {
        Assert.True(DictionarySize.TryCreate(64 * 1024 * 1024, out _));
        Assert.False(DictionarySize.TryCreate((64 * 1024 * 1024) + 1, out _));
        Assert.False(DictionarySize.TryCreate(1024, out _));
    }

    [Fact]
    public void Clamp_rounds_down_to_a_legal_power_of_two()
    {
        var size = DictionarySize.Clamp((100 * 1024 * 1024) + 7);

        Assert.Equal(64L * 1024 * 1024, size.Bytes);
    }
}
