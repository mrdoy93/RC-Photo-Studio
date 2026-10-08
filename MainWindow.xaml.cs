using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PrinterScanner.Models;
using PrinterScanner.Services;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace PrinterScanner;

public partial class MainWindow : Window
{
    private readonly TempSession _tempSession = new();
    private readonly ImageService _imageService = new();
    private readonly WiaScannerService _scannerService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly AppPreferences _preferences;
    private readonly PrinterService _printerService;
    private readonly PdfService _pdfService;
    private bool _isBusy;
    private double _previewZoom = 1d;
    private bool _isCropMode;
    private bool _isSelectingCrop;
    private WpfPoint _cropStart;
    private CancellationTokenSource? _scanCancellation;
    private int _scanCapabilityRequest;

    private const double PreviewZoomStep = 0.25d;
    private const double MinimumPreviewZoom = 0.25d;
    private const double MaximumPreviewZoom = 4d;

    public ObservableCollection<ScanPage> Pages { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _printerService = new PrinterService(_imageService);
        _pdfService = new PdfService(_imageService, _tempSession);
        _preferences = _settingsService.Load();
        if (!_preferences.HasExplicitThemePreference)
        {
            _preferences.IsDarkMode = false;
            _preferences.HasExplicitThemePreference = true;
        }
        if (_preferences.SaveFolderVersion < 1)
        {
            _preferences.SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _preferences.SaveFolderVersion = 1;
        }
        SaveLocationText.Text = GetInitialSaveFolder(_preferences.SaveFolder);
        ApplyTheme(_preferences.IsDarkMode);
        SourceInitialized += (_, _) => ApplyWindowChromeTheme(_preferences.IsDarkMode);
        Loaded += MainWindow_Loaded;
        UpdateDocumentState();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        SetBusy(true, "Detecting Epson and Brother devices…");
        try
        {
            var scannerTask = _scannerService.GetScannersAsync();
            var printers = _printerService.GetPrinters();
            var scanners = await scannerTask;

            ScannerCombo.ItemsSource = scanners;
            PrinterCombo.ItemsSource = printers;

            ScannerCombo.SelectedItem = scanners.FirstOrDefault(scanner => scanner.Id == _preferences.LastScannerId)
                                        ?? scanners.FirstOrDefault();
            PrinterCombo.SelectedItem = printers.FirstOrDefault(printer => printer.Name == _preferences.LastPrinterName)
                                        ?? printers.FirstOrDefault();

            if (scanners.Count == 0)
            {
                SetStatus("No WIA scanner found", StatusKind.Warning);
            }
            else if (printers.Count == 0)
            {
                SetStatus("Scanner ready; no printer installed", StatusKind.Warning);
            }
            else
            {
                SetStatus("Scanner ready", StatusKind.Success);
            }
        }
        catch (Exception exception)
        {
            SetStatus("Device detection failed", StatusKind.Error);
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanPageAsync(replacePage: null);
    }

    private async Task ScanPageAsync(ScanPage? replacePage)
    {
        if (ScannerCombo.SelectedItem is not ScannerDevice scanner)
        {
            ShowError("Connect and select an Epson or Brother scanner first.");
            return;
        }

        var options = ReadScanOptions();
        var rawPath = _tempSession.NewPath(".png");
        var processedPath = _tempSession.NewPath(".png");
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;

        SetScanProgress(true, 0, "Preparing the scanner…");
        SetBusy(true, "Scanning page…");
        try
        {
            var progress = new Progress<int>(UpdateScannerAcquisitionProgress);
            await _scannerService.ScanAsync(scanner, options, rawPath, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetScanProgress(true, 80, "Processing the scanned image…");
            SetStatus("Optimizing scanned page…", StatusKind.Working);
            await Task.Run(() => _imageService.ProcessScan(rawPath, processedPath, options.ColorMode), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetScanProgress(true, 98, "Preparing the page preview…");

            var page = new ScanPage(
                processedPath,
                _imageService.LoadThumbnail(processedPath),
                options.ColorMode);

            if (replacePage is not null)
            {
                var index = Pages.IndexOf(replacePage);
                if (index >= 0)
                {
                    Pages[index] = page;
                    PageList.SelectedIndex = index;
                }
            }
            else
            {
                Pages.Add(page);
                PageList.SelectedItem = page;
            }

            RenumberPages();
            UpdateDocumentState();
            SetScanProgress(true, 100, "Scan complete");
            await Task.Delay(120);
            SetScanProgress(false);
            var automaticJpegPath = await SaveAutomaticJpegCopyAsync(page);
            SetStatus(
                ContinuousScanCheck.IsChecked == true
                    ? $"Page scanned; JPG saved to {Path.GetDirectoryName(automaticJpegPath)}; ready for the next page"
                    : $"Page scanned; JPG saved to {Path.GetDirectoryName(automaticJpegPath)}",
                StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            TryDelete(processedPath);
            SetStatus("Scan cancelled", StatusKind.Warning);
        }
        catch (Exception exception)
        {
            SetStatus("Scan failed", StatusKind.Error);
            ShowError(exception.Message);
        }
        finally
        {
            TryDelete(rawPath);
            SetScanProgress(false);
            SetBusy(false);
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveOutputAsync(finishScanning: true);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveIndividualPdfArchiveAsync();
    }

    private async Task SaveIndividualPdfArchiveAsync()
    {
        if (Pages.Count == 0)
        {
            ShowError("Scan at least one page before saving.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Choose where to save the individual compressed PDFs",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            InitialDirectory = GetSaveFolder(),
            FileName = $"Scan-{DateTime.Now:yyyy-MM-dd-HHmm}-Individual-PDFs.zip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var archivePath = dialog.FileName;
        var pages = Pages.ToList();
        var compression = ReadCompression();
        var paperSize = SelectedText(PaperSizeCombo);

        SetBusy(true, "Compressing individual PDFs into ZIP…");
        try
        {
            await _pdfService.CreateIndividualArchiveAsync(pages, archivePath, compression, paperSize);
            EstimatedSizeText.Text = $"Saved ZIP: {FormatSize(new FileInfo(archivePath).Length)}";
            SetStatus($"ZIP saved with {pages.Count} individual PDF{(pages.Count == 1 ? string.Empty : "s")}", StatusKind.Success);
        }
        catch (Exception exception)
        {
            TryDelete(archivePath);
            SetStatus("Individual PDF ZIP creation failed", StatusKind.Error);
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveOutputAsync(bool finishScanning)
    {
        if (Pages.Count == 0)
        {
            ShowError("Scan at least one page before saving.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = finishScanning ? "Finish scanning and save the combined PDF" : "Save compressed PDF",
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            InitialDirectory = GetSaveFolder(),
            FileName = $"Scan-{DateTime.Now:yyyy-MM-dd-HHmm}.pdf"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetBusy(true, "Compressing and combining PDF…");
        try
        {
            await _pdfService.CreateAsync(
                Pages.ToList(),
                dialog.FileName,
                ReadCompression(),
                SelectedText(PaperSizeCombo));
            var size = new FileInfo(dialog.FileName).Length;
            EstimatedSizeText.Text = $"Saved PDF: {FormatSize(size)}";
            SetStatus("PDF saved", StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus("PDF creation failed", StatusKind.Error);
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<string> SaveAutomaticJpegCopyAsync(ScanPage page)
    {
        var destination = GetSaveFolder();
        Directory.CreateDirectory(destination);
        var outputPath = Path.Combine(destination, $"Scan-{DateTime.Now:yyyy-MM-dd-HHmmssfff}.jpg");
        var quality = ReadJpegQuality();
        await Task.Run(() => _imageService.SaveJpeg(page, outputPath, quality));
        return outputPath;
    }

    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is not PrinterDevice printer)
        {
            ShowError("Select an installed printer first.");
            return;
        }

        var printOptions = ReadPrintOptions();
        if (printOptions.PaperSize is "A3" or "Legal" &&
            !_printerService.SupportsPaperSize(printer, printOptions.PaperSize))
        {
            const string message = "A3 or Legal is not available on this Printer Model";
            SetStatus(message, StatusKind.Error);
            ShowError(message);
            return;
        }

        if (ScannerCombo.SelectedItem is not ScannerDevice scanner)
        {
            ShowError("Connect and select an Epson or Brother scanner first.");
            return;
        }

        var options = ReadScanOptions();
        var printDescription = DescribePrintOptions(printOptions);
        var rawPath = _tempSession.NewPath(".png");
        var processedPath = _tempSession.NewPath(".png");
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;

        SetScanProgress(true, 0, "Preparing the scanner…");
        SetBusy(true, $"Scanning for direct print: {printDescription}…");
        try
        {
            var progress = new Progress<int>(UpdateScannerAcquisitionProgress);
            await _scannerService.ScanAsync(scanner, options, rawPath, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetScanProgress(true, 80, "Processing the scanned image…");
            SetStatus("Optimizing scanned page…", StatusKind.Working);
            await Task.Run(() => _imageService.ProcessScan(rawPath, processedPath, options.ColorMode), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var page = new ScanPage(
                processedPath,
                _imageService.LoadThumbnail(processedPath),
                options.ColorMode);

            SetScanProgress(true, 100, "Scan complete");
            await Task.Delay(120);
            SetScanProgress(false);
            var automaticJpegPath = await SaveAutomaticJpegCopyAsync(page);
            SetStatus($"Sending print job: {printDescription}…", StatusKind.Working);
            await _printerService.PrintAsync(printer, [page], printOptions);
            SetStatus($"Printed with {printDescription}; JPG saved to {Path.GetDirectoryName(automaticJpegPath)}", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            TryDelete(processedPath);
            SetStatus("Direct scan and print cancelled", StatusKind.Warning);
        }
        catch (Exception exception)
        {
            SetStatus("Direct scan and print failed", StatusKind.Error);
            ShowError(exception.Message);
        }
        finally
        {
            TryDelete(rawPath);
            SetScanProgress(false);
            SetBusy(false);
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void PrinterPropertiesButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is not PrinterDevice printer)
        {
            ShowError("Select an installed printer first.");
            return;
        }

        try
        {
            SettingsPopup.IsOpen = false;
            Activate();
            UpdateLayout();
            var owner = new WindowInteropHelper(this).Handle;
            if (_printerService.ShowProperties(owner, printer))
            {
                SetStatus("Printer properties updated", StatusKind.Success);
            }
        }
        catch (Exception exception)
        {
            SetStatus("Printer properties unavailable", StatusKind.Error);
            ShowError(exception.Message);
        }
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is not ScanPage page)
        {
            return;
        }

        page.Rotation += 90;
        page.Thumbnail = _imageService.LoadThumbnail(page.FilePath, page.Rotation);
        _previewZoom = 1d;
        PreviewScrollViewer.ScrollToHorizontalOffset(0);
        PreviewScrollViewer.ScrollToVerticalOffset(0);
        ShowSelectedPage();
        SetStatus("Page rotated and fitted to preview", StatusKind.Success);
    }

    private void CropButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is not ScanPage)
        {
            return;
        }

        _previewZoom = 1d;
        UpdatePreviewZoom();
        PreviewScrollViewer.ScrollToHorizontalOffset(0);
        PreviewScrollViewer.ScrollToVerticalOffset(0);
        PreviewImage.UpdateLayout();

        _isCropMode = true;
        CropCanvas.Visibility = Visibility.Visible;
        CropSelectionRectangle.Visibility = Visibility.Collapsed;
        PreviewScrollViewer.PanningMode = PanningMode.None;
        ZoomOutButton.IsEnabled = false;
        ZoomInButton.IsEnabled = false;
        ZoomFitButton.IsEnabled = false;
        RotateButton.IsEnabled = false;
        CropButton.Visibility = Visibility.Collapsed;
        ApplyCropButton.Visibility = Visibility.Visible;
        CancelCropButton.Visibility = Visibility.Visible;
        DeleteButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        SetStatus("Drag over the preview to select the area to keep", StatusKind.Working);
    }

    private async void ApplyCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is not ScanPage page ||
            CropSelectionRectangle.Visibility != Visibility.Visible ||
            CropSelectionRectangle.Width < 5 ||
            CropSelectionRectangle.Height < 5 ||
            CropCanvas.ActualWidth <= 0 ||
            CropCanvas.ActualHeight <= 0)
        {
            ShowError("Drag a crop rectangle over the page first.");
            return;
        }

        var left = Canvas.GetLeft(CropSelectionRectangle) / CropCanvas.ActualWidth;
        var top = Canvas.GetTop(CropSelectionRectangle) / CropCanvas.ActualHeight;
        var width = CropSelectionRectangle.Width / CropCanvas.ActualWidth;
        var height = CropSelectionRectangle.Height / CropCanvas.ActualHeight;
        var croppedPath = _tempSession.NewPath(".png");
        var index = Pages.IndexOf(page);

        SetBusy(true, "Cropping page…");
        try
        {
            await Task.Run(() => _imageService.CropPage(page, croppedPath, left, top, width, height));
            var croppedPage = new ScanPage(
                croppedPath,
                _imageService.LoadThumbnail(croppedPath),
                page.ColorMode);

            ExitCropMode();
            Pages[index] = croppedPage;
            PageList.SelectedIndex = index;
            PageList.SelectedItem = croppedPage;
            RenumberPages();
            UpdateDocumentState();
            TryDelete(page.FilePath);
            SetStatus("Page cropped and fitted to preview", StatusKind.Success);
        }
        catch (Exception exception)
        {
            TryDelete(croppedPath);
            SetStatus("Page crop failed", StatusKind.Error);
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CancelCropButton_Click(object sender, RoutedEventArgs e)
    {
        ExitCropMode();
        SetStatus("Crop cancelled", StatusKind.Success);
    }

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCropMode)
        {
            return;
        }

        _cropStart = ClampToCropCanvas(e.GetPosition(CropCanvas));
        _isSelectingCrop = true;
        CropCanvas.CaptureMouse();
        CropSelectionRectangle.Visibility = Visibility.Visible;
        UpdateCropSelection(_cropStart);
        e.Handled = true;
    }

    private void CropCanvas_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_isSelectingCrop)
        {
            UpdateCropSelection(ClampToCropCanvas(e.GetPosition(CropCanvas)));
        }
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelectingCrop)
        {
            return;
        }

        UpdateCropSelection(ClampToCropCanvas(e.GetPosition(CropCanvas)));
        _isSelectingCrop = false;
        CropCanvas.ReleaseMouseCapture();
        if (CropSelectionRectangle.Width < 5 || CropSelectionRectangle.Height < 5)
        {
            CropSelectionRectangle.Visibility = Visibility.Collapsed;
            SetStatus("Select a larger crop area", StatusKind.Warning);
        }
        else
        {
            SetStatus("Crop selection ready; click Apply Crop", StatusKind.Success);
        }

        e.Handled = true;
    }

    private WpfPoint ClampToCropCanvas(WpfPoint point) => new(
        Math.Clamp(point.X, 0, CropCanvas.ActualWidth),
        Math.Clamp(point.Y, 0, CropCanvas.ActualHeight));

    private void UpdateCropSelection(WpfPoint current)
    {
        var left = Math.Min(_cropStart.X, current.X);
        var top = Math.Min(_cropStart.Y, current.Y);
        Canvas.SetLeft(CropSelectionRectangle, left);
        Canvas.SetTop(CropSelectionRectangle, top);
        CropSelectionRectangle.Width = Math.Abs(current.X - _cropStart.X);
        CropSelectionRectangle.Height = Math.Abs(current.Y - _cropStart.Y);
    }

    private void ExitCropMode()
    {
        _isCropMode = false;
        _isSelectingCrop = false;
        CropCanvas.ReleaseMouseCapture();
        CropCanvas.Visibility = Visibility.Collapsed;
        CropSelectionRectangle.Visibility = Visibility.Collapsed;
        PreviewScrollViewer.PanningMode = PanningMode.Both;
        ZoomOutButton.IsEnabled = true;
        ZoomInButton.IsEnabled = true;
        ZoomFitButton.IsEnabled = true;
        RotateButton.IsEnabled = true;
        CropButton.Visibility = Visibility.Visible;
        ApplyCropButton.Visibility = Visibility.Collapsed;
        CancelCropButton.Visibility = Visibility.Collapsed;
        DeleteButton.IsEnabled = true;
        RescanButton.IsEnabled = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is not ScanPage page)
        {
            return;
        }

        var index = Pages.IndexOf(page);
        Pages.Remove(page);
        TryDelete(page.FilePath);
        RenumberPages();
        PageList.SelectedIndex = Math.Min(index, Pages.Count - 1);
        UpdateDocumentState();
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is ScanPage page)
        {
            await ScanPageAsync(page);
        }
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isCropMode)
        {
            ExitCropMode();
        }

        _previewZoom = 1d;
        ShowSelectedPage();
    }

    private void ShowSelectedPage()
    {
        if (PageList.SelectedItem is not ScanPage page)
        {
            PreviewImage.Source = null;
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
            EmptyPreviewText.Visibility = Visibility.Visible;
            PageCounterText.Text = Pages.Count == 0 ? "No pages" : $"{Pages.Count} pages";
            ZoomLevelText.Text = "100%";
            return;
        }

        PreviewImage.Source = _imageService.LoadForDisplay(page.FilePath, page.Rotation);
        EmptyPreviewText.Visibility = Visibility.Collapsed;
        PageCounterText.Text = $"Page {page.DisplayNumber} of {Pages.Count}";
        Dispatcher.BeginInvoke(UpdatePreviewZoom);
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(_previewZoom - PreviewZoomStep);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(_previewZoom + PreviewZoomStep);
    }

    private void ZoomFitButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(1d);
    }

    private void PreviewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewZoom();
    }

    private void SetPreviewZoom(double zoom)
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        _previewZoom = Math.Clamp(zoom, MinimumPreviewZoom, MaximumPreviewZoom);
        UpdatePreviewZoom();
    }

    private void UpdatePreviewZoom()
    {
        if (PreviewImage.Source is not BitmapSource bitmap ||
            bitmap.PixelWidth <= 0 ||
            bitmap.PixelHeight <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(1d, PreviewScrollViewer.ViewportWidth - 32d);
        var availableHeight = Math.Max(1d, PreviewScrollViewer.ViewportHeight - 32d);
        var fitScale = Math.Min(availableWidth / bitmap.PixelWidth, availableHeight / bitmap.PixelHeight);

        PreviewImage.Width = bitmap.PixelWidth * fitScale * _previewZoom;
        PreviewImage.Height = bitmap.PixelHeight * fitScale * _previewZoom;
        ZoomLevelText.Text = $"{_previewZoom * 100:0}%";
    }

    private void ContinuousScanCheck_Click(object sender, RoutedEventArgs e)
    {
        ContinuousScanCheck.Content = ContinuousScanCheck.IsChecked == true ? "On" : "Off";
        UpdateDocumentState();
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!_preferences.IsDarkMode);
    }

    private void ScanSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettingsPopup(showScanSettings: true);
    }

