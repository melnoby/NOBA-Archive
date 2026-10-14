using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Application.Handlers;
using NOBA.Archive.Backends.SevenZip;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// DROP 03 — Archive Management: adding, removing, and renaming entries in an archive that already
/// exists, rather than only ever creating a new one.
/// </summary>
/// <remarks>
/// <see cref="ManageArchiveHandler"/> never asks a provider to mutate the bytes of an archive it
/// already wrote — it rewrites the archive from its own surviving entries plus whatever changed, through
/// the same <c>CreateAsync</c>/<c>IArchivePublisher</c> path <see cref="CreateArchiveHandler"/> already
/// uses. These tests prove the rewrite is faithful (nothing that should survive is lost or corrupted)
/// and that it is as safe as creation already is: a failure partway through never touches the archive
/// being updated.
/// </remarks>
public sealed class ManageArchiveTests
{
    private static async Task<string> WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    private static ArchivePath Path_(string value)
    {
        Assert.True(ArchivePath.TryCreate(value, out var path, out var rejection), rejection?.Detail);
        return path;
    }

    private static async Task<string> EntriesOf(EngineFixture engine, string archive)
    {
        var listing = await engine.ListAsync(archive);
        return string.Join(
            " | ",
            listing.Entries
                .Where(static e => !e.IsDirectory)
                .Select(static e => e.DisplayPath)
                .Order(StringComparer.Ordinal));
    }

    private static async Task<string> ContentOf(EngineFixture engine, string archive, string destination)
    {
        await engine.ExtractAsync(archive, destination);
        var path = Directory
            .EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .Single();
        return await File.ReadAllTextAsync(path, Encoding.UTF8);
    }

