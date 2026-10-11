using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Views
{
    public partial class TicketPrintingWindow : Window
    {
        private const double MillimetersPerInch = 25.4;
        private const double PreviewUnitsPerInch = MillimetersPerInch * 2.0;
        private const double MinimumPreviewZoom = 0.25;
        private const double MaximumPreviewZoom = 4.0;
        private const double PreviewZoomStep = 0.10;
        private string _imagePath;
        private PaperDefinition _customPaper;
        private double _ticketWidthInches = 5.5;
        private double _ticketHeightInches = 2.0;
        private int _numberingBoxCount = 0;
        private IList<TicketNumberBoxSettings> _numberingBoxes = new List<TicketNumberBoxSettings>();
        private ImageSource _previewImageSource;
        private int _previewCopyCount = 1;
        private int _currentPageIndex;
        private double _previewZoom = 1.0;
        private bool _updatingNumberRangeControls;

        public TicketPrintingWindow()
        {
            InitializeComponent();
            WindowSizingService.Maximize(this);
            PaperOrientationBox.ItemsSource = new[] { "Portrait", "Landscape" };
            PaperOrientationBox.SelectedIndex = 0;
            var papers = PaperDefinition.StandardPapers().ToList();
            papers.Add(new PaperDefinition
            {
                Name = "Long (8.5 × 13 in)",
                WidthMm = 8.5 * MillimetersPerInch,
                HeightMm = 13 * MillimetersPerInch
            });
            _customPaper = new PaperDefinition
            {
                Name = "Custom size",
                WidthMm = 8.5 * MillimetersPerInch,
                HeightMm = 11 * MillimetersPerInch
            };
            papers.Add(_customPaper);
            PaperSizeBox.ItemsSource = papers;
            PaperSizeBox.SelectedItem = papers.FirstOrDefault(paper => paper.Name == "A4") ?? papers[0];
        }

        private void PaperSize_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper == null) return;

            CustomPaperPanel.Visibility = ReferenceEquals(paper, _customPaper)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyPaperPreview(paper);
        }

        private void PaperOrientation_Changed(object sender, SelectionChangedEventArgs e)
        {
            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper != null) ApplyPaperPreview(paper);
        }

        private void ApplyCustomPaperSize_Click(object sender, RoutedEventArgs e)
        {
            double widthInches;
            double heightInches;
            if (!TryReadDimension(CustomPaperWidthBox.Text, out widthInches) ||
                !TryReadDimension(CustomPaperHeightBox.Text, out heightInches))
            {
                MessageBox.Show(this, "Enter a valid width and height greater than zero.",
                    "Custom Paper Size", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _customPaper.WidthMm = widthInches * MillimetersPerInch;
            _customPaper.HeightMm = heightInches * MillimetersPerInch;
            ApplyPaperPreview(_customPaper);
        }

        private static bool TryReadDimension(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                   value > 0 && value <= 100;
        }

        private void ApplyPaperPreview(PaperDefinition paper)
        {
            double paperWidthMm;
            double paperHeightMm;
            GetOrientedPaperSize(paper, out paperWidthMm, out paperHeightMm);
            PreviewPaper.Width = paperWidthMm * 2;
            PreviewPaper.Height = paperHeightMm * 2;
            DocumentPreviewCanvas.Width = PreviewPaper.Width;
            DocumentPreviewCanvas.Height = PreviewPaper.Height;
            PaperSizeStatusText.Text = string.Format("{0:0.#} × {1:0.#} mm  ({2:0.###} × {3:0.###} in)",
                paperWidthMm, paperHeightMm,
                paperWidthMm / MillimetersPerInch, paperHeightMm / MillimetersPerInch);
            UpdateDuplicateControls();
            if (_previewImageSource != null) UpdateTicketPreview();
        }

        private void GetOrientedPaperSize(PaperDefinition paper,
            out double widthMm, out double heightMm)
        {
            var shortSide = Math.Min(paper.WidthMm, paper.HeightMm);
            var longSide = Math.Max(paper.WidthMm, paper.HeightMm);
            var landscape = PaperOrientationBox != null && PaperOrientationBox.SelectedIndex == 1;
            widthMm = landscape ? longSide : shortSide;
            heightMm = landscape ? shortSide : longSide;
        }

        private void BrowseImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Browse ticket image",
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            var preview = ImageLoadingService.Load(dialog.FileName, 2400);
            if (preview == null)
            {
                MessageBox.Show(this, "The selected image could not be opened.", "Ticket Printing",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedImagePath = dialog.FileName;
            var proposedWidthInches = _ticketWidthInches;
            var proposedHeightInches = _ticketHeightInches;
            double naturalWidthInches;
            double naturalHeightInches;
            if (TryGetNaturalImageSize(selectedImagePath, out naturalWidthInches, out naturalHeightInches))
            {
                proposedWidthInches = naturalWidthInches;
                proposedHeightInches = naturalHeightInches;
            }

            var setup = new TicketImageSetupWindow(selectedImagePath, proposedWidthInches,
                proposedHeightInches, _numberingBoxCount, _numberingBoxes) { Owner = this };
            if (setup.ShowDialog() == true)
                ApplySetupResult(setup, selectedImagePath, preview);
        }

        private void EditAgain_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_imagePath) || !File.Exists(_imagePath) ||
                _previewImageSource == null)
                return;

            var setup = new TicketImageSetupWindow(_imagePath, _ticketWidthInches,
                _ticketHeightInches, _numberingBoxCount, _numberingBoxes, true) { Owner = this };
            if (setup.ShowDialog() == true)
                ApplySetupResult(setup, _imagePath, _previewImageSource);
        }

        private void ApplySetupResult(TicketImageSetupWindow setup, string imagePath,
            ImageSource preview)
        {
            _imagePath = imagePath;
            _ticketWidthInches = setup.TicketWidthInches;
            _ticketHeightInches = setup.TicketHeightInches;
            _numberingBoxCount = setup.NumberingBoxCount;
            _numberingBoxes = setup.NumberingBoxes;
            _previewImageSource = preview;
            DocumentPreviewHint.Visibility = Visibility.Collapsed;
            DocumentStatusText.ToolTip = _imagePath;
            SavePdfButton.IsEnabled = true;
            EditAgainButton.IsEnabled = true;
            UpdateDuplicateControls();
            UpdateTicketPreview();
            DocumentStatusText.Text = string.Format("{0}\n{1:0.###} × {2:0.###} in",
                Path.GetFileName(_imagePath), _ticketWidthInches, _ticketHeightInches);
        }

        private static bool TryGetNaturalImageSize(string imagePath,
            out double widthInches, out double heightInches)
        {
            widthInches = 0;
            heightInches = 0;
            try
            {
                using (var image = System.Drawing.Image.FromFile(imagePath))
                {
                    var pixelWidth = (double)image.Width;
                    var pixelHeight = (double)image.Height;
                    var dpiX = image.HorizontalResolution;
                    var dpiY = image.VerticalResolution;
                    if (dpiX < 10 || dpiX > 2400) dpiX = 96;
                    if (dpiY < 10 || dpiY > 2400) dpiY = 96;

                    try
                    {
                        var orientation = image.GetPropertyItem(0x0112);
                        if (orientation != null && orientation.Value != null && orientation.Value.Length >= 2)
                        {
                            var value = BitConverter.ToUInt16(orientation.Value, 0);
                            if (value >= 5 && value <= 8)
                            {
                                var pixels = pixelWidth;
                                pixelWidth = pixelHeight;
                                pixelHeight = pixels;
                                var dpi = dpiX;
                                dpiX = dpiY;
                                dpiY = dpi;
                            }
                        }
                    }
                    catch (ArgumentException) { }

                    widthInches = pixelWidth / dpiX;
                    heightInches = pixelHeight / dpiY;
                    if (widthInches <= 0 || heightInches <= 0 ||
                        double.IsNaN(widthInches) || double.IsNaN(heightInches) ||
                        double.IsInfinity(widthInches) || double.IsInfinity(heightInches))
                        return false;

                    var largestDimension = Math.Max(widthInches, heightInches);
                    if (largestDimension > 100)
                    {
                        var scale = 100 / largestDimension;
                        widthInches *= scale;
                        heightInches *= scale;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void UpdateTicketPreview()
        {
            DocumentPreviewCanvas.Children.Clear();
            if (_previewImageSource == null)
            {
                UpdatePageNavigation(1);
                return;
            }

            var pageCount = GetPageCount();
            _currentPageIndex = Math.Max(0, Math.Min(_currentPageIndex, pageCount - 1));
            RenderPageIntoCanvas(DocumentPreviewCanvas, _currentPageIndex);
            UpdatePageNavigation(pageCount);
        }

        private void RenderPageIntoCanvas(Canvas targetCanvas, int pageIndex)
        {
            targetCanvas.Children.Clear();
            if (_previewImageSource == null) return;

            var ticketWidth = _ticketWidthInches * PreviewUnitsPerInch;
            var ticketHeight = _ticketHeightInches * PreviewUnitsPerInch;
            var columns = GetCopiesPerRow();
            var copiesPerPage = GetCopyCapacity();
            var firstCopyIndex = pageIndex * copiesPerPage;
            var copiesOnPage = Math.Max(0,
                Math.Min(copiesPerPage, _previewCopyCount - firstCopyIndex));
            var baseNumber = GetBaseNumberText();

            for (var localIndex = 0; localIndex < copiesOnPage; localIndex++)
            {
                var copyIndex = firstCopyIndex + localIndex;
                var ticket = CreateTicketPreviewVisual(
                    IncrementNumericText(baseNumber, copyIndex), ticketWidth, ticketHeight);
                Canvas.SetLeft(ticket, (localIndex % columns) * ticketWidth);
                Canvas.SetTop(ticket, (localIndex / columns) * ticketHeight);
                targetCanvas.Children.Add(ticket);
            }
        }

        private Grid CreateTicketPreviewVisual(string numberText, double ticketWidth, double ticketHeight)
        {
            var ticket = new Grid
            {
                Width = ticketWidth,
                Height = ticketHeight,
                ClipToBounds = true
            };
            ticket.Children.Add(new Image
            {
                Source = _previewImageSource,
                Stretch = Stretch.Fill
            });

            var numberingCanvas = new Canvas
            {
                Width = ticketWidth,
                Height = ticketHeight,
                Background = Brushes.Transparent,
                ClipToBounds = true
            };
            foreach (var box in _numberingBoxes)
            {
                var border = new Border
                {
                    Width = box.WidthInches * PreviewUnitsPerInch,
                    Height = box.HeightInches * PreviewUnitsPerInch,
                    Background = box.IsBackgroundTransparent
                        ? Brushes.Transparent : GetBrush(box.BackgroundColor, Colors.White),
                    BorderBrush = box.IsBorderTransparent
                        ? Brushes.Transparent : GetBrush(box.BorderColor, Colors.Black),
                    BorderThickness = new Thickness(box.IsBorderTransparent
                        ? 0
                        : (box.BorderThicknessPixels > 0 ? box.BorderThicknessPixels : 1) *
                          PreviewUnitsPerInch / 96.0),
                    ClipToBounds = true,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(box.RotationDegrees),
                    Child = new TextBlock
                    {
                        Text = numberText,
                        Foreground = GetBrush(box.TextColor, Colors.Black),
                        FontFamily = GetFontFamily(box.FontFamilyName),
                        FontSize = box.FontSizePoints * PreviewUnitsPerInch / 72.0,
                        FontWeight = FontWeights.SemiBold,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.NoWrap,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center,
                        ClipToBounds = true
                    }
                };
                Canvas.SetLeft(border, box.XInches * PreviewUnitsPerInch);
                Canvas.SetTop(border, box.YInches * PreviewUnitsPerInch);
                numberingCanvas.Children.Add(border);
            }
            ticket.Children.Add(numberingCanvas);
            return ticket;
        }

        private void UpdateDuplicateControls()
        {
            var hasImage = _previewImageSource != null;
            var maximumCopies = GetMaximumCopiesForNumberWidth();
            _previewCopyCount = Math.Max(1, Math.Min(_previewCopyCount, maximumCopies));
            NumberRangePanel.IsEnabled = hasImage && _numberingBoxes.Count > 0;
            var baseNumber = GetBaseNumberText();
            UpdateNumberRangeControls(baseNumber);
        }

        private void NumberRange_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = string.IsNullOrEmpty(e.Text) ||
                        e.Text.Any(character => character < '0' || character > '9');
        }

        private void NumberRange_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text, true))
            {
                e.CancelCommand();
                return;
            }

            var text = e.DataObject.GetData(DataFormats.Text) as string;
            if (string.IsNullOrEmpty(text) ||
                text.Any(character => character < '0' || character > '9'))
                e.CancelCommand();
        }

        private void NumberRange_Changed(object sender, TextChangedEventArgs e)
        {
            if (_updatingNumberRangeControls || _previewImageSource == null ||
                _numberingBoxes.Count == 0 || StartNumberBox == null || EndNumberBox == null)
                return;

            var startNumber = (StartNumberBox.Text ?? "").Trim();
            var endNumber = (EndNumberBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(startNumber) || string.IsNullOrEmpty(endNumber) ||
                startNumber.Any(character => character < '0' || character > '9') ||
                endNumber.Any(character => character < '0' || character > '9') ||
                startNumber.Length != endNumber.Length)
                return;

            var copyCount = GetCopyCountForRange(startNumber, endNumber);
            if (copyCount == 0) return;

            foreach (var box in _numberingBoxes)
                box.TextValue = startNumber;
            _previewCopyCount = copyCount;
            _currentPageIndex = 0;
            UpdateDuplicateControls();
            UpdateTicketPreview();
        }

        private static int GetCopyCountForRange(string startNumber, string endNumber)
        {
            if (string.IsNullOrEmpty(startNumber) || string.IsNullOrEmpty(endNumber) ||
                startNumber.Length != endNumber.Length ||
                string.CompareOrdinal(startNumber, endNumber) > 0)
                return 0;

            var differenceDigits = new int[startNumber.Length];
            var borrow = 0;
            for (var index = startNumber.Length - 1; index >= 0; index--)
            {
                var difference = (endNumber[index] - '0') -
                                 (startNumber[index] - '0') - borrow;
                if (difference < 0)
                {
                    difference += 10;
                    borrow = 1;
                }
                else
                {
                    borrow = 0;
                }
                differenceDigits[index] = difference;
            }

            var maximumDifference = int.MaxValue - 1;
            var totalDifference = 0;
            foreach (var digit in differenceDigits)
            {
                if (totalDifference > (maximumDifference - digit) / 10)
                    return int.MaxValue;
                totalDifference = totalDifference * 10 + digit;
            }
            return totalDifference + 1;
        }

        private void UpdateNumberRangeControls(string baseNumber)
        {
            if (StartNumberBox == null || EndNumberBox == null) return;
            _updatingNumberRangeControls = true;
            try
            {
                StartNumberBox.Text = baseNumber;
                EndNumberBox.Text = string.IsNullOrEmpty(baseNumber)
                    ? ""
                    : IncrementNumericText(baseNumber, _previewCopyCount - 1);
            }
            finally
            {
                _updatingNumberRangeControls = false;
            }
        }

        private string GetBaseNumberText()
        {
            return _numberingBoxes
                .Select(box => box.TextValue)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
        }

        private int GetMaximumCopiesForNumberWidth()
        {
            var baseNumber = GetBaseNumberText();
            if (string.IsNullOrEmpty(baseNumber) ||
                baseNumber.Any(character => character < '0' || character > '9'))
                return int.MaxValue;
            return GetCopyCountForRange(baseNumber, new string('9', baseNumber.Length));
        }

        private int GetCopiesPerRow()
        {
            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper == null || _ticketWidthInches <= 0) return 1;
            double paperWidthMm;
            double paperHeightMm;
            GetOrientedPaperSize(paper, out paperWidthMm, out paperHeightMm);
            return Math.Max(1, (int)Math.Floor(
                paperWidthMm / (_ticketWidthInches * MillimetersPerInch) + 0.000001));
        }

        private int GetCopyCapacity()
        {
            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper == null || _ticketWidthInches <= 0 || _ticketHeightInches <= 0) return 1;
            double paperWidthMm;
            double paperHeightMm;
            GetOrientedPaperSize(paper, out paperWidthMm, out paperHeightMm);
            var columns = GetCopiesPerRow();
            var rows = Math.Max(1, (int)Math.Floor(
                paperHeightMm / (_ticketHeightInches * MillimetersPerInch) + 0.000001));
            return Math.Max(1, Math.Min(100, columns * rows));
        }

        private int GetPageCount()
        {
            return Math.Max(1, (int)Math.Ceiling(
                _previewCopyCount / (double)GetCopyCapacity()));
        }

        private void UpdatePageNavigation(int pageCount)
        {
            var pageText = string.Format(CultureInfo.CurrentCulture,
                "Page {0} of {1}", _currentPageIndex + 1, pageCount);
            SidebarPageStatusText.Text = pageText;
            FooterPageStatusText.Text = pageText;
            PageThumbnailPanel.Visibility = pageCount > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            PageThumbnailList.Children.Clear();
            if (pageCount <= 1 || _previewImageSource == null) return;

            var visiblePageIndexes = new SortedSet<int>();
            if (pageCount <= 15)
            {
                for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
                    visiblePageIndexes.Add(pageIndex);
            }
            else
            {
                visiblePageIndexes.Add(0);
                visiblePageIndexes.Add(pageCount - 1);
                var lastVisiblePage = (int)Math.Min(pageCount - 1L, _currentPageIndex + 6L);
                for (var pageIndex = Math.Max(0, _currentPageIndex - 6);
                     pageIndex <= lastVisiblePage;
                     pageIndex++)
                    visiblePageIndexes.Add(pageIndex);
            }

            foreach (var pageIndex in visiblePageIndexes)
                PageThumbnailList.Children.Add(CreatePageThumbnail(pageIndex));
        }

        private Button CreatePageThumbnail(int pageIndex)
        {
            var paperCanvas = new Canvas
            {
                Width = PreviewPaper.Width,
                Height = PreviewPaper.Height,
                Background = Brushes.White,
                ClipToBounds = true
            };
            RenderPageIntoCanvas(paperCanvas, pageIndex);

            var preview = new Viewbox
            {
                Width = 92,
                Height = 112,
                Stretch = Stretch.Uniform,
                Child = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(180, 185, 194)),
                    BorderThickness = new Thickness(1),
                    Child = paperCanvas
                }
            };
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(preview);
            content.Children.Add(new TextBlock
            {
                Text = "Page " + (pageIndex + 1).ToString(CultureInfo.CurrentCulture),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var button = new Button
            {
                Tag = pageIndex,
                Content = content,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(5),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderBrush = pageIndex == _currentPageIndex
                    ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                    : new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                BorderThickness = new Thickness(pageIndex == _currentPageIndex ? 2 : 1)
            };
            button.Click += PageThumbnail_Click;
            return button;
        }

        private void PageThumbnail_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || !(button.Tag is int)) return;
            _currentPageIndex = (int)button.Tag;
            UpdateTicketPreview();
        }

        private static string IncrementNumericText(string value, int increment)
        {
            if (string.IsNullOrEmpty(value) || increment <= 0) return value ?? "";
            var digits = value.ToCharArray();
            long carry = increment;
            for (var index = digits.Length - 1; index >= 0 && carry > 0; index--)
            {
                var sum = digits[index] - '0' + carry;
                digits[index] = (char)('0' + (int)(sum % 10));
                carry = sum / 10;
            }
            return carry > 0
                ? carry.ToString(CultureInfo.InvariantCulture) + new string(digits)
                : new string(digits);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            SetPreviewZoom(_previewZoom - PreviewZoomStep);
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            SetPreviewZoom(_previewZoom + PreviewZoomStep);
        }

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            SetPreviewZoom(_previewZoom + (e.Delta > 0 ? PreviewZoomStep : -PreviewZoomStep));
            e.Handled = true;
        }

        private void SetPreviewZoom(double zoom)
        {
            _previewZoom = Math.Max(MinimumPreviewZoom, Math.Min(MaximumPreviewZoom, zoom));
            _previewZoom = Math.Round(_previewZoom, 2);
            PreviewZoomTransform.ScaleX = _previewZoom;
            PreviewZoomTransform.ScaleY = _previewZoom;
            ZoomPercentText.Text = string.Format(CultureInfo.CurrentCulture,
                "{0:0}%", _previewZoom * 100);
        }

        private static SolidColorBrush GetBrush(string value, Color fallback)
        {
            try
            {
                var converted = ColorConverter.ConvertFromString(value);
                if (converted is Color) return new SolidColorBrush((Color)converted);
            }
            catch { }
            return new SolidColorBrush(fallback);
        }

        private static FontFamily GetFontFamily(string name)
        {
            try { return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name); }
            catch { return new FontFamily("Segoe UI"); }
        }

        private void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_imagePath) || !File.Exists(_imagePath))
            {
                MessageBox.Show(this, "Browse an image before saving the preview.", "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper == null) return;
            double paperWidthMm;
            double paperHeightMm;
            GetOrientedPaperSize(paper, out paperWidthMm, out paperHeightMm);

            var dialog = new SaveFileDialog
            {
                Title = "Save Ticket Preview as PDF",
                Filter = "PDF document (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = Path.GetFileNameWithoutExtension(_imagePath) + ".pdf"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                PdfExportService.SaveImagePreview(dialog.FileName, _imagePath,
                    paperWidthMm, paperHeightMm,
                    _ticketWidthInches * MillimetersPerInch,
                    _ticketHeightInches * MillimetersPerInch,
                    _numberingBoxes, _previewCopyCount);
                MessageBox.Show(this, "PDF saved successfully.", "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the PDF.\n\n" + ex.Message, "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