    private void PrintSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettingsPopup(showScanSettings: false);
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SettingsPopup.IsOpen &&
            !SettingsPopupHost.IsMouseOver &&
            !SettingsPanel.IsMouseOver &&
            !ScanSettingsButton.IsMouseOver &&
            !PrintSettingsButton.IsMouseOver)
        {
            SettingsPopup.IsOpen = false;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        SettingsPopup.IsOpen = false;
    }

    private void ToggleSettingsPopup(bool showScanSettings)
    {
        EnsureSettingsPopupContent();
        var selectedPanelIsOpen = SettingsPopup.IsOpen &&
                                  ScanSettingsPanel.Visibility == (showScanSettings ? Visibility.Visible : Visibility.Collapsed);
        if (selectedPanelIsOpen)
        {
            SettingsPopup.IsOpen = false;
            return;
        }

        ScanSettingsPanel.Visibility = showScanSettings ? Visibility.Visible : Visibility.Collapsed;
        PrintSettingsPanel.Visibility = showScanSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPopup.IsOpen = true;
    }

    private void EnsureSettingsPopupContent()
    {
        if (SettingsPopupHost.Child is not null)
        {
            return;
        }

        SettingsStorageBorder.Child = null;
        SettingsPopupHost.Child = SettingsPanel;
    }

    private void ApplyTheme(bool dark)
    {
        _preferences.IsDarkMode = dark;
        SetBrush("WindowBackgroundBrush", dark ? "#0B1220" : "#F5F7FA");
        SetBrush("PanelBackgroundBrush", dark ? "#111827" : "#FFFFFF");
        SetBrush("ControlBackgroundBrush", dark ? "#1F2937" : "#E2E8F0");
        SetBrush("ControlHoverBrush", dark ? "#2C3A4F" : "#CBD5E1");
        SetBrush("ControlPressedBrush", dark ? "#37475F" : "#94A3B8");
        SetBrush("DisabledBackgroundBrush", dark ? "#182132" : "#D1D5DB");
        SetBrush("PreviewBackgroundBrush", dark ? "#0B1322" : "#E2E8F0");
        SetBrush("TextBrush", dark ? "#F8FAFC" : "#0F172A");
        SetBrush("MutedTextBrush", dark ? "#CBD5E1" : "#334155");
        SetBrush("DisabledTextBrush", dark ? "#94A3B8" : "#475569");
        SetBrush("SelectionBrush", dark ? "#243B63" : "#BFDBFE");
        SetBrush("BorderBrush", dark ? "#475569" : "#64748B");
        SetBrush("AccentBrush", dark ? "#3B82F6" : "#0B5ED7");
        SetBrush("AccentHoverBrush", dark ? "#60A5FA" : "#094EA5");
        SetBrush("AccentPressedBrush", dark ? "#2563EB" : "#073B7A");
        ThemeToggleButton.Content = dark ? "☾  Dark" : "☀  Light";
        ApplyWindowChromeTheme(dark);
    }

    private static void SetBrush(string key, string color)
    {
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void ApplyWindowChromeTheme(bool dark)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
    }

    private ScanOptions ReadScanOptions() => new(
        SelectedText(SourceCombo) == "Flatbed" ? ScanSource.Flatbed : ScanSource.AutomaticDocumentFeeder,
        SelectedText(ScanTypeCombo) switch
        {
            "Color" => ScanColorMode.Color,
            "True Gray" => ScanColorMode.TrueGray,
            "Black & White" => ScanColorMode.BlackAndWhite,
            _ => ScanColorMode.GrayErrorDiffusion
        },
        ReadDpi(),
        SelectedText(PaperSizeCombo));

    private PrintOptions ReadPrintOptions() => new(
        SelectedText(PrintPaperSizeCombo),
        SelectedText(PrintColorCombo) switch
        {
            "Grayscale" => PrintColorMode.Grayscale,
            "Black & White" => PrintColorMode.BlackAndWhite,
            _ => PrintColorMode.Color
        },
        SelectedText(PrintQualityCombo) switch
        {
            "Draft" => PrintQualityMode.Draft,
            "High" => PrintQualityMode.High,
            _ => PrintQualityMode.Normal
        },
        SelectedText(PrintReductionCombo),
        ReadCopyCount());

    private int ReadCopyCount()
    {
        var copies = int.TryParse(PrintCopiesTextBox.Text, out var requestedCopies)
            ? Math.Clamp(requestedCopies, 1, 999)
            : 1;
        PrintCopiesTextBox.Text = copies.ToString();
        return copies;
    }

    private void DecreaseCopiesButton_Click(object sender, RoutedEventArgs e) =>
        SetCopyCount(ReadCopyCount() - 1);

    private void IncreaseCopiesButton_Click(object sender, RoutedEventArgs e) =>
        SetCopyCount(ReadCopyCount() + 1);

    private void SetCopyCount(int copies)
    {
        PrintCopiesTextBox.Text = Math.Clamp(copies, 1, 999).ToString();
        PrintCopiesTextBox.SelectAll();
        PrintCopiesTextBox.Focus();
    }

    private void PrintCopiesTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private void PrintCopiesTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ReadCopyCount();

    private void PrintCopiesTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text) ||
            e.DataObject.GetData(System.Windows.DataFormats.Text) is not string text ||
            text.Length == 0 || text.Any(character => !char.IsDigit(character)))
        {
            e.CancelCommand();
        }
    }

    private static string DescribePrintOptions(PrintOptions options)
    {
        var size = options.ReductionPreset.StartsWith("None", StringComparison.OrdinalIgnoreCase)
            ? options.PaperSize
            : options.ReductionPreset;
        var color = options.ColorMode switch
        {
            PrintColorMode.Grayscale => "grayscale",
            PrintColorMode.BlackAndWhite => "black & white",
            _ => "color"
        };
        var copies = options.Copies == 1 ? "1 copy" : $"{options.Copies} copies";
        return $"{size}, {color}, {options.Quality.ToString().ToLowerInvariant()} quality, {copies}";
    }

    private int ReadDpi()
    {
        var selected = new[] { Dpi150, Dpi200, Dpi300, Dpi600 }.First(button => button.IsChecked == true);
        return int.Parse(Convert.ToString(selected.Tag) ?? "300");
    }

    private PdfCompression ReadCompression() => SelectedText(CompressionCombo) switch
    {
        "Smallest file" => PdfCompression.Smallest,
        "High quality" => PdfCompression.HighQuality,
        _ => PdfCompression.Balanced
    };

    private long ReadJpegQuality() => ReadCompression() switch
    {
        PdfCompression.Smallest => 45L,
        PdfCompression.HighQuality => 92L,
        _ => 72L
    };

    private static string SelectedText(System.Windows.Controls.ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem item
            ? Convert.ToString(item.Content) ?? string.Empty
            : Convert.ToString(comboBox.SelectedValue) ?? string.Empty;

    private static void SelectComboText(System.Windows.Controls.ComboBox comboBox, string value)
    {
        foreach (var entry in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(Convert.ToString(entry.Content), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = entry;
                return;
            }
        }
    }

    private void RenumberPages()
    {
        for (var index = 0; index < Pages.Count; index++)
        {
            Pages[index].DisplayNumber = index + 1;
        }
    }

    private void UpdateDocumentState()
    {
        var hasPages = Pages.Count > 0;
        DocumentPageCountText.Text = $"Pages: {Pages.Count}";
        FinishButton.IsEnabled = hasPages && !_isBusy;
        SaveButton.IsEnabled = hasPages && !_isBusy;
        PrintButton.IsEnabled = !_isBusy &&
                                ScannerCombo.SelectedItem is ScannerDevice &&
                                PrinterCombo.SelectedItem is PrinterDevice;
        ScanButton.Content = ContinuousScanCheck.IsChecked == true && hasPages ? "Scan Next Page" : "Scan";
        FinishButton.Content = "Finish & Combine PDF";
        SaveButton.Content = "Save PDFs as ZIP";
        PrintButton.Content = "Print Now";
        EstimatedSizeText.Text = $"PDF: {EstimateOutputSize()}";
        ShowSelectedPage();
    }

    private string EstimateOutputSize()
    {
        long sourceBytes = 0;
        foreach (var page in Pages)
        {
            try
            {
                sourceBytes += new FileInfo(page.FilePath).Length;
            }
            catch
            {
                // A page may be in the short interval between scanning and processing.
            }
        }

        var factor = ReadCompression() switch
        {
            PdfCompression.Smallest => 0.55,
            PdfCompression.HighQuality => 1.05,
            _ => 0.75
        };
        return FormatSize((long)Math.Max(0, sourceBytes * factor));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / 1024d / 1024d:0.##} MB";
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        if (busy)
        {
            SettingsPopup.IsOpen = false;
        }
        ScanButton.IsEnabled = !busy && ScannerCombo.SelectedItem is ScannerDevice;
        FinishButton.IsEnabled = !busy && Pages.Count > 0;
        SaveButton.IsEnabled = !busy && Pages.Count > 0;
        PrintButton.IsEnabled = !busy &&
                                ScannerCombo.SelectedItem is ScannerDevice &&
                                PrinterCombo.SelectedItem is PrinterDevice;
        if (message is not null)
        {
            SetStatus(message, StatusKind.Working);
        }
    }

    private void UpdateScannerAcquisitionProgress(int percentage)
    {
        if (_scanCancellation?.IsCancellationRequested == true)
        {
            return;
        }

        var detail = percentage switch
        {
            < 25 => "Finding the selected scanner…",
            < 40 => "Applying scan settings…",
            < 70 => "Scanning the page…",
            _ => "Receiving the scanned image…"
        };
        SetScanProgress(true, percentage, detail);
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        cancellation.Cancel();
        CancelScanButton.IsEnabled = false;
        CancelScanButton.Content = "Cancelling…";
        ScanProgressDetailText.Text = "Cancelling the current scan…";
        SetStatus("Cancelling scan…", StatusKind.Warning);
    }

    private void SetScanProgress(bool visible, int percentage = 0, string? detail = null)
    {
        var wasVisible = ScanProgressOverlay.Visibility == Visibility.Visible;
        ScanProgressOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            ScanProgressBar.Value = 0;
            ScanProgressPercentText.Text = "0%";
            CancelScanButton.IsEnabled = false;
            CancelScanButton.Content = "Cancel Scan";
            return;
        }

        var requestedValue = Math.Clamp(percentage, 0, 100);
        var value = wasVisible ? Math.Max(ScanProgressBar.Value, requestedValue) : requestedValue;
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Value = value;
        ScanProgressPercentText.Text = $"{value}%";
        CancelScanButton.IsEnabled = value < 100 && _scanCancellation is { IsCancellationRequested: false };
        if (!wasVisible)
        {
            CancelScanButton.Content = "Cancel Scan";
        }
        if (!string.IsNullOrWhiteSpace(detail) && (!wasVisible || requestedValue >= ScanProgressBar.Value))
        {
            ScanProgressDetailText.Text = detail;
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush(kind switch
        {
            StatusKind.Success => System.Windows.Media.Color.FromRgb(20, 164, 77),
            StatusKind.Warning => System.Windows.Media.Color.FromRgb(245, 158, 11),
            StatusKind.Error => System.Windows.Media.Color.FromRgb(220, 53, 69),
            _ => System.Windows.Media.Color.FromRgb(13, 110, 253)
        });
    }

    private void ShowError(string message)
    {
        System.Windows.MessageBox.Show(this, message, "RC Printer & Scanner", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void ScannerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScanButton.IsEnabled = !_isBusy && ScannerCombo.SelectedItem is ScannerDevice;
        PrintButton.IsEnabled = !_isBusy &&
                                ScannerCombo.SelectedItem is ScannerDevice &&
                                PrinterCombo.SelectedItem is PrinterDevice;

        var request = ++_scanCapabilityRequest;
        HideLargeScanSizes();
        if (ScannerCombo.SelectedItem is not ScannerDevice scanner)
        {
            return;
        }

        var support = await _scannerService.GetLargePaperSupportAsync(scanner);
        if (request != _scanCapabilityRequest ||
            ScannerCombo.SelectedItem is not ScannerDevice selectedScanner ||
            !string.Equals(selectedScanner.Id, scanner.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        A3ScanSizeItem.Visibility = support.SupportsA3 ? Visibility.Visible : Visibility.Collapsed;
        LegalScanSizeItem.Visibility = support.SupportsLegal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideLargeScanSizes()
    {
        if (PaperSizeCombo is null || A3ScanSizeItem is null || LegalScanSizeItem is null)
        {
            return;
        }

        A3ScanSizeItem.Visibility = Visibility.Collapsed;
        LegalScanSizeItem.Visibility = Visibility.Collapsed;
        if (SelectedText(PaperSizeCombo) is "A3" or "Legal")
        {
            SelectComboText(PaperSizeCombo, "A4");
        }
    }

    private void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PrintButton.IsEnabled = !_isBusy &&
                                ScannerCombo.SelectedItem is ScannerDevice &&
                                PrinterCombo.SelectedItem is PrinterDevice;
        UpdatePrintPaperSizes();
    }

    private void UpdatePrintPaperSizes()
    {
        if (PrintPaperSizeCombo is null || A3PrintSizeItem is null || LegalPrintSizeItem is null)
        {
            return;
        }

        A3PrintSizeItem.Visibility = Visibility.Collapsed;
        LegalPrintSizeItem.Visibility = Visibility.Collapsed;
        if (SelectedText(PrintPaperSizeCombo) is "A3" or "Legal")
        {
            SelectComboText(PrintPaperSizeCombo, "A4");
        }

        if (PrinterCombo.SelectedItem is not PrinterDevice printer)
        {
            return;
        }

        try
        {
            A3PrintSizeItem.Visibility = _printerService.SupportsPaperSize(printer, "A3")
                ? Visibility.Visible
                : Visibility.Collapsed;
            LegalPrintSizeItem.Visibility = _printerService.SupportsPaperSize(printer, "Legal")
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            // A legacy driver that cannot report capabilities will validate the
            // selected paper size when the print job is created.
            A3PrintSizeItem.Visibility = Visibility.Visible;
            LegalPrintSizeItem.Visibility = Visibility.Visible;
        }
    }

    private void PrintReductionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaperSizeCombo is null || PrintPaperSizeCombo is null || PrintReductionCombo is null)
        {
            return;
        }

        var preset = SelectedText(PrintReductionCombo);
        var separator = preset.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (separator < 0)
        {
            return;
        }

        var sourceSize = preset[..separator];
        var targetSize = preset[(separator + 4)..];
        if (!IsScanSizeAvailable(sourceSize) || !IsPrintSizeAvailable(targetSize))
        {
            SelectComboText(PrintReductionCombo, "None (fit to selected paper)");
            if (StatusText is not null)
            {
                SetStatus("That reduction is not available for the selected devices", StatusKind.Warning);
            }
            return;
        }

        SelectComboText(PaperSizeCombo, sourceSize);
        SelectComboText(PrintPaperSizeCombo, targetSize);
        if (StatusText is not null)
        {
            SetStatus($"Reduction set: scan {sourceSize}, print {targetSize}", StatusKind.Success);
        }
    }

    private bool IsScanSizeAvailable(string paperSize) => paperSize switch
    {
        "A3" => A3ScanSizeItem.Visibility == Visibility.Visible,
        "Legal" => LegalScanSizeItem.Visibility == Visibility.Visible,
        _ => true
    };

    private bool IsPrintSizeAvailable(string paperSize) => paperSize switch
    {
        "A3" => A3PrintSizeItem.Visibility == Visibility.Visible,
        "Legal" => LegalPrintSizeItem.Visibility == Visibility.Visible,
        _ => true
    };

    private void CompressionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EstimatedSizeText is not null)
        {
            UpdateDocumentState();
        }
    }

    private void BrowseSaveLocationButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where scanned files will be saved",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(SaveLocationText.Text)
                ? SaveLocationText.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
            !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            RememberSaveFolder(dialog.SelectedPath);
            SetStatus("Save location updated", StatusKind.Success);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _preferences.LastScannerId = (ScannerCombo.SelectedItem as ScannerDevice)?.Id;
        _preferences.LastPrinterName = (PrinterCombo.SelectedItem as PrinterDevice)?.Name;
        _preferences.SaveFolder = SaveLocationText.Text;
        try
        {
            _settingsService.Save(_preferences);
        }
        catch
        {
            // Closing should not be blocked if preferences cannot be saved.
        }

        PreviewImage.Source = null;
        _tempSession.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Session cleanup will retry when the app closes.
        }
    }

    private string GetSaveFolder()
    {
        var selected = SaveLocationText.Text;
        return string.IsNullOrWhiteSpace(selected)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : selected;
    }

    private static string GetInitialSaveFolder(string? savedFolder)
    {
        if (!string.IsNullOrWhiteSpace(savedFolder) && Directory.Exists(savedFolder))
        {
            return savedFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private void RememberSaveFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        SaveLocationText.Text = folder;
        _preferences.SaveFolder = folder;
    }

    private enum StatusKind
    {
        Working,
        Success,
        Warning,
        Error
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
