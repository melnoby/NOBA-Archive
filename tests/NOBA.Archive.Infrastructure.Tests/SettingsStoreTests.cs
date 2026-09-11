using System.Text.Json.Nodes;
using NOBA.Archive.Core.Formats;
using NOBA.Archive.Infrastructure.FileSystem;
using NOBA.Archive.Infrastructure.Runtime;
using NOBA.Archive.Infrastructure.Settings;
using NOBA.Archive.TestKit.Fakes;
using Xunit;

namespace NOBA.Archive.Infrastructure.Tests;

/// <summary>
/// ARCHITECTURE.md §14: startup never fails because of this file, and a write is all-or-nothing.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noba-settings-" + Guid.NewGuid().ToString("N"));

    private SettingsStore CreateStore(out AppDataPathProvider paths)
    {
        paths = new AppDataPathProvider(_root);
        Directory.CreateDirectory(_root);
        return new SettingsStore(paths, new PhysicalFileSystem(), new FakeClock(), new NullLogger<SettingsStore>());
    }

    [Fact]
    public void Returns_defaults_when_no_file_exists()
    {
        var store = CreateStore(out _);

        var result = store.Load();

        Assert.False(result.WasQuarantined);
        Assert.Equal(NobaSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal(ConflictPolicy.Ask, result.Settings.DefaultConflictPolicy);
    }

    [Fact]
    public void Round_trips_saved_settings()
    {
        var store = CreateStore(out _);
        var saved = NobaSettings.Defaults with
        {
            Theme = AppTheme.Dark,
            UiLanguage = "ar-EG",
            MaxConcurrentOperations = 3,
        };

        store.Save(saved);
        var loaded = store.Load().Settings;

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal("ar-EG", loaded.UiLanguage);
        Assert.Equal(3, loaded.MaxConcurrentOperations);
    }

    /// <summary>A malformed file must not stop the product from starting.</summary>
    [Fact]
    public void Quarantines_a_corrupt_file_and_falls_back_to_defaults()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.SettingsFilePath, "{ this is not json");

        var result = store.Load();

        Assert.True(result.WasQuarantined);
        Assert.NotNull(result.QuarantinePath);
        Assert.True(File.Exists(result.QuarantinePath));
        Assert.False(File.Exists(paths.SettingsFilePath));
        Assert.Equal(NobaSettings.Defaults.Theme, result.Settings.Theme);
    }

    [Fact]
    public void Survives_a_file_that_is_valid_json_but_the_wrong_shape()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.SettingsFilePath, "[1, 2, 3]");

        var result = store.Load();

        Assert.True(result.WasQuarantined);
        Assert.Equal(NobaSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
    }

    /// <summary>
    /// A v1 file must not be able to re-enable link creation through the old permissive flag.
    /// </summary>
    [Fact]
    public void Migrates_a_v1_file_and_does_not_carry_over_the_permissive_link_flag()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.SettingsFilePath, """{"schemaVersion":1,"allowSymlinks":true,"theme":"Dark"}""");

        var result = store.Load();

        Assert.True(result.WasMigrated);
        Assert.Equal(NobaSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal(LinkPolicy.Never, result.Settings.LinkPolicy);
        Assert.Equal(AppTheme.Dark, result.Settings.Theme);
    }

    [Fact]
    public void Rewrites_the_file_after_migrating_so_it_only_happens_once()
    {
        var store = CreateStore(out var paths);
        File.WriteAllText(paths.SettingsFilePath, """{"schemaVersion":1,"allowSymlinks":true}""");

        Assert.True(store.Load().WasMigrated);
        Assert.False(store.Load().WasMigrated);
    }

    [Fact]
    public void Treats_an_unversioned_file_as_version_one()
    {
        var node = JsonNode.Parse("""{"theme":"Light"}""") as JsonObject;

        Assert.NotNull(node);
        Assert.Equal(1, SettingsMigrator.ReadVersion(node));
    }

    [Fact]
    public void Migration_advances_to_the_current_version()
    {
        var node = JsonNode.Parse("""{"schemaVersion":1}""") as JsonObject;
        Assert.NotNull(node);

        var migrated = SettingsMigrator.Migrate(node, out var steps);

        Assert.Equal(1, steps);
        Assert.Equal(NobaSettings.CurrentSchemaVersion, SettingsMigrator.ReadVersion(migrated));
    }

    [Fact]
    public void An_already_current_file_needs_no_migration()
    {
        var node = JsonNode.Parse($$"""{"schemaVersion":{{NobaSettings.CurrentSchemaVersion}}}""") as JsonObject;
        Assert.NotNull(node);

        SettingsMigrator.Migrate(node, out var steps);

        Assert.Equal(0, steps);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
