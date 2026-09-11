using System.Xml.Linq;
using NOBA.Archive.Core.Actions;
using Xunit;

namespace NOBA.Archive.Core.Tests;

/// <summary>
/// The generated sparse manifest, against the requirements E2 established.
/// </summary>
/// <remarks>
/// <para>
/// <b>These requirements were paid for.</b> Four Windows runs failed on this file's shape before E2
/// passed: all-<c>desktop5</c> prefixes (<c>C00CE014</c>), a certificate in the wrong store
/// (<c>0x800B0109</c>), a missing <c>AllowExternalContent</c> element described only by a comment
/// (<c>0x80073D2E</c>), and a sideloading policy the harness printed and then deployed past
/// (<c>0x80073CFF</c>). The ids below are the E2 audit's, so a failure here names the same
/// requirement the harness pre-flight would have named.
/// </para>
/// <para>
/// <b>What this cannot prove.</b> That <c>makeappx</c> accepts the file, and that Windows registers
/// it. Both need Windows. What it proves is that the file cannot regress into a shape already known
/// to fail - which is the part that does not need a machine.
/// </para>
/// </remarks>
public sealed class ShellPackageManifestTests
{
    private static readonly XNamespace F = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";

    private const string Publisher = "CN=NOBA-DEV-ONLY";

    private static string Text() =>
        ShellPackageManifest.Generate(new ShellPackageIdentity(Publisher, "0.5.0.0", "NOBA Archive", "NOBA"));

    private static XDocument Doc() => XDocument.Parse(Text());

    private static XElement Root() => Doc().Root!;

    private static XElement Application() =>
        Root().Element(F + "Applications")!.Element(F + "Application")!;

    // ---------------------------------------------------------------------------------------------
    // Identity - SHELL-065
    // ---------------------------------------------------------------------------------------------

    /// <summary>M03/SHELL-065: the name is the constant, not a copy of it.</summary>
    [Fact]
    public void The_package_name_comes_from_shell_registration()
    {
        var identity = Root().Element(F + "Identity")!;

        Assert.Equal(ShellRegistration.PackageIdentityName, identity.Attribute("Name")!.Value);
    }

    /// <summary>
    /// M15/SHELL-065: the CLSID is the one the adapter implements, and the manifest wants it bare.
    /// </summary>
    [Fact]
    public void The_clsid_is_the_registrations_clsid_without_braces()
    {
        var expected = ShellRegistration.ModernHandlerClsid.Trim('{', '}');

        var comClass = Root().Descendants(Com + "Class").Single();
        var verbs = Root().Descendants(Desktop5 + "Verb").ToList();

        Assert.Equal(expected, comClass.Attribute("Id")!.Value);
        Assert.NotEmpty(verbs);
        Assert.All(verbs, v => Assert.Equal(expected, v.Attribute("Clsid")!.Value));
    }

    /// <summary>M20: every verb points at the class the package declares. A mismatch draws nothing.</summary>
    [Fact]
    public void Every_verb_points_at_the_declared_class()
    {
        var declared = Root().Descendants(Com + "Class").Single().Attribute("Id")!.Value;

        Assert.All(
            Root().Descendants(Desktop5 + "Verb"),
            v => Assert.Equal(declared, v.Attribute("Clsid")!.Value));
    }

    /// <summary>The verb id is the constant too.</summary>
    [Fact]
    public void The_verb_id_comes_from_shell_registration()
    {
        Assert.All(
            Root().Descendants(Desktop5 + "Verb"),
            v => Assert.Equal(ShellRegistration.ModernRootVerbId, v.Attribute("Id")!.Value));
    }

    /// <summary>
    /// M04: the publisher is whatever the caller was told to sign as, verbatim. Nothing here may
    /// normalise it - <c>Add-AppxPackage</c> compares it character for character.
    /// </summary>
    [Fact]
    public void The_publisher_is_carried_through_untouched()
    {
        const string Awkward = "CN=Contoso Ltd., O=Contoso, C=GB";

        var manifest = ShellPackageManifest.Generate(
            new ShellPackageIdentity(Awkward, "1.2.3.4", "NOBA Archive", "Contoso"));

        Assert.Equal(Awkward, XDocument.Parse(manifest).Root!.Element(F + "Identity")!.Attribute("Publisher")!.Value);
    }

    /// <summary>A publisher nobody supplied is a signature failure waiting to happen, so it throws.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_publisher_is_refused(string publisher)
    {
        Assert.Throws<ArgumentException>(() =>
            ShellPackageManifest.Generate(new ShellPackageIdentity(publisher, "0.5.0.0", "n", "p")));
    }

    // ---------------------------------------------------------------------------------------------
    // The four shapes that failed on Windows
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// M06. Its absence is <c>0x80073D2E</c>, and the defect was that a comment described the element
    /// while the element itself was never written.
    /// </summary>
    [Fact]
    public void Allow_external_content_is_present_and_true()
    {
        var element = Root().Element(F + "Properties")!.Element(Uap10 + "AllowExternalContent");

        Assert.NotNull(element);
        Assert.Equal("true", element!.Value);
    }