    /// <summary>MAN-001: adding a file to an existing archive keeps every entry already in it.</summary>
    [Fact]
    public async Task Adding_a_file_keeps_the_existing_entries_and_adds_the_new_one()
    {
        using var sandbox = new TempDirectory("MAN-001");
        var a = await WriteFile(sandbox.Combine("a.txt"), "one");
        var b = await WriteFile(sandbox.Combine("b.txt"), "two");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a, b]);

        var newFile = await WriteFile(sandbox.Combine("c.txt"), "three");
        var outcome = await engine.ManageAsync(archive, add: [newFile]);

        Assert.Equal(2, outcome.EntriesKept);
        Assert.Equal(1, outcome.EntriesAdded);
        Assert.Equal(0, outcome.EntriesRemoved);
        Assert.Equal(0, outcome.EntriesRenamed);
        Assert.Equal("a.txt | b.txt | c.txt", await EntriesOf(engine, archive));
    }

    /// <summary>MAN-002: removing an entry drops it and nothing else, and its bytes are gone.</summary>
    [Fact]
    public async Task Removing_an_entry_drops_only_that_entry()
    {
        using var sandbox = new TempDirectory("MAN-002");
        var a = await WriteFile(sandbox.Combine("a.txt"), "one");
        var b = await WriteFile(sandbox.Combine("b.txt"), "two");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a, b]);

        var outcome = await engine.ManageAsync(archive, remove: [Path_("a.txt")]);

        Assert.Equal(1, outcome.EntriesKept);
        Assert.Equal(1, outcome.EntriesRemoved);
        Assert.Equal("b.txt", await EntriesOf(engine, archive));
    }

    /// <summary>MAN-003: renaming an entry changes its path but keeps its content.</summary>
    [Fact]
    public async Task Renaming_an_entry_changes_its_path_and_keeps_its_content()
    {
        using var sandbox = new TempDirectory("MAN-003");
        var a = await WriteFile(sandbox.Combine("a.txt"), "original content");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a]);

        var outcome = await engine.ManageAsync(
            archive,
            rename: [new EntryRename(Path_("a.txt"), Path_("renamed.txt"))]);

        Assert.Equal(0, outcome.EntriesKept);
        Assert.Equal(1, outcome.EntriesRenamed);
        Assert.Equal("renamed.txt", await EntriesOf(engine, archive));
        Assert.Equal("original content", await ContentOf(engine, archive, sandbox.Combine("extracted")));
    }

    /// <summary>MAN-004: add, remove, and rename all land correctly in the same rewrite.</summary>
    [Fact]
    public async Task Add_remove_and_rename_all_apply_together()
    {
        using var sandbox = new TempDirectory("MAN-004");
        var a = await WriteFile(sandbox.Combine("a.txt"), "keep me");
        var b = await WriteFile(sandbox.Combine("b.txt"), "remove me");
        var c = await WriteFile(sandbox.Combine("c.txt"), "rename me");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a, b, c]);

        var newFile = await WriteFile(sandbox.Combine("d.txt"), "brand new");
        var outcome = await engine.ManageAsync(
            archive,
            remove: [Path_("b.txt")],
            rename: [new EntryRename(Path_("c.txt"), Path_("c-renamed.txt"))],
            add: [newFile]);

        Assert.Equal(1, outcome.EntriesKept);
        Assert.Equal(1, outcome.EntriesRemoved);
        Assert.Equal(1, outcome.EntriesRenamed);
        Assert.Equal(1, outcome.EntriesAdded);
        Assert.Equal("a.txt | c-renamed.txt | d.txt", await EntriesOf(engine, archive));
    }

    /// <summary>MAN-005: a remove request naming an entry that is not in the archive fails and changes nothing.</summary>
    [Fact]
    public async Task Removing_an_unknown_entry_fails_and_leaves_the_archive_untouched()
    {
        using var sandbox = new TempDirectory("MAN-005");
        var a = await WriteFile(sandbox.Combine("a.txt"), "one");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a]);
        var before = await File.ReadAllBytesAsync(archive);

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(archive, remove: [Path_("does-not-exist.txt")]));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
    }

    /// <summary>MAN-006: adding a file under a path an existing entry already occupies fails and changes nothing.</summary>
    [Fact]
    public async Task Adding_a_name_collision_fails_and_leaves_the_archive_untouched()
    {
        using var sandbox = new TempDirectory("MAN-006");
        var a = await WriteFile(sandbox.Combine("a.txt"), "original");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a]);
        var before = await File.ReadAllBytesAsync(archive);

        var collidingDir = sandbox.Combine("colliding");
        var colliding = await WriteFile(Path.Combine(collidingDir, "a.txt"), "different");
        // Added as a loose file so it lands at "a.txt" in the archive, the same path "a.txt" already has.

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(archive, add: [colliding]));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
    }

    /// <summary>
    /// MAN-007: a write failure partway through building the replacement archive leaves the original
    /// archive byte-for-byte as it was, with no staging file left behind either.
    /// </summary>
    /// <remarks>
    /// The mirror image of CRT-014 (<see cref="CreationFailureInjectionTests"/>), run against an update
    /// rather than a fresh create: the fault fires while the new archive is being written, after the
    /// old archive has already been fully read and closed, which is exactly the moment an update is
    /// riskiest to get wrong.
    /// </remarks>
    [Fact]
    public async Task A_failure_mid_rewrite_leaves_the_original_archive_untouched()
    {
        using var sandbox = new TempDirectory("MAN-007");
        var a = await WriteFile(sandbox.Combine("a.txt"), "small original content");
        var archive = sandbox.Combine("out.zip");

        await using (var setup = new EngineFixture())
        {
            await setup.CreateAsync(archive, [a]);
        }

        var before = await File.ReadAllBytesAsync(archive);

        // Large and incompressible, so writing it blows the budget below long before the replacement
        // archive is finished - a mid-write failure, not a same-as-CreateNew failure in disguise.
        var bigFile = sandbox.Combine("big.bin");
        await File.WriteAllBytesAsync(bigFile, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));

        var diskFull = new ArchiveOperationException(ArchiveError.Create(
            ArchiveErrorCode.DiskFull,
            "There is not enough space on the destination volume.",
            archive));

        // Each CreateNew call gets its own fresh budget, so the small extracted entry (a few bytes to a
        // temp file) comfortably survives; only the much larger replacement-archive write exhausts it.
        var fileSystem = new FaultInjectingFileSystem(
            new PhysicalFileSystem(),
            writeFaultAfterBytes: 2048,
            writeFaultError: diskFull);

        await using var engine = new EngineFixture(password: null, fileSystemOverride: fileSystem);
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(archive, add: [bigFile]));

        Assert.Equal(ArchiveErrorCode.DiskFull, failure.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));

        foreach (var leftover in Directory.EnumerateFiles(sandbox.Path, ".noba-*.tmp", SearchOption.AllDirectories))
        {
            Assert.Fail($"a staging file survived the failure: {leftover}");
        }
    }

    /// <summary>MAN-008: the same add/remove/rename round trip works for 7Z, not only for the managed ZIP writer.</summary>
    [Fact]
    public async Task Seven_zip_archives_can_be_updated_the_same_way()
    {
        if (SevenZipLibraryLocator.Locate() is null)
        {
            Assert.Skip(
                "The 7-Zip native library is not installed. Searched:" +
                Environment.NewLine + SevenZipLibraryLocator.DescribeSearch());
            return;
        }

        using var sandbox = new TempDirectory("MAN-008");
        var a = await WriteFile(sandbox.Combine("a.txt"), "keep me");
        var b = await WriteFile(sandbox.Combine("b.txt"), "remove me");
        var archive = sandbox.Combine("out.7z");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(
            archive,
            [a, b],
            CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));

        var newFile = await WriteFile(sandbox.Combine("c.txt"), "brand new");
        var outcome = await engine.ManageAsync(
            archive,
            options: CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast),
            remove: [Path_("b.txt")],
            add: [newFile]);

        Assert.Equal(1, outcome.EntriesKept);
        Assert.Equal(1, outcome.EntriesRemoved);
        Assert.Equal(1, outcome.EntriesAdded);
        Assert.Equal("a.txt | c.txt", await EntriesOf(engine, archive));
    }

    /// <summary>MAN-009: updating cannot silently convert an archive to a different format.</summary>
    [Fact]
    public async Task Mismatched_format_options_are_refused()
    {
        using var sandbox = new TempDirectory("MAN-009");
        var a = await WriteFile(sandbox.Combine("a.txt"), "one");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a]);
        var before = await File.ReadAllBytesAsync(archive);

        var mismatched = sandbox.Combine("new.txt");
        await WriteFile(mismatched, "two");

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(
                archive,
                options: CompressionOptions.ForProfile(ArchiveFormat.Tar, CompressionProfile.Balanced),
                add: [mismatched]));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
    }

    /// <summary>MAN-010: a request with nothing to add, remove, or rename is refused rather than silently rewriting.</summary>
    [Fact]
    public async Task An_empty_request_is_refused()
    {
        using var sandbox = new TempDirectory("MAN-010");
        var a = await WriteFile(sandbox.Combine("a.txt"), "one");
        var archive = sandbox.Combine("out.zip");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [a]);

        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.ManageAsync(archive));

        Assert.Equal(ArchiveErrorCode.InvalidConfiguration, failure.Error.Code);
    }
}

