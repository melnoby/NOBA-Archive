using System.Globalization;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// The design system, checked rather than described: <c>DES-001…006</c>.
/// </summary>
/// <remarks>
/// <c>docs/DESIGN_TOKENS.md</c> §9 listed four things that had to be true "when the first UI surface is
/// built". This file is three of them; the fourth (rendering the file list in Arabic with long paths)
/// needs a running window and belongs to the Windows run.
/// </remarks>
public sealed class DesignFoundationTests
{
    /// <summary>
    /// DES-001: every pair the product renders meets WCAG AA, in every shipped theme.
    /// </summary>
    /// <remarks>
    /// The palette has claimed AA since Phase 1 and nothing had ever computed it. Translucent tokens
    /// are composited first — a selection colour at 24% alpha measured as though it were opaque answers
    /// a question the user never asks.
    /// </remarks>
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void Every_shipped_colour_pair_meets_AA(ThemeMode theme)
    {
        var failures = new List<string>();

        foreach (var pair in Contrast.ShippedPairs)
        {
            var ratio = Contrast.Measure(pair, theme);
            if (ratio < pair.Required)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pair.Foreground} on {pair.Background}: {ratio:F2}:1, needs {pair.Required:F1}:1"));
            }
        }

        Assert.True(failures.Count == 0, $"{theme}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>DES-002: both themes define exactly the same semantic vocabulary.</summary>
    /// <remarks>
    /// A token present in one theme and missing from the other is a control that renders in one and
    /// throws — or worse, falls back to a platform default — in the other.
    /// </remarks>
    [Fact]
    public void Both_themes_define_the_same_tokens()
    {
        var dark = DesignTokens.Colors(ThemeMode.Dark).Keys.OrderBy(static k => k, StringComparer.Ordinal);
        var light = DesignTokens.Colors(ThemeMode.Light).Keys.OrderBy(static k => k, StringComparer.Ordinal);

        Assert.Equal(dark, light);
    }

    /// <summary>DES-003: Arabic gets a taller line box at every size.</summary>
    /// <remarks>
    /// Diacritics and descenders collide at Latin metrics. This is the assertion that stops the ramp
    /// being "simplified" into one line height per size by someone who only ever renders Latin.
    /// </remarks>
    [Fact]
    public void Arabic_line_height_exceeds_latin_at_every_size()
    {
        foreach (var step in DesignTokens.TypeRamp)
        {
            Assert.True(
                step.LineHeightFor(UiScript.Arabic) > step.LineHeightFor(UiScript.Latin),
                $"{step.Name}: Arabic {step.LineHeightFor(UiScript.Arabic)} vs Latin {step.LineHeightFor(UiScript.Latin)}");
        }

        Assert.True(DesignTokens.FileListRowHeight(UiScript.Arabic) > DesignTokens.FileListRowHeight(UiScript.Latin));
    }

    /// <summary>DES-004: the checked-in WinUI dictionary is what the generator produces.</summary>
    /// <remarks>
    /// The dictionary has to exist as a file for the XAML project to compile, which makes it a second
    /// copy of the palette. This test is what stops the copy drifting — and it runs on a machine that
    /// cannot build WinUI at all, which is the point.
    /// </remarks>
    [Fact]
    public void The_generated_resource_dictionary_is_current()
    {
        var path = ResourceDictionaryPath();
        Assert.True(File.Exists(path), $"The generated dictionary is missing: {path}");

        var onDisk = File.ReadAllText(path).ReplaceLineEndings("\n");
        var generated = ResourceDictionaryWriter.Generate();

        Assert.True(
            string.Equals(onDisk, generated, StringComparison.Ordinal),
            "Themes.generated.xaml is out of date. Regenerate it from DesignTokens rather than editing it.");
    }

    /// <summary>DES-005: no primitive is reachable from outside the token layer.</summary>
    /// <remarks>
    /// ADR-0011's rule is that no component names a raw colour. Here it is a structural fact — the
    /// primitives are private fields — and this test is what notices if one is ever promoted to public
    /// "just for one screen".
    /// </remarks>
    [Fact]
    public void No_primitive_is_publicly_reachable()
    {
        var exposed = typeof(DesignTokens)
            .GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static m => m.Name.Contains("Blue", StringComparison.Ordinal)
                            || m.Name.Contains("Neutral", StringComparison.Ordinal)
                            || m.Name.Contains("Red", StringComparison.Ordinal)
                            || m.Name.Contains("Green", StringComparison.Ordinal)
                            || m.Name.Contains("Amber", StringComparison.Ordinal))
            .Select(static m => m.Name)
            .ToList();

        Assert.True(exposed.Count == 0, $"primitives exposed: {string.Join(", ", exposed)}");
    }

    /// <summary>DES-006: token names map to XAML keys predictably.</summary>
    [Fact]
    public void Token_keys_are_pascal_cased_with_a_product_prefix()
    {
        Assert.Equal("NobaSurfaceRaised", ResourceDictionaryWriter.KeyFor("surface.raised"));
        Assert.Equal("NobaTextPrimary", ResourceDictionaryWriter.KeyFor("text.primary"));
        Assert.Equal("NobaSpace4", ResourceDictionaryWriter.KeyFor("space.4"));
    }

    internal static string ResourceDictionaryPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NOBA.Archive.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "NOBA.Archive.App", "Themes.generated.xaml");
    }
}
