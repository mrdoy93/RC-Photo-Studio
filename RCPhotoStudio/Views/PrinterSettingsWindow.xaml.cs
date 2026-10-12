using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RCPhotoStudio.Models;

namespace RCPhotoStudio.Views
{
    public sealed class PrinterChoice
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public override string ToString() { return DisplayName ?? Name ?? ""; }
    }

    public sealed class PrinterPaperSizeOption
    {
        public PaperSize PaperSize { get; set; }
        public string DisplayName { get; set; }
        public override string ToString() { return DisplayName ?? ""; }
    }

    public sealed class PrinterMediaTypeOption
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name ?? ""; }
    }

    public sealed class PrinterResolutionOption
    {
        public PrinterResolution Resolution { get; set; }
        public string DisplayName { get; set; }
        public override string ToString() { return DisplayName ?? ""; }
    }

    public partial class PrinterSettingsWindow : Window
    {
        private const short DcMediaTypeNames = 34;
        private const short DcMediaTypes = 35;
        private const int MediaTypeNameLength = 64;

        private readonly PhotoPackage _package;
        private readonly Action _saveSettings;
        private PrinterSettings _workingSettings;
        private bool _loading;
        private int _maximumCopies = 1;
        private int _capabilityRequestId;
        private bool _initialLoadStarted;

        private sealed class CapabilityData
        {
            public PrinterSettings Settings { get; set; }
            public List<PrinterPaperSizeOption> PaperSizes { get; set; }
            public List<PrinterMediaTypeOption> MediaTypes { get; set; }
            public List<PrinterResolutionOption> Resolutions { get; set; }
            public string Error { get; set; }
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DeviceCapabilities(string device, string port, short capability,
            IntPtr output, IntPtr deviceMode);

        public PrinterSettingsWindow(PhotoPackage package, Action saveSettings)
        {
            InitializeComponent();
            _package = package;
            _saveSettings = saveSettings;
            PrinterBox.IsEnabled = false;
            PaperSizeBox.IsEnabled = false;
            PaperTypeBox.IsEnabled = false;
            ColorBox.IsEnabled = false;
            QualityBox.IsEnabled = false;
            CopiesBox.IsEnabled = false;
            DriverPropertiesButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            CapabilitySummary.Text = "Preparing printer options…";
            StatusText.Text = "Loading installed printers.";
            Loaded += PrinterSettingsWindow_Loaded;
        }

        private void PrinterSettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialLoadStarted) return;
            _initialLoadStarted = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LoadPrinters));
        }

        private void LoadPrinters()
        {
            _loading = true;
            try
            {
                var printers = PrinterSettings.InstalledPrinters.Cast<string>()
                    .Select(name => new PrinterChoice
                    {
                        Name = name,
                        DisplayName = CompactPrinterName(name)
                    }).ToList();
                PrinterBox.ItemsSource = printers;
                var selected = printers.FirstOrDefault(item => string.Equals(item.Name, _package.DefaultPrinter,
                    StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    var defaults = new PrinterSettings();
                    selected = defaults.IsValid
                        ? printers.FirstOrDefault(item => string.Equals(item.Name, defaults.PrinterName,
                            StringComparison.OrdinalIgnoreCase))
                        : null;
                }
                PrinterBox.SelectedItem = selected ?? printers.FirstOrDefault();
                PrinterBox.IsEnabled = printers.Count > 0;
            }
            finally
            {
                _loading = false;
            }

            LoadCapabilities(SelectedPrinterName(), null);
        }

        private void PrinterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            LoadCapabilities(SelectedPrinterName(), null);
        }

        private string SelectedPrinterName()
        {
            var choice = PrinterBox.SelectedItem as PrinterChoice;
            return choice == null ? "" : choice.Name;
        }

        private static string CompactPrinterName(string name)
        {
            var compact = (name ?? "").Replace(" Series", "").Replace(" Printer", "").Trim();
            return compact.Length <= 38 ? compact : compact.Substring(0, 35) + "…";
        }

        private static string CompactPaperName(PaperSize paperSize)
        {
            var name = (paperSize.PaperName ?? "Paper").Replace(" x ", " × ").Trim();
            var details = name.IndexOf(" (", StringComparison.Ordinal);
            if (details > 0) name = name.Substring(0, details);
            return name.Length <= 28 ? name : name.Substring(0, 25) + "…";
        }

        private void LoadCapabilities(string printerName, PrinterSettings suppliedSettings)
        {
            var requestId = ++_capabilityRequestId;
            _loading = true;
            PaperSizeBox.IsEnabled = false;
            PaperTypeBox.IsEnabled = false;
            ColorBox.IsEnabled = false;
            QualityBox.IsEnabled = false;
            CopiesBox.IsEnabled = false;
            DriverPropertiesButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            CapabilitySummary.Text = "Loading printer options…";
            StatusText.Text = "Reading available settings from the selected printer.";

            Task.Factory.StartNew(() => ReadCapabilities(printerName, suppliedSettings))
                .ContinueWith(task =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (requestId != _capabilityRequestId) return;
                        ApplyCapabilities(printerName, task.Result);
                    }));
                });
            _loading = false;
        }

        private CapabilityData ReadCapabilities(string printerName, PrinterSettings suppliedSettings)
        {
            var data = new CapabilityData();
            try
            {
                data.Settings = suppliedSettings ?? PrintWorkspaceWindow.CreatePrinterSettings(_package, printerName);
                if (data.Settings == null || !data.Settings.IsValid)
                {
                    data.Error = "The selected printer is not currently available.";
                    return data;
                }

                data.PaperSizes = data.Settings.PaperSizes.Cast<PaperSize>()
                    .Select(sizeItem => new PrinterPaperSizeOption
                    {
                        PaperSize = sizeItem,
                        DisplayName = CompactPaperName(sizeItem)
                    }).ToList();
                data.MediaTypes = LoadMediaTypes(printerName);
                data.Resolutions = data.Settings.PrinterResolutions.Cast<PrinterResolution>()
                    .Select(resolution => new PrinterResolutionOption
                    {
                        Resolution = resolution,
                        DisplayName = ResolutionName(resolution)
                    }).ToList();
                if (data.Resolutions.Count == 0)
                {
                    var defaultResolution = data.Settings.DefaultPageSettings.PrinterResolution;
                    data.Resolutions.Add(new PrinterResolutionOption
                    {
                        Resolution = defaultResolution,
                        DisplayName = ResolutionName(defaultResolution)
                    });
                }
            }
            catch (Exception ex)
            {
                data.Error = "Printer capabilities could not be loaded: " + ex.Message;
            }
            return data;
        }

        private void ApplyCapabilities(string printerName, CapabilityData data)
        {
            _loading = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(data.Error))
                {
                    StatusText.Text = data.Error;
                    CapabilitySummary.Text = "Printer options unavailable";
                    return;
                }

                _workingSettings = data.Settings;
                var paperSizes = data.PaperSizes;
                var mediaTypes = data.MediaTypes;
                var resolutions = data.Resolutions;
                var sameSavedPrinter = string.Equals(_package.DefaultPrinter, printerName,
                    StringComparison.OrdinalIgnoreCase);

                PaperSizeBox.ItemsSource = paperSizes;
                var savedPaperName = sameSavedPrinter ? _package.PrinterPaperSizeName : null;
                var paperSize = paperSizes.FirstOrDefault(item => string.Equals(item.PaperSize.PaperName, savedPaperName,
                                    StringComparison.OrdinalIgnoreCase)) ??
                                paperSizes.FirstOrDefault(item =>
                                    string.Equals(item.PaperSize.PaperName, "A4", StringComparison.OrdinalIgnoreCase) ||
                                    (item.PaperSize.PaperName ?? "").StartsWith("A4 ", StringComparison.OrdinalIgnoreCase)) ??
                                paperSizes.FirstOrDefault(item =>
                                    string.Equals(item.PaperSize.PaperName, _workingSettings.DefaultPageSettings.PaperSize.PaperName,
                                        StringComparison.OrdinalIgnoreCase)) ?? paperSizes.FirstOrDefault();
                PaperSizeBox.SelectedItem = paperSize;

                PaperTypeBox.ItemsSource = mediaTypes;
                PrinterMediaTypeOption mediaType = null;
                if (sameSavedPrinter && _package.PrinterMediaTypeId > 0)
                    mediaType = mediaTypes.FirstOrDefault(item => item.Id == _package.PrinterMediaTypeId);
                if (sameSavedPrinter && mediaType == null &&
                    !string.IsNullOrWhiteSpace(_package.PrinterMediaTypeName) &&
                    !string.Equals(_package.PrinterMediaTypeName, "Printer Default", StringComparison.OrdinalIgnoreCase))
                    mediaType = mediaTypes.FirstOrDefault(item => string.Equals(item.Name,
                        _package.PrinterMediaTypeName, StringComparison.OrdinalIgnoreCase));
                PaperTypeBox.SelectedItem = mediaType ??
                    mediaTypes.FirstOrDefault(item => string.Equals(item.Name, "Epson Matte",
                        StringComparison.OrdinalIgnoreCase)) ?? mediaTypes[0];

                ColorBox.ItemsSource = _workingSettings.SupportsColor
                    ? new[] { "Color", "Black & White" }
                    : new[] { "Black & White" };
                var useColor = sameSavedPrinter
                    ? _package.PrinterPrintInColor
                    : _workingSettings.SupportsColor;
                ColorBox.SelectedItem = useColor && _workingSettings.SupportsColor ? "Color" : "Black & White";

                QualityBox.ItemsSource = resolutions;
                PrinterResolutionOption selectedResolution = null;
                if (sameSavedPrinter)
                    selectedResolution = resolutions.FirstOrDefault(item =>
                        (int)item.Resolution.Kind == _package.PrinterResolutionKind &&
                        item.Resolution.X == _package.PrinterResolutionX &&
                        item.Resolution.Y == _package.PrinterResolutionY);
                if (selectedResolution == null)
                {
                    var current = _workingSettings.DefaultPageSettings.PrinterResolution;
                    selectedResolution = resolutions.FirstOrDefault(item =>
                        item.Resolution.Kind == PrinterResolutionKind.Medium) ??
                        resolutions.FirstOrDefault(item =>
                        item.Resolution.Kind == current.Kind && item.Resolution.X == current.X &&
                        item.Resolution.Y == current.Y) ?? resolutions.FirstOrDefault();
                }
                QualityBox.SelectedItem = selectedResolution;

                _maximumCopies = Math.Max(1, _workingSettings.MaximumCopies);
                var copies = sameSavedPrinter ? _package.PrinterCopies : 1;
                CopiesBox.Text = Math.Max(1, Math.Min(_maximumCopies, copies)).ToString();

                CapabilitySummary.Text = string.Format("{0} paper size(s) • {1} paper type(s) • {2} quality option(s)",
                    paperSizes.Count, Math.Max(0, mediaTypes.Count - 1), resolutions.Count);
                StatusText.Text = "Settings are saved for this package.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Printer capabilities could not be loaded: " + ex.Message;
            }
            finally
            {
                _loading = false;
                var enabled = _workingSettings != null && _workingSettings.IsValid;
                PaperSizeBox.IsEnabled = enabled;
                PaperTypeBox.IsEnabled = enabled;
                ColorBox.IsEnabled = enabled;
                QualityBox.IsEnabled = enabled;
                CopiesBox.IsEnabled = enabled;
                DriverPropertiesButton.IsEnabled = enabled;
                SaveButton.IsEnabled = enabled;
            }
        }

        private static List<PrinterMediaTypeOption> LoadMediaTypes(string printerName)
        {
            var result = new List<PrinterMediaTypeOption>
            {
                new PrinterMediaTypeOption { Id = 0, Name = "Printer Default" }
            };
            IntPtr namesBuffer = IntPtr.Zero;
            IntPtr typesBuffer = IntPtr.Zero;
            try
            {
                var nameCount = DeviceCapabilities(printerName, null, DcMediaTypeNames, IntPtr.Zero, IntPtr.Zero);
                var typeCount = DeviceCapabilities(printerName, null, DcMediaTypes, IntPtr.Zero, IntPtr.Zero);
                var count = Math.Min(nameCount, typeCount);
                if (count <= 0) return result;

                namesBuffer = Marshal.AllocHGlobal(count * MediaTypeNameLength * 2);
                typesBuffer = Marshal.AllocHGlobal(count * 4);
                if (DeviceCapabilities(printerName, null, DcMediaTypeNames, namesBuffer, IntPtr.Zero) <= 0 ||
                    DeviceCapabilities(printerName, null, DcMediaTypes, typesBuffer, IntPtr.Zero) <= 0)
                    return result;

                for (var index = 0; index < count; index++)
                {
                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(namesBuffer, index * MediaTypeNameLength * 2), MediaTypeNameLength) ?? "";
                    var terminator = name.IndexOf('\0');
                    if (terminator >= 0) name = name.Substring(0, terminator);
                    name = name.Trim();
                    if (name.Length == 0) continue;
                    result.Add(new PrinterMediaTypeOption
                    {
                        Id = Marshal.ReadInt32(typesBuffer, index * 4),
                        Name = name
                    });
                }
            }
            catch
            {
                // Some virtual printer drivers do not expose media types.
            }
            finally
            {
                if (namesBuffer != IntPtr.Zero) Marshal.FreeHGlobal(namesBuffer);
                if (typesBuffer != IntPtr.Zero) Marshal.FreeHGlobal(typesBuffer);
            }
            return result;
        }

        private static string ResolutionName(PrinterResolution resolution)
        {
            if (resolution == null) return "Printer Default";
            if (resolution.Kind == PrinterResolutionKind.Medium) return "Standard";
            if (resolution.Kind != PrinterResolutionKind.Custom) return resolution.Kind.ToString();
            return string.Format("{0} × {1} dpi", resolution.X, resolution.Y);
        }

        private PrinterSettings BuildSettingsFromControls()
        {
            var printerName = SelectedPrinterName();
            var settings = _workingSettings != null &&
                           string.Equals(_workingSettings.PrinterName, printerName, StringComparison.OrdinalIgnoreCase)
                ? (PrinterSettings)_workingSettings.Clone()
                : PrintWorkspaceWindow.CreatePrinterSettings(_package, printerName);

            settings.PrinterName = printerName;
            var paperSize = PaperSizeBox.SelectedItem as PrinterPaperSizeOption;
            if (paperSize != null) settings.DefaultPageSettings.PaperSize = paperSize.PaperSize;
            var resolution = QualityBox.SelectedItem as PrinterResolutionOption;
            if (resolution != null) settings.DefaultPageSettings.PrinterResolution = resolution.Resolution;
            settings.DefaultPageSettings.Color = Convert.ToString(ColorBox.SelectedItem) == "Color";
            settings.Copies = (short)GetCopies();
            var mediaType = PaperTypeBox.SelectedItem as PrinterMediaTypeOption;
            if (mediaType != null && mediaType.Id != 0)
                PrintWorkspaceWindow.ApplyMediaType(settings, mediaType.Id);
            return settings;
        }

        private void DriverProperties_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildSettingsFromControls();
            if (settings == null || !settings.IsValid) return;
            using (var dialog = new System.Windows.Forms.PrintDialog())
            {
                dialog.UseEXDialog = true;
                dialog.AllowPrintToFile = false;
                dialog.PrinterSettings = settings;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var returned = (PrinterSettings)dialog.PrinterSettings.Clone();
                _loading = true;
                PrinterBox.SelectedItem = PrinterBox.Items.Cast<PrinterChoice>().FirstOrDefault(item =>
                    string.Equals(item.Name, returned.PrinterName, StringComparison.OrdinalIgnoreCase));
                _loading = false;
                LoadCapabilities(returned.PrinterName, returned);
            }
        }

        private void DecreaseCopies_Click(object sender, RoutedEventArgs e)
        {
            CopiesBox.Text = Math.Max(1, GetCopies() - 1).ToString();
        }

        private void IncreaseCopies_Click(object sender, RoutedEventArgs e)
        {
            CopiesBox.Text = Math.Min(_maximumCopies, GetCopies() + 1).ToString();
        }

        private void CopiesBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(character => !char.IsDigit(character));
        }

        private void CopiesBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || CopiesBox == null) return;
            int copies;
            if (int.TryParse(CopiesBox.Text, out copies) && copies > _maximumCopies)
                CopiesBox.Text = _maximumCopies.ToString();
        }

        private int GetCopies()
        {
            int copies;
            if (!int.TryParse(CopiesBox.Text, out copies)) copies = 1;
            return Math.Max(1, Math.Min(_maximumCopies, copies));
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildSettingsFromControls();
            if (settings == null || !settings.IsValid)
            {
                MessageBox.Show(this, "Select an installed printer first.", "Printer Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var paperSize = PaperSizeBox.SelectedItem as PrinterPaperSizeOption;
            var mediaType = PaperTypeBox.SelectedItem as PrinterMediaTypeOption;
            var resolution = QualityBox.SelectedItem as PrinterResolutionOption;
            _package.DefaultPrinter = settings.PrinterName;
            _package.PrinterPaperSizeName = paperSize == null ? null : paperSize.PaperSize.PaperName;
            _package.PrinterMediaTypeId = mediaType == null ? 0 : mediaType.Id;
            _package.PrinterMediaTypeName = mediaType == null ? null : mediaType.Name;
            _package.PrinterPrintInColor = settings.DefaultPageSettings.Color;
            _package.PrinterCopies = GetCopies();
            _package.PrinterSettingsVersion = 1;
            if (resolution != null)
            {
                _package.PrinterResolutionKind = (int)resolution.Resolution.Kind;
                _package.PrinterResolutionX = resolution.Resolution.X;
                _package.PrinterResolutionY = resolution.Resolution.Y;
            }
            PrintWorkspaceWindow.SavePrinterSettings(_package, settings);
            if (_saveSettings != null) _saveSettings();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
