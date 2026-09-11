using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace NOBA.Archive.Core.Actions;

/// <summary>
/// Who is publishing the sparse package, and as what version.
/// </summary>
/// <param name="PublisherSubject">
/// The signing certificate's subject, character for character.
/// <b>This is the trap that cost a Windows run.</b> <c>Add-AppxPackage</c> fails with a signature
/// error naming neither the manifest nor the certificate when these differ, so the value is supplied
/// by whoever holds the certificate rather than assumed here. Development signs as
/// <c>CN=NOBA-DEV-ONLY</c>; Phase 6 owns procurement of anything else.
/// </param>
/// <param name="Version">Four-part, as the schema requires.</param>
/// <param name="DisplayName">The package's display name.</param>
/// <param name="PublisherDisplayName">The publisher's display name.</param>
public sealed record ShellPackageIdentity(
    string PublisherSubject,
    string Version,
    string DisplayName,
    string PublisherDisplayName);

/// <summary>
/// Generates the sparse package manifest that registers the modern context-menu handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>SHELL-065: this file is generated, never hand-written.</b> The package name, the CLSID and the
/// verb id come from <see cref="ShellRegistration"/>; a manifest typed alongside those constants is a
/// second copy of the product's identity, and the failure mode is a handler that registers cleanly
/// and never activates.
/// </para>
/// <para>
/// <b>The shape is the one E2 PROVED, not the one the plan sketched.</b> Plan section F.1 shows
/// <c>desktop5:Extension</c>; <c>makeappx</c> rejects that with <c>C00CE014</c>, because
/// <c>Extension</c> and <c>FileExplorerContextMenus</c> shipped in <c>desktop4</c> while
/// <c>ItemType</c> was added in <c>desktop5</c>. The mixed prefixes below are correct and were paid
/// for: they are what E2 attempt 1 failed on and what the passing 2026-08-31 run used
/// (DELTA sections 9 and 18).
/// </para>
/// <para>
/// <b>ONE verb, not one per action.</b> The classic surface registers a key per entry because the
/// registry has no other way to show a submenu. The modern root returns <c>ECF_HASSUBCOMMANDS</c> and
/// supplies its children from <c>EnumSubCommands</c>, so the manifest declares exactly one verb and
/// the menu's contents are decided at draw time by <c>ShellMenuBuilder</c>. Listing actions here
/// would put the menu in two places again - which is defect D1's whole family.
/// </para>
/// </remarks>
public static class ShellPackageManifest
{
    /// <summary>The adapter DLL, as installed beside the application.</summary>
    public const string AdapterLibrary = "NOBA.Archive.ShellAdapter.dll";

    /// <summary>
    /// The managed bridge, which must sit BESIDE the adapter.
    /// </summary>
    /// <remarks>
    /// Not referenced by the manifest - the package names only the COM class's Path - but it is half
    /// of the payload contract, and naming it here is what lets the build script and the register
    /// script be tested against one source rather than against each other.
    /// <c>ClrHost.cpp</c> resolves this file and its <c>.runtimeconfig.json</c> relative to the
    /// adapter's own module directory, so "beside" is literal.
    /// </remarks>
    public const string BridgeLibrary = "NOBA.Archive.ShellBridge.dll";

    /// <summary>
    /// The command-line host, which <c>Invoke</c> starts for the headless verbs.
    /// </summary>
    /// <remarks>
    /// The adapter resolves the executable the launch plan names relative to its own directory, so a
    /// payload without this draws a correct menu whose entries then start nothing. That failure looks
    /// like the handler being broken and is not.
    /// </remarks>
    public const string CommandLineHost = ShellRegistration.CommandLineExecutable;

    /// <summary>
    /// The floor <c>uap10:AllowExternalContent</c> requires. Below it the element is not understood
    /// and deployment fails with <c>0x80073D2E</c>.
    /// </summary>
    public const string MinimumWindowsVersion = "10.0.19041.0";

    /// <summary>The newest build the package has actually been deployed on.</summary>
    public const string MaxVersionTested = "10.0.26200.0";

    private static readonly XNamespace F = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";

    /// <summary>The item types the handler registers for: every file, and folders.</summary>
    private static readonly string[] ItemTypes = ["*", "Directory"];

