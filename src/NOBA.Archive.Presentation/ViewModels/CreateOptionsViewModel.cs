using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Presentation.Bidi;
using NOBA.Archive.Presentation.Services;

namespace NOBA.Archive.Presentation.ViewModels;

/// <summary>
/// One option on the create screen, and whether this build can actually honour it.
/// </summary>
/// <param name="UnavailableReason">
/// Shown next to a disabled control. "Unavailable" with no reason teaches the user that the product is
/// arbitrary; naming the missing capability tells them whether it is fixable.
/// </param>
public sealed record CreateOption(string Key, bool IsAvailable, string? UnavailableReason)
{
    public static CreateOption Available(string key) => new(key, IsAvailable: true, UnavailableReason: null);

    public static CreateOption Missing(string key, string reason) => new(key, IsAvailable: false, reason);
}

/// <summary>
/// The create screen's options, derived entirely from what the engine says it can do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is hard-coded from knowledge of the formats.</b> The screen asks the provider
/// registry for the capability set and renders that: if the native library is missing, 7Z disappears
/// from the format list and ZIP remains; if a future build gains split volumes, the control appears
/// without a change here.
/// </para>
/// <para>
/// The reason this matters is not tidiness. A UI that offers an option the backend ignores produces an
/// archive that is not what the user asked for and says nothing — the exact failure the capability gate
/// was built to prevent, reintroduced at the top of the stack. So an unsupported option is disabled and
/// labelled, never hidden and never silently dropped.
/// </para>
/// </remarks>
public sealed class CreateOptionsViewModel : ObservableObject
{
    private readonly IArchiveWorkspace _workspace;

    private ArchiveFormat? _format;
    private CompressionProfile _profile = CompressionProfile.Balanced;
    private CompressionLevel _level = CompressionLevel.Normal;
    private bool _solid;
    private bool _encrypt;
    private bool _encryptFileNames;

    public CreateOptionsViewModel(IArchiveWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;

        CreatableFormats = workspace.AvailableFormats
            .Where(f => workspace.CapabilitiesFor(f).Has(ArchiveCapabilities.Create))
            .ToList();

        _format = CreatableFormats.Count > 0 ? CreatableFormats[0] : null;
    }

    /// <summary>Only the formats this build can write. Reading a format does not put it here.</summary>
    public IReadOnlyList<ArchiveFormat> CreatableFormats { get; }

    public bool CanCreateAnything => CreatableFormats.Count > 0;

    public ArchiveFormat? Format
    {
        get => _format;
        set
        {
            if (!Set(ref _format, value))
            {
                return;
            }

            // Options that the new format cannot honour are turned off rather than left set and
            // ignored: a checkbox that stays ticked while doing nothing is worse than one that clears.
            if (!Solid.IsAvailable)
            {
                _solid = false;
            }

            if (!Encryption.IsAvailable)
            {
                _encrypt = false;
                _encryptFileNames = false;
            }

            if (!EncryptFileNames.IsAvailable)
            {
                _encryptFileNames = false;
            }

            Raise(nameof(HoldsManyEntries));
            Raise(nameof(Solid));
            Raise(nameof(Encryption));
            Raise(nameof(EncryptFileNames));
            Raise(nameof(IsSolid));
            Raise(nameof(IsEncrypted));
            Raise(nameof(AreFileNamesEncrypted));
            Raise(nameof(RequiresPassword));
        }
    }

    private ArchiveCapabilities Capabilities =>
        _format is null ? ArchiveCapabilities.None : _workspace.CapabilitiesFor(_format);

    /// <summary>
    /// Whether this many inputs can be written to the chosen format.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="FormatCapabilityReport.PermitsInputCount"/> rather than testing the
    /// capability here. The rule has three callers now — this screen, the shell menu and the CLI — and
    /// the one thing that must not happen is a menu offering what the screen would refuse.
    /// </remarks>
    public bool PermitsInputCount(int count) =>
        _format is { } format && _workspace.ReportFor(format).PermitsInputCount(count);

    /// <summary>Whether the chosen format can hold more than one member.</summary>
    public bool HoldsManyEntries => PermitsInputCount(2);

    public CreateOption Solid => Capability(ArchiveCapabilities.SolidCompression, "solid");

    public CreateOption Encryption => Capability(ArchiveCapabilities.Encrypt, "encrypt");

    /// <summary>
    /// Encrypting the file list needs both the capability and an encrypted archive to hide it in.
    /// </summary>
    public CreateOption EncryptFileNames
    {
        get
        {
            var capability = Capability(ArchiveCapabilities.EncryptFileNames, "encryptFileNames");
            if (!capability.IsAvailable)
            {
                return capability;
            }

            return _encrypt
                ? capability
                : CreateOption.Missing("encryptFileNames", "requiresEncryption");
        }
    }

    public CreateOption SplitVolumes => Capability(ArchiveCapabilities.SplitVolumes, "splitVolumes");

