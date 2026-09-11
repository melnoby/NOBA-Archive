namespace NOBA.Archive.Core.Formats;

/// <summary>
/// What a provider can actually do with a given format. The UI binds command availability to these
/// flags, so an operation a backend cannot safely perform is never offered.
/// </summary>
[Flags]
public enum ArchiveCapabilities
{
    None = 0,
    Open = 1 << 0,
    ListEntries = 1 << 1,
    Extract = 1 << 2,
    Create = 1 << 3,
    AddEntries = 1 << 4,
    RemoveEntries = 1 << 5,
    RenameEntries = 1 << 6,
    Test = 1 << 7,
    Encrypt = 1 << 8,
    EncryptFileNames = 1 << 9,
    SolidCompression = 1 << 10,
    SplitVolumes = 1 << 11,
    ReadSplitVolumes = 1 << 12,
    Comments = 1 << 13,
    RecoveryRecord = 1 << 14,
    RandomAccessRead = 1 << 15,
    StreamingRead = 1 << 16,
    PreservesUnixMode = 1 << 17,

    /// <summary>
    /// Testing this format verifies the file <em>contents</em>, not merely the structure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ZIP, 7Z, RAR and GZIP all store a checksum over each entry's uncompressed bytes, so a test that
    /// reads them to the end detects a flipped bit. TAR stores a checksum over its headers and nothing
    /// at all over file data: a tar whose contents were altered in place passes every check the format
    /// makes.
    /// </para>
    /// <para>
    /// The distinction exists as a capability because it is the difference between two answers to
    /// "is this archive intact" that a person would otherwise read as identical. It lets the UI say
    /// what was actually verified without any screen knowing which format it is looking at.
    /// </para>
    /// </remarks>
    ContentIntegrityCheck = 1 << 18,

    ReadOnlySet = Open | ListEntries | Extract | Test,
}

public static class ArchiveCapabilitiesExtensions
{
    public static bool Has(this ArchiveCapabilities value, ArchiveCapabilities required) =>
        required != ArchiveCapabilities.None && (value & required) == required;

    public static bool HasAny(this ArchiveCapabilities value, ArchiveCapabilities any) => (value & any) != 0;
}