    /// <summary>
    /// The manifest, as XML text ready to write beside the payload.
    /// </summary>
    public static string Generate(ShellPackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.PublisherSubject))
        {
            throw new ArgumentException(
                "The publisher subject must be the signing certificate's subject exactly; " +
                "Add-AppxPackage fails with a signature error that names neither when they differ.",
                nameof(identity));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(Preamble),
            new XElement(F + "Package",
                new XAttribute("xmlns", F.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "uap", Uap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "uap10", Uap10.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "rescap", Rescap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "com", Com.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "desktop4", Desktop4.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "desktop5", Desktop5.NamespaceName),
                new XAttribute("IgnorableNamespaces", "uap uap10 rescap com desktop4 desktop5"),

                new XElement(F + "Identity",
                    new XAttribute("Name", ShellRegistration.PackageIdentityName),
                    new XAttribute("Publisher", identity.PublisherSubject),
                    new XAttribute("Version", identity.Version),
                    // Neutral because the package carries no payload binaries - they live at the
                    // external location. Naming an architecture would make the package refuse to
                    // deploy on the others for no reason.
                    new XAttribute("ProcessorArchitecture", "neutral")),

                new XElement(F + "Properties",
                    new XElement(F + "DisplayName", identity.DisplayName),
                    new XElement(F + "PublisherDisplayName", identity.PublisherDisplayName),
                    new XElement(F + "Logo", @"Assets\StoreLogo.png"),
                    // THE ELEMENT WHOSE ABSENCE COST A WINDOWS RUN. Without it,
                    // Add-AppxPackage -ExternalLocation fails 0x80073D2E - and a comment describing
                    // it is not it (DELTA section 11).
                    new XElement(Uap10 + "AllowExternalContent", "true")),

                new XElement(F + "Dependencies",
                    new XElement(F + "TargetDeviceFamily",
                        new XAttribute("Name", "Windows.Desktop"),
                        new XAttribute("MinVersion", MinimumWindowsVersion),
                        new XAttribute("MaxVersionTested", MaxVersionTested))),

                new XElement(F + "Resources",
                    new XElement(F + "Resource", new XAttribute("Language", "en-us"))),

                new XElement(F + "Capabilities",
                    new XElement(Rescap + "Capability", new XAttribute("Name", "runFullTrust")),
                    new XElement(Rescap + "Capability", new XAttribute("Name", "unvirtualizedResources"))),

                new XElement(F + "Applications",
                    new XElement(F + "Application",
                        new XAttribute("Id", "NobaArchive"),
                        new XAttribute("Executable", ShellRegistration.ApplicationExecutable),
                        new XAttribute(Uap10 + "TrustLevel", "mediumIL"),
                        new XAttribute(Uap10 + "RuntimeBehavior", "win32App"),

                        new XElement(Uap + "VisualElements",
                            new XAttribute("DisplayName", identity.DisplayName),
                            new XAttribute("Description", identity.DisplayName),
                            new XAttribute("BackgroundColor", "transparent"),
                            new XAttribute("Square150x150Logo", @"Assets\Square150x150Logo.png"),
                            new XAttribute("Square44x44Logo", @"Assets\Square44x44Logo.png"),
                            // The application is installed by the product's own installer and already
                            // has a Start menu entry. A sparse package that added a second one would
                            // be OPEN-5's duplicate, arriving from the other direction.
                            new XAttribute("AppListEntry", "none")),

                        new XElement(F + "Extensions",
                            ComServer(),
                            ContextMenus())))));

        // A plain StringWriter reports UTF-16 as its encoding, and XDocument.Save writes THAT into the
        // declaration - producing <?xml version="1.0" encoding="utf-16"?> on a file that is then
        // written as UTF-8 bytes. The declaration would then disagree with the file, which is the kind
        // of mismatch makeappx rejects with an error that names neither.
        using var writer = new Utf8StringWriter();
        document.Save(writer);
        return writer.ToString() + Environment.NewLine;
    }

    /// <summary>
    /// The out-of-process COM server. This element is what keeps the adapter and its CLR out of
    /// <c>explorer.exe</c>, and E1 and E2 both exist to prove that it does.
    /// </summary>
    private static XElement ComServer() =>
        new(Com + "Extension",
            new XAttribute("Category", "windows.comServer"),
            new XElement(Com + "ComServer",
                new XElement(Com + "SurrogateServer",
                    new XAttribute("DisplayName", "NOBA Archive Shell"),
                    // No CustomSurrogateExecutable and no SystemSurrogate: that is the empty
                    // DllSurrogate shape, which means the system surrogate, dllhost.exe. E2 measured
                    // a live dllhost.exe holding the stub with the CLR in it and the standard user as
                    // its owner (DELTA section 18).
                    new XElement(Com + "Class",
                        new XAttribute("Id", Unbraced(ShellRegistration.ModernHandlerClsid)),
                        new XAttribute("Path", AdapterLibrary),
                        new XAttribute("ThreadingModel", "STA")))));

    /// <summary>
    /// The verb. One entry, whose children are decided at draw time.
    /// </summary>
    /// <remarks>
    /// The prefixes are mixed and that is correct: <c>Extension</c> and
    /// <c>FileExplorerContextMenus</c> are <c>desktop4</c>, <c>ItemType</c> and <c>Verb</c> are
    /// <c>desktop5</c>. All-<c>desktop5</c> is what <c>makeappx</c> rejected with <c>C00CE014</c>.
    /// </remarks>
    private static XElement ContextMenus() =>
        new(Desktop4 + "Extension",
            new XAttribute("Category", "windows.fileExplorerContextMenus"),
            new XElement(Desktop4 + "FileExplorerContextMenus",
                ItemTypes.Select(type =>
                    new XElement(Desktop5 + "ItemType",
                        new XAttribute("Type", type),
                        new XElement(Desktop5 + "Verb",
                            new XAttribute("Id", ShellRegistration.ModernRootVerbId),
                            new XAttribute("Clsid", Unbraced(ShellRegistration.ModernHandlerClsid)))))));

    /// <summary>
    /// The manifest wants a bare GUID; the registry and the scripts want it braced. One constant, two
    /// spellings, and the conversion in one place rather than a second constant to keep in step.
    /// </summary>
    internal static string Unbraced(string clsid) => clsid.Trim('{', '}');

    /// <summary>A <see cref="StringWriter"/> that admits to being UTF-8, so the declaration is true.</summary>
    private sealed class Utf8StringWriter() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private const string Preamble = @"
  GENERATED by `noba-ar appx-manifest`. Do not edit.

  SHELL-065: Identity/Name, the CLSID and the verb id come from ShellRegistration. Editing this file
  puts the product's identity in two places, and the failure mode is a handler that registers
  cleanly and never activates - a menu that silently does not appear, with nothing in any log.

  This is the SPARSE package for an unpackaged application (ADR-0006). It carries no payload
  binaries: uap10:AllowExternalContent lets them stay where the installer put them, and
  Add-AppxPackage is given -ExternalLocation pointing at that directory.
";
}
