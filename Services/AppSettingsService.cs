using System.IO;
using System.Text.Json;

namespace PrinterScanner.Services;

public sealed class PrintPresetPreferences
{
    public string? PrinterName { get; set; }
    public string PaperName { get; set; } = "A4";
    public int? MediaSizeName { get; set; }
    public double PaperWidth { get; set; } = 793.7d;
    public double PaperHeight { get; set; } = 1122.52d;
    public int? DriverPaperKind { get; set; }
    public string ColorMode { get; set; } = "Color";
    public string Quality { get; set; } = "Normal";
    public bool FitToSelectedPrintSize { get; set; } = true;
    public int Copies { get; set; } = 1;
}

public sealed class AppPreferences
{
    public bool IsDarkMode { get; set; }
    public bool HasExplicitThemePreference { get; set; }
    public string? LastScannerId { get; set; }
    public string? LastPrinterName { get; set; }
    public string? SaveFolder { get; set; }
    public int SaveFolderVersion { get; set; }
    public List<PrintPresetPreferences?> PrintPresets { get; set; } = [];
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
