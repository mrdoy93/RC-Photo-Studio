using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace RCPhotoStudio.Services
{
    public static class ThemeService
    {
        public static bool IsDark { get; private set; }

        private static string SettingsFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RC Photo Studio"); }
        }

        public static bool LoadPreference()
        {
            try
            {
                var file = Path.Combine(SettingsFolder, "theme.txt");
                return File.Exists(file) && File.ReadAllText(file).Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Apply(bool dark)
        {
            IsDark = dark;
            if (Application.Current == null) return;
            var resources = Application.Current.Resources;
            Set(resources, "AppBackgroundBrush", dark ? "#111827" : "#F4F6FA");
            Set(resources, "SurfaceBrush", dark ? "#1F2937" : "#FFFFFF");
            Set(resources, "SurfaceAltBrush", dark ? "#293548" : "#EDF1F7");
            Set(resources, "TextBrush", dark ? "#F8FAFC" : "#172033");
            Set(resources, "MutedTextBrush", dark ? "#B8C2D2" : "#5E687B");
            Set(resources, "BorderBrush", dark ? "#46546A" : "#9DA8BA");
            Set(resources, "AccentBrush", dark ? "#60A5FA" : "#2563EB");
            Set(resources, "AccentHoverBrush", dark ? "#93C5FD" : "#1D4ED8");
            Set(resources, "DangerBrush", dark ? "#F87171" : "#C62828");
            Set(resources, "SuccessBrush", dark ? "#34D399" : "#087F5B");

            // Native WPF templates sometimes read system colors instead of the
            // application brush names. Override those keys so text inside combo
            // boxes, list items, tables, and disabled controls follows the theme.
            resources[SystemColors.WindowBrushKey] = resources["SurfaceBrush"];
            resources[SystemColors.ControlBrushKey] = resources["SurfaceBrush"];
            resources[SystemColors.MenuBrushKey] = resources["SurfaceBrush"];
            resources[SystemColors.WindowTextBrushKey] = resources["TextBrush"];
            resources[SystemColors.ControlTextBrushKey] = resources["TextBrush"];
            resources[SystemColors.MenuTextBrushKey] = resources["TextBrush"];
            resources[SystemColors.GrayTextBrushKey] = resources["MutedTextBrush"];
            resources[SystemColors.HighlightBrushKey] = resources["AccentBrush"];
            resources[SystemColors.HighlightTextBrushKey] = Brushes.White;

            try
            {
                Directory.CreateDirectory(SettingsFolder);
                File.WriteAllText(Path.Combine(SettingsFolder, "theme.txt"), dark ? "dark" : "light");
            }
            catch { }
        }

        private static void Set(ResourceDictionary resources, string key, string color)
        {
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