    /// <summary>
    /// M17/M18/M21. <c>Extension</c> and <c>FileExplorerContextMenus</c> are <c>desktop4</c>;
    /// <c>ItemType</c> and <c>Verb</c> are <c>desktop5</c>. All-<c>desktop5</c> is <c>C00CE014</c>.
    /// </summary>
    [Fact]
    public void The_context_menu_prefixes_are_the_pairing_makeappx_accepts()
    {
        var root = Root();

        Assert.Single(root.Descendants(Desktop4 + "Extension"));
        Assert.Single(root.Descendants(Desktop4 + "FileExplorerContextMenus"));
        Assert.NotEmpty(root.Descendants(Desktop5 + "ItemType"));
        Assert.NotEmpty(root.Descendants(Desktop5 + "Verb"));

        // And the shapes that were rejected must be absent, not merely unused.
        Assert.Empty(root.Descendants(Desktop5 + "Extension"));
        Assert.Empty(root.Descendants(Desktop5 + "FileExplorerContextMenus"));
        Assert.Empty(root.Descendants(Desktop4 + "ItemType"));
        Assert.Empty(root.Descendants(Desktop4 + "Verb"));
    }

    /// <summary>M08: below this floor <c>AllowExternalContent</c> is not understood.</summary>
    [Fact]
    public void The_minimum_version_is_the_allow_external_content_floor()
    {
        var family = Root().Element(F + "Dependencies")!.Element(F + "TargetDeviceFamily")!;

        Assert.Equal("Windows.Desktop", family.Attribute("Name")!.Value);
        Assert.True(
            Version.Parse(family.Attribute("MinVersion")!.Value) >= Version.Parse("10.0.19041.0"),
            "MinVersion is below the AllowExternalContent floor; deployment fails 0x80073D2E");
    }

    /// <summary>M14: an in-process server would load the adapter and its CLR into explorer.exe.</summary>
    [Fact]
    public void The_com_server_is_a_surrogate_server()
    {
        Assert.Single(Root().Descendants(Com + "SurrogateServer"));
    }

    /// <summary>
    /// The surrogate is the SYSTEM one. Naming a custom executable or a well-known surrogate would
    /// change the process topology E1 and E2 measured.
    /// </summary>
    [Fact]
    public void The_surrogate_is_the_system_surrogate()
    {
        var surrogate = Root().Descendants(Com + "SurrogateServer").Single();

        Assert.Null(surrogate.Attribute("CustomSurrogateExecutable"));
        Assert.Null(surrogate.Attribute("SystemSurrogate"));
    }

    // ---------------------------------------------------------------------------------------------
    // The elevation boundary - plan section L.4
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// M22. There is no <c>RunAs</c> anywhere and there must never be. The surrogate inherits
    /// Explorer's identity, which is the standard user, and E2 criterion 3 measured exactly that.
    /// </summary>
    [Fact]
    public void Nothing_in_the_manifest_asks_to_run_as_anyone()
    {
        Assert.Empty(Root().Descendants().Attributes().Where(a =>
            a.Name.LocalName.Contains("RunAs", StringComparison.Ordinal)));
    }

