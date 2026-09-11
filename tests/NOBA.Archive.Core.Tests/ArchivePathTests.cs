using NOBA.Archive.Core.Primitives;
using Xunit;

namespace NOBA.Archive.Core.Tests;

/// <summary>
/// ArchivePath is the first gate every attacker-supplied path passes through. These cases mirror the
/// traversal class in THREAT_MODEL.md T1.
/// </summary>
public sealed class ArchivePathTests
{
    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData("../../etc/passwd")]
    [InlineData("docs/../../escape.txt")]
    [InlineData("....//escape.txt")]
    [InlineData("a/../../b")]
    public void Rejects_traversal_segments(string raw)
    {
        var ok = ArchivePath.TryCreate(raw, out _, out var rejection);

        Assert.False(ok, $"'{raw}' must not produce a usable path.");
        Assert.NotNull(rejection);
        Assert.Equal(PathRejectionReason.TraversalSegment, rejection.Reason);
    }

    [Theory]
    [InlineData(@"C:\Windows\evil.dll", PathRejectionReason.DriveRelativePath)]
    [InlineData("C:evil.dll", PathRejectionReason.DriveRelativePath)]
    [InlineData("/etc/passwd", PathRejectionReason.RootedPath)]
    [InlineData(@"\Windows\evil.dll", PathRejectionReason.RootedPath)]
    [InlineData(@"\\server\share\evil.dll", PathRejectionReason.UncPath)]
    public void Rejects_rooted_and_unc_paths(string raw, PathRejectionReason expected)
    {
        var ok = ArchivePath.TryCreate(raw, out _, out var rejection);

        Assert.False(ok);
        Assert.NotNull(rejection);
        Assert.Equal(expected, rejection.Reason);
    }

    /// <summary>CVE-2025-8088 in miniature: the payload rides in an alternate data stream name.</summary>
    [Theory]
    [InlineData("readme.txt:payload.ps1")]
    [InlineData("docs/notes.txt:hidden:$DATA")]
    public void Rejects_alternate_data_stream_names(string raw)
    {
        var ok = ArchivePath.TryCreate(raw, out _, out var rejection);

        Assert.False(ok);
        Assert.NotNull(rejection);
        Assert.Equal(PathRejectionReason.AlternateDataStream, rejection.Reason);
    }

    [Fact]
    public void Rejects_control_characters()
    {
        var ok = ArchivePath.TryCreate("do\u0000cs/file.txt", out _, out var rejection);

        Assert.False(ok);
        Assert.NotNull(rejection);
        Assert.Equal(PathRejectionReason.ControlCharacter, rejection.Reason);
    }

    [Fact]
    public void Rejects_paths_deeper_than_the_limit()
    {
        var raw = string.Join('/', Enumerable.Repeat("d", ArchivePath.MaxDepth + 5));

        var ok = ArchivePath.TryCreate(raw, out _, out var rejection);

        Assert.False(ok);
        Assert.NotNull(rejection);
        Assert.Equal(PathRejectionReason.TooDeep, rejection.Reason);
    }

    [Theory]
    [InlineData("", PathRejectionReason.Empty)]
    [InlineData("   ", PathRejectionReason.Empty)]
    [InlineData("./", PathRejectionReason.Empty)]
    public void Rejects_empty_paths(string raw, PathRejectionReason expected)
    {
        var ok = ArchivePath.TryCreate(raw, out _, out var rejection);

        Assert.False(ok);
        Assert.NotNull(rejection);
        Assert.Equal(expected, rejection.Reason);
    }

    [Theory]
    [InlineData(@"docs\report.pdf", "docs/report.pdf")]
    [InlineData("docs//report.pdf", "docs/report.pdf")]
    [InlineData("./docs/./report.pdf", "docs/report.pdf")]
    [InlineData("report.pdf", "report.pdf")]
    public void Normalises_accepted_paths(string raw, string expected)
    {
        var ok = ArchivePath.TryCreate(raw, out var path, out var rejection);

        Assert.True(ok, rejection?.Detail);
        Assert.Equal(expected, path.Value);
    }

    /// <summary>Arabic, CJK, emoji and spaces are ordinary content, not something to defend against.</summary>
    [Theory]
    [InlineData("مستندات/تقرير سنوي.pdf")]
    [InlineData("文档/报告.pdf")]
    [InlineData("archive 🗜️/data (1) [copy].bin")]
    [InlineData("very.long.name.with.many.dots.tar.gz")]
    public void Accepts_legitimate_unicode_and_spaces(string raw)
    {
        var ok = ArchivePath.TryCreate(raw, out var path, out var rejection);

        Assert.True(ok, rejection?.Detail);
        Assert.False(path.IsEmpty);
    }

    [Fact]
    public void Exposes_file_name_directory_and_extension()
    {
        Assert.True(ArchivePath.TryCreate("docs/reports/q3.final.PDF", out var path, out _));

        Assert.Equal("q3.final.PDF", path.FileName);
        Assert.Equal("docs/reports", path.DirectoryPath);
        Assert.Equal(".pdf", path.Extension);
        Assert.Equal(3, path.Segments.Length);
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    public void Reports_no_extension_when_there_is_none(string raw)
    {
        Assert.True(ArchivePath.TryCreate(raw, out var path, out _));

        Assert.Equal(string.Empty, path.Extension);
    }
}
