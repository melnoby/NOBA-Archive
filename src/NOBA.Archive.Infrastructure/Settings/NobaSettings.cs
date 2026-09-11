using System.Text.Json.Serialization;
using NOBA.Archive.Core.Formats;

namespace NOBA.Archive.Infrastructure.Settings;

public enum AppTheme
{
    System = 0,
    Light,
    Dark,
}

public enum CpuUsagePreference
{
    Low = 0,
    Normal,
    High,
}

/// <summary>
/// Persisted settings. <see cref="SchemaVersion"/> drives the migration chain; a malformed file must
/// never block startup (ARCHITECTURE.md §14).
/// </summary>
public sealed record NobaSettings
{
    /// <summary>Bump this whenever a migration step is added, and add the step to <c>SettingsMigrator</c>.</summary>
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>BCP-47 tag, or null to follow the OS. Arabic is a first-class option, not an add-on.</summary>
    public string? UiLanguage { get; init; }

    public string DefaultCreateFormatId { get; init; } = ArchiveFormat.Zip.Id;

    public CompressionProfile DefaultCompressionProfile { get; init; } = CompressionProfile.Balanced;

    public ConflictPolicy DefaultConflictPolicy { get; init; } = ConflictPolicy.Ask;

    public LinkPolicy LinkPolicy { get; init; } = LinkPolicy.Never;

    public IllegalNamePolicy IllegalNamePolicy { get; init; } = IllegalNamePolicy.Sanitize;

    public bool PropagateMarkOfTheWeb { get; init; } = true;

    public int MaxConcurrentOperations { get; init; } = 1;

    public CpuUsagePreference CpuUsage { get; init; } = CpuUsagePreference.Normal;

    public double MaxEntryExpansionRatio { get; init; } = 1000d;

    public long MaxEntryCount { get; init; } = 1_000_000;

    public bool ExplorerContextMenuEnabled { get; init; }

    /// <summary>Off by default, and there is no update URL in the codebase to turn on.</summary>
    public bool AutomaticUpdatesEnabled { get; init; }

    public static NobaSettings Defaults { get; } = new();
}
