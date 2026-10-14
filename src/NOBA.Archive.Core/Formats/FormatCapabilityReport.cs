namespace NOBA.Archive.Core.Formats;

/// <summary>
/// What the product may offer for one format, in the vocabulary a screen actually asks in.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ArchiveCapabilities"/> is a backend fact: a bit per thing a provider implements. This is
/// the same information asked the way a user interface needs it — "may I offer Create for this
/// format?" — computed in one place so that no screen ever answers it by knowing what "7z" means.
/// A format check that lives in a view is a format check that will disagree with the engine on the
/// day a provider changes, and the user finds out by getting an archive that is not what they asked
/// for.
/// </para>
/// <para>
/// <b>Encryption is modelled once, not twice.</b> "Encrypt content" and "password-protect" are the
/// same capability in every provider this product has — a password is how the content is encrypted.
/// Two properties reading the same flag could only ever disagree by mistake, so there is one:
/// <see cref="CanPasswordProtect"/>. Hiding the entry list is a genuinely separate capability and has
/// its own property.
/// </para>
/// <para>
/// <b>A capability is not a feature.</b> This reports what the backend can do. Whether the product has
/// built a screen for it is a different question, answered elsewhere — <see cref="CanAdd"/> is true for
/// 7Z today while no Add workflow exists, and that is not a contradiction.
/// </para>
/// </remarks>
public sealed record FormatCapabilityReport(ArchiveFormat Format, ArchiveCapabilities Capabilities)
{
    public bool CanOpen => Capabilities.Has(ArchiveCapabilities.Open);

    /// <summary>Listing entries — what the browse table needs.</summary>
    public bool CanBrowse => Capabilities.Has(ArchiveCapabilities.ListEntries);

    public bool CanTest => Capabilities.Has(ArchiveCapabilities.Test);

    public bool CanExtract => Capabilities.Has(ArchiveCapabilities.Extract);

    public bool CanCreate => Capabilities.Has(ArchiveCapabilities.Create);

    /// <summary>
    /// Adding entries to a <em>new</em> archive at Create time. Left exactly as it was before DROP 03:
    /// it feeds <see cref="PermitsMemberCount"/>, which gates whether Create accepts more than one
    /// input, and that question is unrelated to whether an existing archive on disk can be rewritten.
    /// </summary>
    public bool CanAdd => Capabilities.Has(ArchiveCapabilities.AddEntries);

    /// <summary>Removing an entry from an existing archive. See <see cref="CanUpdate"/> for how.</summary>
    public bool CanRemove => Capabilities.Has(UpdateRequirements);

    /// <summary>Renaming an entry in an existing archive. See <see cref="CanUpdate"/> for how.</summary>
    public bool CanRename => Capabilities.Has(UpdateRequirements);

    /// <summary>
    /// What <see cref="CanUpdate"/>, <see cref="CanRemove"/>, and <see cref="CanRename"/> all actually
    /// require.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not <see cref="ArchiveCapabilities.RemoveEntries"/> or <see cref="ArchiveCapabilities.RenameEntries"/>:
    /// no provider in this build sets either raw flag, because no provider implements a native in-place
    /// update (see <c>SevenZipArchiveProvider</c> and <c>TarArchiveProvider</c>'s own comments — a
    /// promise a backend cannot keep is worse than a missing feature). DROP 03 answers "can this archive
    /// be updated" a different way: by rewriting it. Opening the old archive, listing it, extracting
    /// what should survive, and creating a fresh one from that plus whatever is new is enough to remove,
    /// rename, and add entries without any provider ever mutating bytes it already wrote — so what this
    /// needs is exactly <see cref="ArchiveCapabilities.Open"/>, <see cref="ArchiveCapabilities.ListEntries"/>,
    /// <see cref="ArchiveCapabilities.Extract"/>, <see cref="ArchiveCapabilities.Create"/>, and
    /// <see cref="ArchiveCapabilities.AddEntries"/> together.
    /// </para>
    /// <para>
    /// Checked against every provider this build has: true for ZIP, 7Z, and TAR (all declare that
    /// combination already, for Create); false for GZIP (no <c>AddEntries</c> — a gzip holds one member,
    /// so "add" has no meaning, and neither does rewriting it to remove or rename that one member); false
    /// for RAR (no <c>Create</c> or <c>AddEntries</c> — read-only per ADR-0004).
    /// </para>
    /// </remarks>
    private const ArchiveCapabilities UpdateRequirements =
        ArchiveCapabilities.Open
        | ArchiveCapabilities.ListEntries
        | ArchiveCapabilities.Extract
        | ArchiveCapabilities.Create
        | ArchiveCapabilities.AddEntries;

