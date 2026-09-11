using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Infrastructure.Settings;

public sealed record SettingsLoadResult(NobaSettings Settings, bool WasMigrated, bool WasQuarantined, string? QuarantinePath);

/// <summary>
/// Loads and saves settings. Two rules drive the design: startup never fails because of this file,
/// and a write is either fully applied or not applied at all.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IAppDataPathProvider _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _gate = new();

    public SettingsStore(
        IAppDataPathProvider paths,
        IFileSystem fileSystem,
        IClock clock,
        ILogger<SettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _fileSystem = fileSystem;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Never throws. A broken file is moved aside and defaults are used.</summary>
    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            var path = _paths.SettingsFilePath;
            if (!_fileSystem.FileExists(path))
            {
                return new SettingsLoadResult(NobaSettings.Defaults, false, false, null);
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Settings could not be read; continuing with defaults.");
                return new SettingsLoadResult(NobaSettings.Defaults, false, false, null);
            }

            try
            {
                var node = JsonNode.Parse(text) as JsonObject
                    ?? throw new JsonException("The settings file is not a JSON object.");
                var migrated = SettingsMigrator.Migrate(node, out var steps);
                var settings = migrated.Deserialize<NobaSettings>(SerializerOptions)
                    ?? throw new JsonException("The settings file deserialised to null.");

                if (steps > 0)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Applied {Steps} settings migration step(s).", steps);
                    }

                    Save(settings);
                }

                return new SettingsLoadResult(settings, steps > 0, false, null);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                var quarantine = Quarantine(path, ex);
                return new SettingsLoadResult(NobaSettings.Defaults, false, true, quarantine);
            }
        }
    }

    /// <summary>
    /// Writes to a sibling temp file and publishes it. A crash mid-write leaves the previous settings
    /// intact rather than a truncated file.
    /// </summary>
    public void Save(NobaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            var path = _paths.SettingsFilePath;
            _fileSystem.CreateDirectory(_paths.AppDataRoot);

            var payload = JsonSerializer.Serialize(settings with { SchemaVersion = NobaSettings.CurrentSchemaVersion }, SerializerOptions);
            var temp = path + ".tmp";

            File.WriteAllText(temp, payload);
            _fileSystem.MoveFile(temp, path, overwrite: true);
        }
    }

    private string? Quarantine(string path, Exception cause)
    {
        var stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var quarantine = Path.Combine(
            _paths.AppDataRoot,
            string.Create(CultureInfo.InvariantCulture, $"settings.corrupt-{stamp}.json"));

        try
        {
            _fileSystem.MoveFile(path, quarantine, overwrite: false);
            _logger.LogWarning(
                cause,
                "The settings file was unreadable and has been moved to {Quarantine}. Defaults are in use.",
                quarantine);
            return quarantine;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The settings file was unreadable and could not be moved aside. Defaults are in use.");
            return null;
        }
    }
}
