using System.Security.Cryptography;
using System.Text;
using NOBA.Archive.Backends.SevenZip;
using NOBA.Archive.Core.Errors;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Core.Primitives;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// Writing 7Z: <c>7ZC-001…015</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "verified by 7-Zip" means here.</b> Every archive these tests produce is read back by
/// <c>7z.dll</c> — 7-Zip's own handler, the same binary the product ships and the same one that reads
/// archives written by 7-Zip itself. A test that shelled out to <c>7z.exe</c> would prove nothing more
/// and would skip on any machine without it, and a skipped test is an untested control.
/// </para>
/// <para>
/// The option tests (<c>7ZC-007</c>, <c>7ZC-008</c>) exist because a compression setting that never
/// reaches the backend fails silently: the archive is still valid, just not the archive that was asked
/// for. Both were failing before the property-array stride was corrected, and neither would have been
/// noticed by a round-trip test alone.
/// </para>
/// </remarks>
public sealed class SevenZipCreationTests
{
    private static bool RequireNativeBackend()
    {
        if (SevenZipLibraryLocator.Locate() is not null)
        {
            return true;
        }

        Assert.Skip(
            "The 7-Zip native library is not installed. Searched:" +
            Environment.NewLine + SevenZipLibraryLocator.DescribeSearch());
        return false;
    }

    private const string LongName =
        "a-very-long-entry-name-that-goes-well-past-the-old-eight-dot-three-world-and-keeps-going-" +
        "because-real-people-name-files-like-this-especially-when-they-are-scanning-documents-2026";

    /// <summary>Builds a source tree exercising the names and sizes that break writers.</summary>
    private static (string Root, Dictionary<string, byte[]> Expected) BuildSourceTree(TempDirectory sandbox)
    {
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(Path.Combine(root, "docs"));

        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["source/a.txt"] = Encoding.UTF8.GetBytes("hello from noba"),
            ["source/تقرير عربي.txt"] = Encoding.UTF8.GetBytes("محتوى عربي طويل بما يكفي ليضغط"),
            ["source/docs/b.txt"] = Encoding.UTF8.GetBytes("nested content"),
            ["source/empty.txt"] = [],
            [$"source/{LongName}.txt"] = Encoding.UTF8.GetBytes("long name"),

            // Incompressible on purpose: a writer that mishandles a stream it cannot shrink shows up
            // here and nowhere else, and 8 MB crosses any plausible internal buffer.
            ["source/large.bin"] = RandomNumberGenerator.GetBytes(8 * 1024 * 1024),
        };

        foreach (var (path, bytes) in expected)
        {
            var full = Path.Combine(sandbox.Path, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        return (root, expected);
    }

    private static Dictionary<string, byte[]> ReadTree(string root)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            found[relative] = File.ReadAllBytes(file);
        }