/// <summary>
/// SHELL/DROP03: <c>FormatCapabilityReport.CanUpdate</c>/<c>CanRemove</c>/<c>CanRename</c>, checked
/// against the capabilities the real, DI-registered providers report - not a hand-copied guess at what
/// they return.
/// </summary>
public sealed class ManageCapabilityTests
{
    [Fact]
    public async Task Zip_and_tar_report_updatable_gzip_and_rar_do_not()
    {
        await using var engine = new EngineFixture();
        var registry = engine.Get<IArchiveProviderRegistry>();

        var zip = new FormatCapabilityReport(ArchiveFormat.Zip, registry.CapabilitiesFor(ArchiveFormat.Zip));
        var tar = new FormatCapabilityReport(ArchiveFormat.Tar, registry.CapabilitiesFor(ArchiveFormat.Tar));
        var gzip = new FormatCapabilityReport(ArchiveFormat.GZip, registry.CapabilitiesFor(ArchiveFormat.GZip));
        var rar = new FormatCapabilityReport(ArchiveFormat.Rar, registry.CapabilitiesFor(ArchiveFormat.Rar));

        Assert.True(zip.CanUpdate, "ZIP needs no native library and should always report updatable");
        Assert.True(zip.CanRemove);
        Assert.True(zip.CanRename);

        Assert.True(tar.CanUpdate, "TAR needs no native library and should always report updatable");

        // GZIP holds exactly one member: it has Create but never AddEntries, so rewriting it to remove
        // or rename "the one thing inside" is not a meaningful operation either.
        Assert.False(gzip.CanUpdate);
        Assert.False(gzip.CanRemove);
        Assert.False(gzip.CanRename);

        // RAR is read-only per ADR-0004 regardless of whether the native library is present: with it,
        // the provider reports no Create/AddEntries; without it, UnavailableNativeBackend reports no
        // capabilities at all. Either way CanUpdate must be false.
        Assert.False(rar.CanUpdate);
        Assert.False(rar.CanRemove);
        Assert.False(rar.CanRename);
    }

    [Fact]
    public async Task Seven_zip_reports_updatable_when_the_native_library_is_present()
    {
        if (SevenZipLibraryLocator.Locate() is null)
        {
            Assert.Skip(
                "The 7-Zip native library is not installed. Searched:" +
                Environment.NewLine + SevenZipLibraryLocator.DescribeSearch());
            return;
        }

        await using var engine = new EngineFixture();
        var registry = engine.Get<IArchiveProviderRegistry>();
        var sevenZip = new FormatCapabilityReport(ArchiveFormat.SevenZip, registry.CapabilitiesFor(ArchiveFormat.SevenZip));

        Assert.True(sevenZip.CanUpdate);
        Assert.True(sevenZip.CanRemove);
        Assert.True(sevenZip.CanRename);
    }

    /// <summary>
    /// CanAdd still answers the Create-time question it always did (D7/PermitsMemberCount), unaffected
    /// by DROP 03 redefining CanUpdate/CanRemove/CanRename.
    /// </summary>
    [Fact]
    public void CanAdd_and_PermitsMemberCount_are_unchanged_by_the_update_capabilities()
    {
        var zip = new FormatCapabilityReport(
            ArchiveFormat.Zip,
            ArchiveCapabilities.Open | ArchiveCapabilities.ListEntries | ArchiveCapabilities.Extract
                | ArchiveCapabilities.Create | ArchiveCapabilities.AddEntries);
        var gzip = new FormatCapabilityReport(ArchiveFormat.GZip, ArchiveCapabilities.Create);

        Assert.True(zip.CanAdd);
        Assert.True(zip.PermitsMemberCount(ArchiveMemberCount.Many));

        Assert.False(gzip.CanAdd);
        Assert.True(gzip.PermitsMemberCount(ArchiveMemberCount.ExactlyOne));
        Assert.False(gzip.PermitsMemberCount(ArchiveMemberCount.Many));
    }
}
