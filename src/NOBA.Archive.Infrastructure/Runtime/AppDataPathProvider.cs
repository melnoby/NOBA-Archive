using NOBA.Archive.Core.Abstractions;

namespace NOBA.Archive.Infrastructure.Runtime;

/// <summary>
/// Where all mutable state lives. Injectable so NobaOS can relocate it and so portable mode is a
/// configuration change rather than a rewrite.
/// </summary>
public sealed class AppDataPathProvider : IAppDataPathProvider
{
    public const string DefaultFolderName = "NOBA Archive";

    public AppDataPathProvider(string? appDataRoot = null)
    {
        AppDataRoot = string.IsNullOrWhiteSpace(appDataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DefaultFolderName)
            : Path.GetFullPath(appDataRoot);
    }

    public string AppDataRoot { get; }

    public string SettingsFilePath => Path.Combine(AppDataRoot, "settings.json");

    public string LogDirectory => Path.Combine(AppDataRoot, "logs");

    public string TempRoot => Path.Combine(AppDataRoot, "tmp");
}
