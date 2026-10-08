using System.IO;
using System.Text.Json;

namespace PrinterScanner.Services;

public sealed class AppPreferences
{
    public bool IsDarkMode { get; set; }
    public bool HasExplicitThemePreference { get; set; }
    public string? LastScannerId { get; set; }
    public string? LastPrinterName { get; set; }
    public string? SaveFolder { get; set; }
    public int SaveFolderVersion { get; set; }
}

public sealed class AppSettingsService
{
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrinterScanner");
        Directory.CreateDirectory(folder);
        _settingsPath = Path.Combine(folder, "settings.json");
    }

    public AppPreferences Load()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_settingsPath)) ?? new AppPreferences()
                : new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    public void Save(AppPreferences preferences)
    {
        var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}