        return found;
    }

    /// <summary>7ZC-001: what comes out is a 7z archive, and 7-Zip's handler agrees.</summary>
    [Fact]
    public async Task A_created_archive_is_a_valid_7z()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-001");
        var (root, expected) = BuildSourceTree(sandbox);
        var archive = sandbox.Combine("out.7z");

        await using var engine = new EngineFixture();
        var outcome = await engine.CreateAsync(
            archive,
            [root],
            CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));

        Assert.Equal(expected.Count, outcome.EntriesAdded);

        var signature = new byte[6];
        await using (var stream = File.OpenRead(archive))
        {
            Assert.Equal(6, await stream.ReadAsync(signature));
        }

        Assert.Equal<byte[]>([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], signature);

        var listing = await engine.ListAsync(archive);
        Assert.Equal(ArchiveFormat.SevenZip, listing.Format);
        Assert.Equal(
            expected.Keys.OrderBy(static k => k, StringComparer.Ordinal),
            listing.Entries.Where(static e => !e.IsDirectory).Select(static e => e.DisplayPath).OrderBy(static k => k, StringComparer.Ordinal));
    }

    /// <summary>7ZC-002: create → open → extract → compare, byte for byte.</summary>
    [Fact]
    public async Task The_round_trip_reproduces_every_byte()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-002");
        var (root, expected) = BuildSourceTree(sandbox);
        var archive = sandbox.Combine("out.7z");
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(
            archive,
            [root],
            CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Balanced));

        var outcome = await engine.ExtractAsync(archive, destination);
        Assert.False(outcome.HadBlockedEntries);

        var actual = ReadTree(destination);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (path, bytes) in expected)
        {
            Assert.True(actual.TryGetValue(path, out var written), $"'{path}' was not extracted.");
            Assert.True(bytes.AsSpan().SequenceEqual(written), $"'{path}' differs after the round trip.");
        }
    }

    /// <summary>7ZC-003: the archive this build creates is readable by its own test verb.</summary>
    [Fact]
    public async Task A_created_archive_passes_its_own_integrity_test()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-003");
        var (root, _) = BuildSourceTree(sandbox);
        var archive = sandbox.Combine("out.7z");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [root], CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));

        var result = await engine.TestAsync(archive);
        Assert.Equal(ArchiveTestStatus.Valid, result.Status);
        Assert.Empty(result.Findings);
    }

    /// <summary>7ZC-004: modification times survive, in the timezone the machine is actually in.</summary>
    /// <remarks>
    /// The property id for the modification time sits between the attribute and the encryption flags in
    /// 7-Zip's enum, and an off-by-eight there does not fail: it produces archives with no timestamps at
    /// all, which every tool displays as blank rather than as an error. This test is the reason that was
    /// caught, and R13's timezone lesson is why the assertion is on the instant rather than on a string.
    /// </remarks>
    [Fact]
    public async Task Modification_times_survive_creation()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-004");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "stamped.txt");
        await File.WriteAllTextAsync(file, "content");

        var stamp = new DateTime(2019, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        var archive = sandbox.Combine("out.7z");
        var destination = sandbox.Combine("dest");

        await using var engine = new EngineFixture();
        await engine.CreateAsync(archive, [root], CompressionOptions.ForProfile(ArchiveFormat.SevenZip, CompressionProfile.Fast));
        await engine.ExtractAsync(archive, destination);

        var extracted = File.GetLastWriteTimeUtc(Path.Combine(destination, "source", "stamped.txt"));
        Assert.True(
            Math.Abs((extracted - stamp).TotalSeconds) < 2,
            $"expected {stamp:O}, got {extracted:O}");
    }

    /// <summary>7ZC-005: the compression level reaches the backend rather than being politely ignored.</summary>
    [Fact]
    public async Task The_compression_level_changes_the_result()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-005");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);

        // Highly compressible: at level 0 this is stored, at level 9 it nearly vanishes. An option that
        // never arrives produces two identical files, which is the failure being tested for.
        var text = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog\n", 4000));
        await File.WriteAllTextAsync(Path.Combine(root, "repetitive.txt"), text);

        await using var engine = new EngineFixture();

        var stored = sandbox.Combine("stored.7z");
        var packed = sandbox.Combine("packed.7z");

        await engine.CreateAsync(stored, [root], new CompressionOptions
        {
            Format = ArchiveFormat.SevenZip,
            Profile = CompressionProfile.Custom,
            Level = CompressionLevel.Store,
        });

        await engine.CreateAsync(packed, [root], new CompressionOptions
        {
            Format = ArchiveFormat.SevenZip,
            Profile = CompressionProfile.Custom,
            Level = CompressionLevel.Maximum,
        });

        var storedSize = new FileInfo(stored).Length;
        var packedSize = new FileInfo(packed).Length;

        Assert.True(
            packedSize * 10 < storedSize,
            $"level 0 produced {storedSize} bytes and level 9 produced {packedSize}: the level is not reaching the backend");
    }

    /// <summary>
    /// 7ZC-006: solid and non-solid produce genuinely different archives.
    /// </summary>
    /// <remarks>
    /// The regression test for a defect that made every later compression property a no-op:
    /// <c>ISetProperties</c> indexes its value array at the platform's <c>PROPVARIANT</c> stride, and
    /// handing it a managed array of the Windows-sized struct meant everything after the first property
    /// was read from the wrong offset. The level worked (index 0) and nothing else did — an archive
    /// requested non-solid came back solid, with no error anywhere.
    /// </remarks>
    [Fact]
    public async Task Solid_compression_is_honoured_in_both_directions()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-006");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);

        // Many similar files: a solid block compresses them together and wins by a wide margin, while
        // non-solid compresses each alone. The size gap is the observable difference.
        for (var i = 0; i < 40; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, $"f{i}.txt"),
                string.Concat(Enumerable.Repeat($"line {i} of a fairly repetitive file\n", 200)));
        }

        await using var engine = new EngineFixture();

        var solid = sandbox.Combine("solid.7z");
        var nonSolid = sandbox.Combine("nonsolid.7z");

        var baseline = new CompressionOptions
        {
            Format = ArchiveFormat.SevenZip,
            Profile = CompressionProfile.Custom,
            Level = CompressionLevel.Normal,
        };

        await engine.CreateAsync(solid, [root], baseline with { Solid = true });
        await engine.CreateAsync(nonSolid, [root], baseline with { Solid = false });

        var solidSize = new FileInfo(solid).Length;
        var nonSolidSize = new FileInfo(nonSolid).Length;

        Assert.True(
            solidSize * 2 < nonSolidSize,
            $"solid produced {solidSize} bytes and non-solid {nonSolidSize}: the solid flag is not reaching the backend");

        // Both still have to be readable, which is the other half of "the option was applied correctly".
        Assert.Equal(ArchiveTestStatus.Valid, (await engine.TestAsync(solid)).Status);
        Assert.Equal(ArchiveTestStatus.Valid, (await engine.TestAsync(nonSolid)).Status);
    }

    /// <summary>7ZC-007: an encrypted archive needs its password, and works with it.</summary>
    [Fact]
    public async Task An_encrypted_archive_round_trips_with_its_password()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-007");
        var (root, expected) = BuildSourceTree(sandbox);
        var archive = sandbox.Combine("secret.7z");

        await using (var engine = new EngineFixture("correct horse battery staple"))
        {
            await engine.CreateAsync(archive, [root], new CompressionOptions
            {
                Format = ArchiveFormat.SevenZip,
                Profile = CompressionProfile.Custom,
                Level = CompressionLevel.Fastest,
                Encryption = EncryptionAlgorithm.Aes256,
            });
        }

        // Without the password: the file list is readable (the header is not encrypted) and the content
        // is not. Reporting that as "corrupt" would be the wrong diagnosis, so the assertion is only
        // that it does not succeed.
        await using (var wrong = new EngineFixture())
        {
            var listing = await wrong.ListAsync(archive);
            Assert.NotEmpty(listing.Entries);
            Assert.Contains(listing.Entries, static e => e.IsEncrypted);

            await Assert.ThrowsAsync<ArchiveOperationException>(
                async () => await wrong.ExtractAsync(archive, sandbox.Combine("denied")));
        }

        await using (var engine = new EngineFixture("correct horse battery staple"))
        {
            Assert.Equal(ArchiveTestStatus.Valid, (await engine.TestAsync(archive)).Status);

            var destination = sandbox.Combine("dest");
            await engine.ExtractAsync(archive, destination);

            var actual = ReadTree(destination);
            foreach (var (path, bytes) in expected)
            {
                Assert.True(actual.TryGetValue(path, out var written), $"'{path}' was not extracted.");
                Assert.True(bytes.AsSpan().SequenceEqual(written), $"'{path}' differs after the round trip.");
            }
        }
    }

    /// <summary>7ZC-008: encrypted file names hide the listing itself.</summary>
    [Fact]
    public async Task Encrypted_file_names_hide_the_listing()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-008");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "confidential.txt"), "nothing to see");

        var archive = sandbox.Combine("sealed.7z");

        await using (var engine = new EngineFixture("hunter2"))
        {
            await engine.CreateAsync(archive, [root], new CompressionOptions
            {
                Format = ArchiveFormat.SevenZip,
                Profile = CompressionProfile.Custom,
                Level = CompressionLevel.Fastest,
                Encryption = EncryptionAlgorithm.Aes256,
                EncryptFileNames = true,
            });
        }

        await using (var wrong = new EngineFixture())
        {
            var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
                async () => await wrong.ListAsync(archive));
            Assert.Equal(ArchiveErrorCode.PasswordRequired, failure.Error.Code);
        }

        await using (var engine = new EngineFixture("hunter2"))
        {
            var listing = await engine.ListAsync(archive);
            Assert.Contains(listing.Entries, static e => e.DisplayPath.EndsWith("confidential.txt", StringComparison.Ordinal));
        }
    }

    /// <summary>7ZC-009: encryption without a password produces no archive at all.</summary>
    /// <remarks>
    /// The failure mode worth preventing is an archive that was requested encrypted and written in the
    /// clear. Refusing before anything is staged is the only way to be sure, so this asserts on the
    /// absence of the file as well as on the error.
    /// </remarks>
    [Fact]
    public async Task Encryption_without_a_password_is_refused_and_leaves_nothing_behind()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-009");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "content");

        var archive = sandbox.Combine("nope.7z");

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [root], new CompressionOptions
            {
                Format = ArchiveFormat.SevenZip,
                Encryption = EncryptionAlgorithm.Aes256,
            }));

        Assert.Equal(ArchiveErrorCode.PasswordRequired, failure.Error.Code);
        Assert.False(File.Exists(archive), "a refused creation left an archive behind");
    }

    /// <summary>7ZC-010: RAR creation is still impossible, and says why (ADR-0004).</summary>
    [Fact]
    public async Task Rar_creation_is_still_refused()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-010");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "content");

        await using var engine = new EngineFixture();
        var failure = await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(
                sandbox.Combine("impossible.rar"),
                [root],
                CompressionOptions.ForProfile(ArchiveFormat.Rar, CompressionProfile.Balanced)));

        Assert.Equal(ArchiveErrorCode.UnsupportedOperation, failure.Error.Code);
    }

    /// <summary>
    /// 7ZC-011: a capability that is not advertised is refused, not silently dropped.
    /// </summary>
    /// <remarks>
    /// Split volumes are the case in hand: writing them needs <c>IArchiveUpdateCallback2</c>, which this
    /// backend does not implement, so <c>SplitVolumes</c> is absent from the capability set. The
    /// dangerous outcome would be a single archive produced for a request that asked to be split — the
    /// user gets a file that looks right and is not what they asked for.
    /// </remarks>
    [Fact]
    public async Task An_unadvertised_option_is_refused_rather_than_ignored()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var sandbox = new TempDirectory("7ZC-011");
        var root = sandbox.Combine("source");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "content");

        var archive = sandbox.Combine("split.7z");

        await using var engine = new EngineFixture();
        await Assert.ThrowsAsync<ArchiveOperationException>(
            async () => await engine.CreateAsync(archive, [root], new CompressionOptions
            {
                Format = ArchiveFormat.SevenZip,
                VolumeSize = ByteSize.FromBytes(1024 * 1024),
            }));

        Assert.False(File.Exists(archive), "a refused creation left an archive behind");
    }

    /// <summary>7ZC-012: the capability set says exactly what the builder can do.</summary>
    /// <remarks>
    /// Both directions matter. Claiming a capability that does not work is a promise the product
    /// breaks; withholding one that does work sends the caller to a backend that is worse at the job.
    /// The absent ones here are deliberate: updating an existing 7z is not implemented, and writing
    /// volumes is not either.
    /// </remarks>
    [Fact]
    public void The_capability_set_matches_what_is_implemented()
    {
        if (!RequireNativeBackend())
        {
            return;
        }

        using var backend = new SevenZipBackend(new NullFormatDetector(), configuredLibraryPath: null);
        var capabilities = backend.GetCapabilities(ArchiveFormat.SevenZip);

        Assert.True(capabilities.Has(ArchiveCapabilities.Create));
        Assert.True(capabilities.Has(ArchiveCapabilities.AddEntries));
        Assert.True(capabilities.Has(ArchiveCapabilities.Encrypt));
        Assert.True(capabilities.Has(ArchiveCapabilities.EncryptFileNames));
        Assert.True(capabilities.Has(ArchiveCapabilities.SolidCompression));

        Assert.False(capabilities.Has(ArchiveCapabilities.SplitVolumes));
        Assert.False(capabilities.Has(ArchiveCapabilities.RemoveEntries));
        Assert.False(capabilities.Has(ArchiveCapabilities.RenameEntries));
    }

    private sealed class NullFormatDetector : Core.Abstractions.IFormatDetector
    {
        public ValueTask<FormatDetection> DetectAsync(Stream probe, string? fileName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FormatDetection.None);

        public FormatDetection DetectFromHeader(ReadOnlySpan<byte> header, string? fileName) => FormatDetection.None;
    }
}
