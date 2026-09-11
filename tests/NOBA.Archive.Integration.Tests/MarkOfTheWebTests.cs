using Microsoft.Extensions.DependencyInjection;
using NOBA.Archive.Core.Abstractions;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.TestKit.Fixtures;
using Xunit;

namespace NOBA.Archive.Integration.Tests;

/// <summary>
/// The Mark of the Web suite (THREAT_MODEL.md T7). Windows-only, and honestly skipped elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// When a file arrives from the internet Windows tags it with a <c>Zone.Identifier</c> alternate data
/// stream, and SmartScreen, Office Protected View and the script hosts all key off that tag. An
/// archiver that extracts a downloaded ZIP and drops the tag has quietly disabled those defences for
/// every file inside it — which is why losing it is a security defect rather than a missing nicety.
/// </para>
/// <para>
/// Note the asymmetry these tests pin down, because it looks contradictory until stated: NOBA Archive
/// <b>writes</b> an alternate data stream onto a path it has already validated, and <b>refuses</b> one
/// an archive entry asks for (SEC-007, the CVE-2025-8088 shape). Same NTFS feature, opposite verdicts,
/// decided by who is asking.
/// </para>
/// <para>
/// These ran nowhere until R13 closed: the shared extraction fixture disables propagation, so
/// <c>ReadZoneIdentifier</c> and <c>WriteZoneIdentifier</c> had never executed once in the whole of
/// Phase 2 despite 330 passing tests.
/// </para>
/// </remarks>
public sealed class MarkOfTheWebTests
{
    /// <summary>What Edge and Chrome actually write onto a downloaded file. ZoneId 3 is "Internet".</summary>
    private const string InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\n";

    private static bool RequireWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        // A skip is not a pass. Recorded as untested rather than quietly counted as working.
        Assert.Skip("Mark of the Web is an NTFS alternate data stream; there is no equivalent here.");
        return false;
    }

    /// <summary>MOTW-001: the platform primitive round-trips at all.</summary>
    [Fact]
    public void The_zone_identifier_round_trips()
    {
        if (!RequireWindows())
        {
            return;
        }

        using var sandbox = new TempDirectory("MOTW-001");
        var file = sandbox.Combine("downloaded.bin");
        File.WriteAllText(file, "content");

        var platform = Platform();
        Assert.True(platform.SupportsMarkOfTheWeb);

        Assert.Null(platform.ReadZoneIdentifier(file));

        platform.WriteZoneIdentifier(file, InternetZone);
        Assert.Equal(InternetZone, platform.ReadZoneIdentifier(file));

        // The stream must not have disturbed the file itself.
        Assert.Equal("content", File.ReadAllText(file));
    }

    /// <summary>MOTW-002: reading a zone from a file that has none is null, not an exception.</summary>
    [Fact]
    public void A_file_without_a_zone_reports_null()
    {
        if (!RequireWindows())
        {
            return;
        }

        using var sandbox = new TempDirectory("MOTW-002");
        var file = sandbox.Combine("local.bin");
        File.WriteAllText(file, "content");

        Assert.Null(Platform().ReadZoneIdentifier(file));
        Assert.Null(Platform().ReadZoneIdentifier(sandbox.Combine("does-not-exist.bin")));
    }

    /// <summary>
    /// MOTW-003: the one that matters. A downloaded archive's zone reaches every file extracted from
    /// it, so SmartScreen and Protected View still fire downstream.
    /// </summary>
    [Fact]
    public async Task The_archives_zone_propagates_to_every_extracted_file()
    {
        if (!RequireWindows())
        {
            return;
        }

        using var sandbox = new TempDirectory("MOTW-003");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("downloaded.zip"),
            ("report.docx", "content"),
            ("nested/macro.xlsm", "content"),
            ("readme.txt", "content"));

        // Tag the archive exactly as a browser would.
        Platform().WriteZoneIdentifier(archive, InternetZone);

        await using var engine = new EngineFixture();
        var destination = sandbox.Combine("dest");

        var outcome = await engine.ExtractAsync(
            archive,
            destination,
            b => b.With(o => o with { PropagateMarkOfTheWeb = true }));

        Assert.Equal(3, outcome.EntriesWritten);

        foreach (var relative in sandbox.Snapshot("dest").Where(p => p.Contains('.', StringComparison.Ordinal)))
        {
            var extracted = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(InternetZone, Platform().ReadZoneIdentifier(extracted));
        }
    }

    /// <summary>
    /// MOTW-004: a local archive has no zone, and extraction must not invent one. Tagging local files
    /// as internet-sourced would train users to click through the warning that matters.
    /// </summary>
    [Fact]
    public async Task A_local_archive_does_not_gain_a_zone()
    {
        if (!RequireWindows())
        {
            return;
        }

        using var sandbox = new TempDirectory("MOTW-004");
        var archive = ArchiveFactory.WriteZip(sandbox.Combine("local.zip"), ("a.txt", "content"));

        await using var engine = new EngineFixture();
        var destination = sandbox.Combine("dest");

        await engine.ExtractAsync(
            archive,
            destination,
            b => b.With(o => o with { PropagateMarkOfTheWeb = true }));

        Assert.Null(Platform().ReadZoneIdentifier(Path.Combine(destination, "a.txt")));
    }

    /// <summary>MOTW-005: turning propagation off turns it off — the setting is real, not decorative.</summary>
    [Fact]
    public async Task Propagation_can_be_disabled()
    {
        if (!RequireWindows())
        {
            return;
        }

        using var sandbox = new TempDirectory("MOTW-005");
        var archive = ArchiveFactory.WriteZip(sandbox.Combine("downloaded.zip"), ("a.txt", "content"));
        Platform().WriteZoneIdentifier(archive, InternetZone);

        await using var engine = new EngineFixture();
        var destination = sandbox.Combine("dest");

        await engine.ExtractAsync(
            archive,
            destination,
            b => b.With(o => o with { PropagateMarkOfTheWeb = false }));

        Assert.Null(Platform().ReadZoneIdentifier(Path.Combine(destination, "a.txt")));
    }

    /// <summary>
    /// MOTW-006: an archive entry that asks for an alternate data stream is still refused. The
    /// asymmetry is deliberate and this test exists so nobody "fixes" it later.
    /// </summary>
    [Fact]
    public async Task An_entry_requesting_an_alternate_data_stream_is_still_refused()
    {
        if (!RequireWindows())
        {
            return;
        }

        using var sandbox = new TempDirectory("MOTW-006");
        var archive = ArchiveFactory.WriteZip(
            sandbox.Combine("hostile.zip"),
            ("readme.txt:Zone.Identifier", "[ZoneTransfer]\r\nZoneId=0\r\n"),
            ("readme.txt", "content"));

        Platform().WriteZoneIdentifier(archive, InternetZone);

        await using var engine = new EngineFixture();
        var destination = sandbox.Combine("dest");

        var outcome = await engine.ExtractAsync(
            archive,
            destination,
            b => b.With(o => o with { PropagateMarkOfTheWeb = true }));

        Assert.True(outcome.HadBlockedEntries, "the ADS entry was not refused.");

        // And the real file still carries the archive's zone rather than the one the archive tried to
        // plant — which is the whole point: the attacker wanted ZoneId=0.
        Assert.Equal(InternetZone, Platform().ReadZoneIdentifier(Path.Combine(destination, "readme.txt")));
    }

    private static IExtractionPlatform Platform()
    {
        // Resolved from the engine rather than constructed, so the test also proves the shipping
        // registration picks the Windows implementation on Windows.
        var engine = new EngineFixture();
        return engine.Get<IExtractionPlatform>();
    }
}