    /// <summary>Modifying an archive in place — adding, removing, or renaming any of its entries.</summary>
    public bool CanUpdate => Capabilities.Has(UpdateRequirements);

    /// <summary>Encrypting content with a password. The same capability the format calls encryption.</summary>
    public bool CanPasswordProtect => Capabilities.Has(ArchiveCapabilities.Encrypt);

    /// <summary>Hiding the entry list as well as the content — a separate promise from encrypting it.</summary>
    public bool CanEncryptEntryNames => Capabilities.Has(ArchiveCapabilities.EncryptFileNames);

    /// <summary>Writing an archive across volumes. Reading them is <see cref="ArchiveCapabilities.ReadSplitVolumes"/>.</summary>
    public bool CanSplit => Capabilities.Has(ArchiveCapabilities.SplitVolumes);

    public bool CanCompressSolid => Capabilities.Has(ArchiveCapabilities.SolidCompression);

    /// <summary>
    /// Whether a compression level or profile means anything for this format.
    /// </summary>
    /// <remarks>
    /// Tied to creation because that is when it is chosen. A format we can only read has no compression
    /// to configure, and offering the control anyway would be a knob attached to nothing.
    /// </remarks>
    public bool CanConfigureCompression => CanCreate;

    /// <summary>True when this build can only read the format — RAR, permanently, per ADR-0004.</summary>
    public bool IsReadOnly => CanOpen && !CanCreate && !CanAdd;

    /// <summary>
    /// Whether Test verifies the file contents, or only that the archive is structurally sound.
    /// </summary>
    /// <remarks>
    /// The one capability where a false answer changes what a clean verdict <em>means</em>. ZIP, 7Z,
    /// RAR and GZIP all checksum each entry's uncompressed bytes; TAR checksums its headers and nothing
    /// else, so a tar whose contents were altered in place passes every check the format defines. A
    /// screen that reports "intact" for both without distinguishing them is telling one of the two
    /// users something untrue.
    /// </remarks>
    public bool CanVerifyContents => Capabilities.Has(ArchiveCapabilities.ContentIntegrityCheck);

    /// <summary>
    /// Whether this build can do anything at all with the format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False is not the same as unknown. A format reaches this report only because some provider claims
    /// it, so <c>false</c> here means "known to the product, and not usable in this installation" —
    /// which is exactly what a missing native library produces: <c>UnavailableNativeBackend</c>
    /// registers 7Z and RAR with no capabilities at all rather than vanishing, precisely so the reason
    /// survives to somebody who can read it.
    /// </para>
    /// <para>
    /// A screen uses this to explain rather than to hide. Silently dropping 7Z from a machine with no
    /// <c>7z.dll</c> answers the question "why can I not open this file" with nothing.
    /// </para>
    /// </remarks>
    public bool IsAvailable => Capabilities != ArchiveCapabilities.None;

    /// <summary>
    /// Whether this format can hold what a selection will actually put in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single-member rule, asked the right question. GZIP is one compressed stream with a name on
    /// it: it reports <see cref="ArchiveCapabilities.Create"/> and not
    /// <see cref="ArchiveCapabilities.AddEntries"/>, and <c>AddEntries</c> is precisely the claim "a
    /// second thing can go in here".
    /// </para>
    /// <para>
    /// <b>The parameter is members, not selections</b>, and that distinction is D7. Asking "how many
    /// things did you click?" gets the wrong answer for a folder — one click, many members — so the
    /// menu offered GZIP for a folder and the engine then refused it. Counting is the caller's job
    /// and it is a different question per caller; deciding is this method's job and it is the same
    /// question everywhere.
    /// </para>
    /// </remarks>
    public bool PermitsMemberCount(ArchiveMemberCount members) =>
        CanCreate && (members == ArchiveMemberCount.ExactlyOne || CanAdd);

    /// <summary>
    /// Whether this format can be written from exactly this many inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single-member rule, in one place. GZIP is one compressed stream with a name on it: it
    /// reports <see cref="ArchiveCapabilities.Create"/> and not <see cref="ArchiveCapabilities.AddEntries"/>,
    /// and <c>AddEntries</c> is precisely the claim "a second thing can go in here". So the condition
    /// is the capability, never the format's name — a future single-member format is covered without
    /// an edit, and GZIP gaining multi-member support would lift the restriction everywhere at once.
    /// </para>
    /// <para>
    /// Everywhere is the point. The Create screen's blocker, the shell's Compress-to menu and the CLI
    /// all ask this method rather than each carrying their own version, because three copies of one
    /// rule is three chances for the menu to offer what the engine will refuse.
    /// </para>
    /// </remarks>
    public bool PermitsInputCount(int count) =>
        count > 0
        && PermitsMemberCount(count == 1 ? ArchiveMemberCount.ExactlyOne : ArchiveMemberCount.Many);
}