    private CreateOption Capability(ArchiveCapabilities required, string key) =>
        _format is null
            ? CreateOption.Missing(key, "noFormat")
            : Capabilities.Has(required)
                ? CreateOption.Available(key)
                : CreateOption.Missing(key, "unsupportedByFormat");

    /// <summary>
    /// The profiles a user chooses between, in order.
    /// </summary>
    /// <remarks>
    /// <see cref="CompressionProfile.Custom"/> is absent on purpose: it is not something to pick, it is
    /// what the model reports once a numeric level has been set by hand. Offering it would be a menu
    /// item that means "whatever you last did".
    /// </remarks>
    public static IReadOnlyList<CompressionProfile> Profiles { get; } =
    [
        CompressionProfile.Fast,
        CompressionProfile.Balanced,
        CompressionProfile.MaximumCompression,
        CompressionProfile.SmallestSize,
    ];

    /// <summary>
    /// Where a profile sits in <see cref="Profiles"/>, or the index of Balanced if it is not offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in the page for two reasons. <see cref="IReadOnlyList{T}"/> has no
    /// <c>IndexOf</c> — the page's first attempt to call one would not have compiled, and the only
    /// compiler that sees that project runs on the other machine.
    /// </para>
    /// <para>
    /// And the fallback is a product decision, not a formatting detail: a model reporting
    /// <see cref="CompressionProfile.Custom"/> has a level that no profile names, and the selector has
    /// to show <em>something</em>. Balanced is the honest default; -1 would blank the control and make
    /// the next selection change look like the user's.
    /// </para>
    /// </remarks>
    public static int IndexOfProfile(CompressionProfile profile)
    {
        for (var i = 0; i < Profiles.Count; i++)
        {
            if (Profiles[i] == profile)
            {
                return i;
            }
        }

        for (var i = 0; i < Profiles.Count; i++)
        {
            if (Profiles[i] == CompressionProfile.Balanced)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The profile's name in the current language.</summary>
    public static string NameOf(CompressionProfile profile, LocalizedStrings text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return profile switch
        {
            CompressionProfile.Fast => text.ProfileFast,
            CompressionProfile.Balanced => text.ProfileBalanced,
            CompressionProfile.MaximumCompression => text.ProfileMaximum,
            CompressionProfile.SmallestSize => text.ProfileSmallest,
            _ => text.ProfileBalanced,
        };
    }

    public CompressionProfile Profile
    {
        get => _profile;
        set
        {
            if (Set(ref _profile, value) && value != CompressionProfile.Custom && _format is not null)
            {
                var preset = CompressionOptions.ForProfile(_format, value);
                _level = preset.Level;
                _solid = preset.Solid && Solid.IsAvailable;
                Raise(nameof(Level));
                Raise(nameof(IsSolid));
            }
        }
    }

    public CompressionLevel Level
    {
        get => _level;
        set
        {
            if (Set(ref _level, value))
            {
                _profile = CompressionProfile.Custom;
                Raise(nameof(Profile));
            }
        }
    }

    public bool IsSolid
    {
        get => _solid;
        set
        {
            if (!Solid.IsAvailable)
            {
                return;
            }

            Set(ref _solid, value);
        }
    }

    public bool IsEncrypted
    {
        get => _encrypt;
        set
        {
            if (!Encryption.IsAvailable || !Set(ref _encrypt, value))
            {
                return;
            }

            if (!value)
            {
                _encryptFileNames = false;
                Raise(nameof(AreFileNamesEncrypted));
            }

            Raise(nameof(EncryptFileNames));
            Raise(nameof(RequiresPassword));
        }
    }

    public bool AreFileNamesEncrypted
    {
        get => _encryptFileNames;
        set
        {
            if (!EncryptFileNames.IsAvailable)
            {
                return;
            }

            Set(ref _encryptFileNames, value);
        }
    }

    /// <summary>Drives the password field. Encryption without a password is refused by the engine.</summary>
    public bool RequiresPassword => _encrypt;

    /// <summary>
    /// The options as the engine will receive them.
    /// </summary>
    /// <remarks>
    /// Built from the same state the controls are bound to, so what the user sees and what is requested
    /// cannot diverge. The capability gate will check this again — this is the screen being honest, not
    /// the screen being trusted.
    /// </remarks>
    public CompressionOptions Build()
    {
        if (_format is null)
        {
            throw new InvalidOperationException("No format is selected, and this build can create none.");
        }

        return new CompressionOptions
        {
            Format = _format,
            Profile = _profile,
            Level = _level,
            Solid = _solid && Solid.IsAvailable,
            Encryption = _encrypt && Encryption.IsAvailable ? EncryptionAlgorithm.Aes256 : EncryptionAlgorithm.None,
            EncryptFileNames = _encryptFileNames && EncryptFileNames.IsAvailable,
        };
    }
}
