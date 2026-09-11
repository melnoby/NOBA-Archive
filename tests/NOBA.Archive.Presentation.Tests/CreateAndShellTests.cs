using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Design;
using NOBA.Archive.Presentation.ViewModels;
using Xunit;

namespace NOBA.Archive.Presentation.Tests;

/// <summary>
/// Capability-driven create options and the shell: <c>UI-020…031</c>.
/// </summary>
/// <remarks>
/// The rule under test throughout is that the screen asks the engine what is possible and shows that.
/// A UI that offers an option the backend ignores produces an archive that is not the one requested,
/// says nothing about it, and defeats the capability gate from above.
/// </remarks>
public sealed class CreateAndShellTests
{
    /// <summary>UI-020: only formats this build can write appear in the format list.</summary>
    [Fact]
    public void Only_creatable_formats_are_offered()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace());

        Assert.True(vm.CreatableFormats.Contains(ArchiveFormat.SevenZip), "7Z is missing");
        Assert.True(vm.CreatableFormats.Contains(ArchiveFormat.Zip), "ZIP is missing");

        // RAR is readable and will never be creatable (ADR-0004). It must not be in this list.
        Assert.True(!vm.CreatableFormats.Contains(ArchiveFormat.Rar), "RAR must never be creatable");
    }

    /// <summary>
    /// UI-021: a missing native library removes 7Z from creation rather than failing later.
    /// </summary>
    /// <remarks>
    /// This is the honest degradation the backend already performs, carried to the surface. Offering
    /// 7Z on a machine with no <c>7z.dll</c> and failing at the end of a long compression is the
    /// behaviour being prevented.
    /// </remarks>
    [Fact]
    public void Without_the_native_backend_only_zip_can_be_created()
    {
        var workspace = new FakeWorkspace(new Dictionary<ArchiveFormat, ArchiveCapabilities>
        {
            [ArchiveFormat.Zip] = ArchiveCapabilities.ReadOnlySet | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries,
        });

        var vm = new CreateOptionsViewModel(workspace);

        Assert.Single(vm.CreatableFormats);
        Assert.Equal(ArchiveFormat.Zip, vm.CreatableFormats[0]);
        Assert.True(vm.CanCreateAnything);
        Assert.False(vm.Solid.IsAvailable);
        Assert.False(vm.Encryption.IsAvailable);
    }

    /// <summary>UI-022: an unsupported option is disabled with a reason, not hidden.</summary>
    [Fact]
    public void An_unsupported_option_is_disabled_and_explained()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace()) { Format = ArchiveFormat.Zip };

        Assert.False(vm.Solid.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(vm.Solid.UnavailableReason));

        Assert.False(vm.SplitVolumes.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(vm.SplitVolumes.UnavailableReason));
    }

    /// <summary>UI-023: setting an unavailable option does nothing rather than lying.</summary>
    /// <remarks>
    /// The dangerous version is a checkbox that stays ticked while the backend ignores it: the user
    /// believes they asked for a solid archive and no part of the system disagrees with them.
    /// </remarks>
    [Fact]
    public void An_unavailable_option_cannot_be_switched_on()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace()) { Format = ArchiveFormat.Zip };

        vm.IsSolid = true;
        vm.IsEncrypted = true;

        Assert.False(vm.IsSolid);
        Assert.False(vm.IsEncrypted);

        var options = vm.Build();
        Assert.False(options.Solid);
        Assert.Equal(EncryptionAlgorithm.None, options.Encryption);
    }

    /// <summary>UI-024: switching format clears options the new format cannot honour.</summary>
    [Fact]
    public void Switching_format_clears_options_it_cannot_honour()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace()) { Format = ArchiveFormat.SevenZip };

        vm.IsSolid = true;
        vm.IsEncrypted = true;
        vm.AreFileNamesEncrypted = true;
        Assert.True(vm.IsSolid);
        Assert.True(vm.AreFileNamesEncrypted);

        vm.Format = ArchiveFormat.Zip;

        Assert.False(vm.IsSolid);
        Assert.False(vm.IsEncrypted);
        Assert.False(vm.AreFileNamesEncrypted);
    }

    /// <summary>UI-025: encrypting the file list needs encryption switched on first.</summary>
    [Fact]
    public void Encrypting_file_names_requires_encryption()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace()) { Format = ArchiveFormat.SevenZip };

        Assert.False(vm.EncryptFileNames.IsAvailable);
        vm.AreFileNamesEncrypted = true;
        Assert.False(vm.AreFileNamesEncrypted);

        vm.IsEncrypted = true;
        Assert.True(vm.EncryptFileNames.IsAvailable);

        vm.AreFileNamesEncrypted = true;
        Assert.True(vm.AreFileNamesEncrypted);

        // And turning encryption back off takes the dependent option with it.
        vm.IsEncrypted = false;
        Assert.False(vm.AreFileNamesEncrypted);
    }

    /// <summary>UI-026: a profile sets the level; setting a level makes the profile custom.</summary>
    [Fact]
    public void Profile_and_level_stay_consistent()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace()) { Format = ArchiveFormat.SevenZip };

        vm.Profile = CompressionProfile.SmallestSize;
        Assert.Equal(CompressionLevel.Maximum, vm.Level);
        Assert.True(vm.IsSolid);

        vm.Level = new CompressionLevel(3);
        Assert.Equal(CompressionProfile.Custom, vm.Profile);
    }

    /// <summary>UI-027: encryption drives the password field.</summary>
    [Fact]
    public void Encryption_requires_a_password()
    {
        var vm = new CreateOptionsViewModel(new FakeWorkspace()) { Format = ArchiveFormat.SevenZip };

        Assert.False(vm.RequiresPassword);
        vm.IsEncrypted = true;
        Assert.True(vm.RequiresPassword);
        Assert.True(vm.Build().RequiresPassword);
    }

    /// <summary>UI-028: the shell derives direction, font and row height from the language.</summary>
    /// <remarks>
    /// There is no state in which the UI is Arabic and laid out for Latin, because none of these are
    /// set independently — they are all functions of one property.
    /// </remarks>
    [Fact]
    public void The_shell_derives_everything_from_the_language()
    {
        var shell = new ShellViewModel(new FakeWorkspace(), UiLanguage.English);

        Assert.Equal(FlowDirection.LeftToRight, shell.Flow);
        Assert.False(shell.IsRightToLeft);
        Assert.Equal(DesignTokens.FileListRowHeight(UiScript.Latin), shell.FileListRowHeight);
        Assert.True(shell.UiFontFamily.Contains("Segoe UI Variable", StringComparison.Ordinal), shell.UiFontFamily);

        shell.ToggleLanguage();

        Assert.Equal(FlowDirection.RightToLeft, shell.Flow);
        Assert.True(shell.IsRightToLeft);
        Assert.Equal(DesignTokens.FileListRowHeight(UiScript.Arabic), shell.FileListRowHeight);
        Assert.True(shell.UiFontFamily.Contains("Noto Sans Arabic", StringComparison.Ordinal), shell.UiFontFamily);
    }

    /// <summary>UI-029: changing the shell language changes how the browse screen formats numbers.</summary>
    [Fact]
    public void The_language_reaches_the_browse_screen()
    {
        var shell = new ShellViewModel(new FakeWorkspace(), UiLanguage.English);
        Assert.Equal("en", shell.Browse.Culture.TwoLetterISOLanguageName);

        shell.Language = UiLanguage.Arabic;
        Assert.Equal("ar", shell.Browse.Culture.TwoLetterISOLanguageName);
    }

    /// <summary>UI-030: the theme switch swaps the whole palette.</summary>
    [Fact]
    public void Theme_switching_swaps_the_palette()
    {
        var shell = new ShellViewModel(new FakeWorkspace());
        var dark = shell.Colors["surface.base"];

        shell.ToggleTheme();

        Assert.Equal(ThemeMode.Light, shell.Theme);
        Assert.NotEqual(dark, shell.Colors["surface.base"]);
    }

    /// <summary>UI-031: collapsing the sidebar uses the token width, not a magic number.</summary>
    [Fact]
    public void The_sidebar_width_comes_from_the_tokens()
    {
        var shell = new ShellViewModel(new FakeWorkspace());

        Assert.Equal(DesignTokens.SidebarWidth, shell.SidebarWidth);
        shell.IsSidebarExpanded = false;
        Assert.Equal(DesignTokens.SidebarCollapsedWidth, shell.SidebarWidth);
    }
}
