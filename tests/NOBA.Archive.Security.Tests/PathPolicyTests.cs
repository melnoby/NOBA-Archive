using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Security;
using Xunit;

namespace NOBA.Archive.Security.Tests;

/// <summary>
/// The SEC-001..SEC-013 family from THREAT_MODEL.md, at the policy level. These run on any OS: the
/// destination root is built from the platform's own temp path rather than a hard-coded drive letter.
/// </summary>
public sealed class PathPolicyTests
{
    private static string Destination => Path.Combine(Path.GetTempPath(), "noba-dest");

    private readonly PathPolicy _policy = new();

    // SEC-001
    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData("../../../etc/passwd")]
    [InlineData("safe/../../escape.txt")]
    public void Blocks_traversal(string raw)
    {
        var result = _policy.Validate(raw, Destination, IllegalNamePolicy.Sanitize);

        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Rejection);
        Assert.Equal(PathRejectionReason.TraversalSegment, result.Rejection.Reason);
    }

    // SEC-002
    [Theory]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData("/etc/shadow")]
    [InlineData(@"\\attacker\share\payload.exe")]
    public void Blocks_rooted_unc_and_device_paths(string raw)
    {
        var result = _policy.Validate(raw, Destination, IllegalNamePolicy.Sanitize);

        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Rejection);
    }

    // SEC-003 - the CVE-2025-8088 shape.
    [Fact]
    public void Blocks_alternate_data_stream_names()
    {
        var result = _policy.Validate("readme.txt:payload.ps1", Destination, IllegalNamePolicy.Sanitize);

        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Rejection);
        Assert.Equal(PathRejectionReason.AlternateDataStream, result.Rejection.Reason);
    }

    // SEC-012 - a prefix comparison alone would wrongly accept the sibling folder.
    [Fact]
    public void Does_not_treat_a_sibling_folder_as_inside_the_destination()
    {
        var root = Path.Combine(Path.GetTempPath(), "out");
        var sibling = Path.Combine(Path.GetTempPath(), "out-evil", "payload.dll");

        Assert.False(_policy.IsInsideDestination(sibling, root));
        Assert.True(_policy.IsInsideDestination(Path.Combine(root, "inner", "file.txt"), root));
        Assert.True(_policy.IsInsideDestination(root, root));
    }

    // SEC-006 - reserved names are a Win32 impossibility, not an escape attempt.
    [Theory]
    [InlineData("CON")]
    [InlineData("aux.h")]
    [InlineData("LPT1.txt")]
    public void Sanitizes_reserved_device_names_instead_of_blocking_them(string name)
    {
        var result = _policy.Validate(name, Destination, IllegalNamePolicy.Sanitize);

        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        Assert.True(result.WasSanitized);
        Assert.NotNull(result.Path);
        var finalSegment = Path.GetFileName(result.Path.Value.FullPath);
        Assert.NotEqual(name, finalSegment);
    }

    [Fact]
    public void Skips_illegal_names_when_the_host_asks_for_that_policy()
    {
        var result = _policy.Validate("CON", Destination, IllegalNamePolicy.Skip);

        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Rejection);
    }

    [Theory]
    [InlineData("report.")]
    [InlineData("report ")]
    public void Trims_trailing_dots_and_spaces(string name)
    {
        var result = _policy.Validate(name, Destination, IllegalNamePolicy.Sanitize);

        Assert.True(result.IsAccepted);
        Assert.True(result.WasSanitized);
        Assert.NotNull(result.Path);
        Assert.EndsWith("report", result.Path.Value.FullPath);
    }

    [Fact]
    public void Accepts_an_ordinary_nested_path_untouched()
    {
        var result = _policy.Validate("docs/reports/q3.pdf", Destination, IllegalNamePolicy.Sanitize);

        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        Assert.False(result.WasSanitized);
        Assert.NotNull(result.Path);
        Assert.True(_policy.IsInsideDestination(result.Path.Value.FullPath, Destination));
    }

    [Fact]
    public void Accepts_arabic_file_names()
    {
        var result = _policy.Validate("مستندات/تقرير.pdf", Destination, IllegalNamePolicy.Sanitize);

        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        Assert.False(result.WasSanitized);
    }

    // SEC-004 - a link target is attacker data too.
    [Fact]
    public void Rejects_link_targets_that_leave_the_destination()
    {
        var inside = Path.Combine(Destination, "sub");

        Assert.False(_policy.IsLinkTargetInsideDestination(@"..\..\Windows", inside, Destination));
        Assert.False(_policy.IsLinkTargetInsideDestination("/etc/passwd", inside, Destination));
        Assert.False(_policy.IsLinkTargetInsideDestination(string.Empty, inside, Destination));
    }

    [Fact]
    public void Accepts_a_link_target_that_stays_inside()
    {
        var inside = Path.Combine(Destination, "sub");

        Assert.True(_policy.IsLinkTargetInsideDestination("sibling.txt", inside, Destination));
    }

    [Fact]
    public void Rejects_a_segment_longer_than_the_limit()
    {
        var raw = new string('a', 300) + ".txt";

        var result = _policy.Validate(raw, Destination, IllegalNamePolicy.Sanitize);

        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Rejection);
        Assert.Equal(PathRejectionReason.TooLong, result.Rejection.Reason);
    }

    [Fact]
    public void Requires_a_destination_root()
    {
        Assert.Throws<ArgumentException>(
            () => _policy.Validate("file.txt", string.Empty, IllegalNamePolicy.Sanitize));
    }
}