    /// <summary>M11: a desktop app at medium integrity, not an app-container.</summary>
    [Fact]
    public void The_application_is_a_medium_integrity_win32_app()
    {
        var app = Application();

        Assert.Equal("mediumIL", app.Attribute(Uap10 + "TrustLevel")!.Value);
        Assert.Equal("win32App", app.Attribute(Uap10 + "RuntimeBehavior")!.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Shape
    // ---------------------------------------------------------------------------------------------

    /// <summary>M05: no payload binaries, so no architecture to be wrong about.</summary>
    [Fact]
    public void The_architecture_is_neutral()
    {
        Assert.Equal("neutral", Root().Element(F + "Identity")!.Attribute("ProcessorArchitecture")!.Value);
    }

    /// <summary>M09: both capabilities, from the canonical sparse-package shape.</summary>
    [Fact]
    public void Both_capabilities_are_declared()
    {
        var names = Root().Element(F + "Capabilities")!
            .Elements(Rescap + "Capability")
            .Select(c => c.Attribute("Name")!.Value)
            .ToList();

        Assert.Contains(names, n => n == "runFullTrust");
        Assert.Contains(names, n => n == "unvirtualizedResources");
    }

    /// <summary>M07: documented to break registration.</summary>
    [Fact]
    public void There_is_no_splash_screen()
    {
        Assert.Empty(Root().Descendants(Uap + "SplashScreen"));
    }

    /// <summary>M13: the COM server must be under Application/Extensions, not at package level.</summary>
    [Fact]
    public void The_com_server_hangs_off_the_application()
    {
        var extensions = Application().Element(F + "Extensions")!;

        Assert.Single(extensions.Elements(Com + "Extension"));
        Assert.Single(extensions.Elements(Desktop4 + "Extension"));
    }

    /// <summary>M02b: a prefix used but not ignorable makes older Windows reject the whole manifest.</summary>
    [Fact]
    public void Every_used_prefix_is_ignorable()
    {
        var declared = Root().Attribute("IgnorableNamespaces")!.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var prefix in new[] { "uap", "uap10", "rescap", "com", "desktop4", "desktop5" })
        {
            Assert.Contains(declared, d => d == prefix);
        }
    }

    /// <summary>
    /// The declaration must agree with the bytes. A <c>StringWriter</c> reports UTF-16, and
    /// <c>XDocument.Save</c> writes that into the declaration of a file that is then saved as UTF-8.
    /// </summary>
    [Fact]
    public void The_declaration_says_utf8()
    {
        Assert.True(
            Text().StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", StringComparison.Ordinal),
            "the declaration must agree with the bytes the file is written as");
    }

    // ---------------------------------------------------------------------------------------------
    // The modern surface's own shape
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// ONE verb, whatever the build can do. The submenu's contents are decided at draw time by
    /// <c>ShellMenuBuilder</c>; listing actions here would put the menu in two places.
    /// </summary>
    [Fact]
    public void There_is_exactly_one_verb_per_item_type()
    {
        foreach (var itemType in Root().Descendants(Desktop5 + "ItemType"))
        {
            Assert.Single(itemType.Elements(Desktop5 + "Verb"));
        }
    }

    /// <summary>Files and folders, so a right-click anywhere in Explorer reaches the handler.</summary>
    [Fact]
    public void The_handler_registers_for_files_and_folders()
    {
        var types = Root().Descendants(Desktop5 + "ItemType")
            .Select(t => t.Attribute("Type")!.Value)
            .ToList();

        Assert.Contains(types, t => t == "*");
        Assert.Contains(types, t => t == "Directory");
    }

    /// <summary>
    /// No format name reaches the manifest. The package is registered once and does not change when a
    /// provider is added - that is what makes the capability model the only place the menu is decided.
    /// </summary>
    /// <remarks>
    /// <b>Word-bounded, and the reason is embarrassing.</b> The first version of this matched
    /// substrings and failed on the real manifest: <c>ProcessorArchitecture</c> contains <c>rAr</c>
    /// across the join between the two words. That is the same defect SHELL-063's scan had two hours
    /// earlier, written again by the same hands after the lesson - which is precisely why the rule is
    /// a test with a control rather than a habit.
    /// </remarks>
    [Fact]
    public void The_manifest_names_no_format()
    {
        var text = Text();

        foreach (var format in FormatNames)
        {
            Assert.False(
                NameRule(format).IsMatch(text),
                $"the manifest names the format '{format}'; the package must not change when a provider does");
        }
    }

    /// <summary>The control: a manifest that did name a format must be caught.</summary>
    [Fact]
    public void The_format_rule_catches_a_manifest_that_names_one()
    {
        const string Offending = """<desktop5:Verb Id="noba.compress.zip" Clsid="..." />""";

        Assert.Contains(FormatNames, f => NameRule(f).IsMatch(Offending));
    }

    /// <summary>
    /// And the other half: the schema's own vocabulary must not trip it, or the rule gets deleted the
    /// first time it blocks a correct manifest.
    /// </summary>
    [Fact]
    public void The_format_rule_does_not_fire_on_the_schemas_own_words()
    {
        const string Legitimate = """
            <Identity ProcessorArchitecture="neutral" />
            <TargetDeviceFamily Name="Windows.Desktop" />
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
            """;

        Assert.DoesNotContain(FormatNames, f => NameRule(f).IsMatch(Legitimate));
    }

    private static readonly string[] FormatNames = ["7z", "zip", "rar", "gzip", "tar", "bzip2", "xz"];

    private static System.Text.RegularExpressions.Regex NameRule(string token) =>
        new(@"\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The application is installed by the product's own installer and already has a Start menu
    /// entry. A sparse package adding a second one is OPEN-5's duplicate from the other direction.
    /// </summary>
    [Fact]
    public void The_package_adds_no_second_start_menu_entry()
    {
        Assert.Equal("none", Application().Element(Uap + "VisualElements")!.Attribute("AppListEntry")!.Value);
    }

    /// <summary>The adapter is what the class points at, by the name the build produces.</summary>
    [Fact]
    public void The_class_points_at_the_adapter_library()
    {
        Assert.Equal(
            ShellPackageManifest.AdapterLibrary,
            Root().Descendants(Com + "Class").Single().Attribute("Path")!.Value);
    }

    /// <summary>M10: the application's executable, from the one constant that names it.</summary>
    [Fact]
    public void The_application_executable_comes_from_shell_registration()
    {
        Assert.Equal(ShellRegistration.ApplicationExecutable, Application().Attribute("Executable")!.Value);
    }

    /// <summary>Generation is deterministic: the same identity produces the same bytes.</summary>
    [Fact]
    public void Generation_is_deterministic()
    {
        Assert.Equal(Text(), Text());
    }
}
