using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Operations;
using NOBA.Archive.Core.Primitives;
using Xunit;

namespace NOBA.Archive.Core.Tests;

public sealed class OperationProgressTests
{
    private static OperationProgress Base() => new()
    {
        Id = OperationId.Create(OperationType.Extract, DateTimeOffset.UnixEpoch, 1),
        Type = OperationType.Extract,
        State = OperationState.Running,
    };

    /// <summary>ADR-0007: an unknown total stays unknown. No invented percentage, ever.</summary>
    [Fact]
    public void Reports_indeterminate_when_no_total_is_known()
    {
        var progress = Base() with { BytesDone = 5_000, ItemsDone = 3 };

        Assert.Null(progress.Percent);
        Assert.True(progress.IsIndeterminate);
        Assert.Null(progress.Eta);
    }

    [Fact]
    public void Computes_percent_from_bytes_when_the_total_is_known()
    {
        var progress = Base() with { BytesDone = 250, BytesTotal = 1000 };

        Assert.Equal(25d, progress.Percent);
        Assert.False(progress.IsIndeterminate);
    }

    [Fact]
    public void Falls_back_to_item_counts_when_byte_totals_are_unknown()
    {
        var progress = Base() with { ItemsDone = 1, ItemsTotal = 4 };

        Assert.Equal(25d, progress.Percent);
    }

    [Fact]
    public void Computes_eta_only_once_throughput_is_known()
    {
        var withoutRate = Base() with { BytesDone = 100, BytesTotal = 1000 };
        var withRate = withoutRate with { AverageBytesPerSecond = 100 };

        Assert.Null(withoutRate.Eta);
        Assert.Equal(TimeSpan.FromSeconds(9), withRate.Eta);
    }

    [Fact]
    public void Never_reports_more_than_one_hundred_percent()
    {
        var progress = Base() with { BytesDone = 5_000, BytesTotal = 1_000 };

        Assert.Equal(100d, progress.Percent);
    }
}

public sealed class CompressionOptionsTests
{
    [Theory]
    [InlineData(CompressionProfile.Fast, 1)]
    [InlineData(CompressionProfile.Balanced, 5)]
    [InlineData(CompressionProfile.MaximumCompression, 9)]
    [InlineData(CompressionProfile.SmallestSize, 9)]
    public void Maps_profiles_to_levels(CompressionProfile profile, int expectedLevel)
    {
        var options = CompressionOptions.ForProfile(ArchiveFormat.SevenZip, profile);

        Assert.Equal(expectedLevel, options.Level.Value);
        Assert.Equal(profile, options.Profile);
    }

    [Fact]
    public void Smallest_size_turns_on_solid_compression()
    {
        Assert.True(CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.SmallestSize).Solid);
        Assert.False(CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast).Solid);
    }

    /// <summary>What the UI uses to decide whether a backend can honour the chosen settings.</summary>
    [Fact]
    public void Derives_the_capabilities_the_options_demand()
    {
        var options = new CompressionOptions
        {
            Format = ArchiveFormat.SevenZip,
            Encryption = EncryptionAlgorithm.Aes256,
            EncryptFileNames = true,
            Solid = true,
            VolumeSize = ByteSize.FromMebibytes(100),
        };

        var required = options.RequiredCapabilities();

        Assert.True(required.Has(ArchiveCapabilities.Create));
        Assert.True(required.Has(ArchiveCapabilities.Encrypt));
        Assert.True(required.Has(ArchiveCapabilities.EncryptFileNames));
        Assert.True(required.Has(ArchiveCapabilities.SolidCompression));
        Assert.True(required.Has(ArchiveCapabilities.SplitVolumes));
        Assert.True(options.RequiresPassword);
    }

    [Fact]
    public void A_plain_archive_demands_only_creation()
    {
        var required = new CompressionOptions { Format = ArchiveFormat.Zip }.RequiredCapabilities();

        Assert.Equal(ArchiveCapabilities.Create, required);
    }
}

public sealed class ArchiveFormatTests
{
    [Theory]
    [InlineData("backup.zip", "zip")]
    [InlineData("BACKUP.7Z", "7z")]
    [InlineData("archive.tar.gz", "gzip")]
    [InlineData("data.rar", "rar")]
    public void Guesses_a_format_from_the_extension(string fileName, string expectedId)
    {
        var format = ArchiveFormat.GuessFromExtension(fileName);

        Assert.NotNull(format);
        Assert.Equal(expectedId, format.Id);
    }

    [Theory]
    [InlineData("noextension")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void Returns_nothing_for_an_unknown_extension(string fileName)
    {
        Assert.Null(ArchiveFormat.GuessFromExtension(fileName));
    }

    [Fact]
    public void Capability_flags_compose_and_answer_precisely()
    {
        const ArchiveCapabilities rarLike = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.ReadSplitVolumes;

        Assert.True(rarLike.Has(ArchiveCapabilities.Extract));
        Assert.True(rarLike.Has(ArchiveCapabilities.Open | ArchiveCapabilities.Test));
        Assert.False(rarLike.Has(ArchiveCapabilities.Create));
        Assert.False(rarLike.Has(ArchiveCapabilities.AddEntries));
    }
}
