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
        private const int MaximumImageCopies = 9999;
        private const double PrintedTicketBorderWidthPixels = 0.10;
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
        private double _sideMarginInches;
        private double _topMarginInches;
        private double _imageGapInches;
        private bool _autoRotateToFill = true;
        private bool _printTicketBorders;
        private bool _updatingNumberRangeControls;

        private sealed class TicketPageSlot
        {
            public double XInches { get; set; }
            public double YInches { get; set; }
            public bool IsRotated { get; set; }
        }

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

        private static bool TryReadSpacing(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
                   value >= 0 && value <= 100;
        }

        private void TicketLayout_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || SideMarginBox == null || TopMarginBox == null ||
                ImageGapBox == null ||
                LayoutStatusText == null)
                return;

            double sideMarginInches;
            double topMarginInches;
            double imageGapInches;
            if (!TryReadSpacing(SideMarginBox.Text, out sideMarginInches) ||
                !TryReadSpacing(TopMarginBox.Text, out topMarginInches) ||
                !TryReadSpacing(ImageGapBox.Text, out imageGapInches))
            {
                LayoutStatusText.Text = "Enter values from 0 to 100 inches.";
                return;
            }

            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper != null)
            {
                double paperWidthMm;
                double paperHeightMm;
                GetOrientedPaperSize(paper, out paperWidthMm, out paperHeightMm);
                var availableWidthInches = paperWidthMm / MillimetersPerInch - sideMarginInches * 2;
                var availableHeightInches = paperHeightMm / MillimetersPerInch - topMarginInches;
                if (availableWidthInches + 0.000001 < _ticketWidthInches)
                {
                    LayoutStatusText.Text = "Side margins leave too little width for the ticket.";
                    return;
                }
                if (availableHeightInches + 0.000001 < _ticketHeightInches)
                {
                    LayoutStatusText.Text = "Top margin leaves too little height for the ticket.";
                    return;
                }
            }

            LayoutStatusText.Text = "Preview updated automatically.";
            _sideMarginInches = sideMarginInches;
            _topMarginInches = topMarginInches;
            _imageGapInches = imageGapInches;
            _currentPageIndex = 0;
            UpdateDuplicateControls();
            UpdateTicketPreview();
        }

        private void AutoRotateToFill_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || AutoRotateToFillBox == null) return;
            _autoRotateToFill = AutoRotateToFillBox.IsChecked == true;
            _currentPageIndex = 0;
            UpdateDuplicateControls();
            UpdateTicketPreview();
        }

        private void PrintTicketBorders_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || PrintTicketBordersBox == null) return;
            _printTicketBorders = PrintTicketBordersBox.IsChecked == true;
            UpdateTicketPreview();
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
            var slots = GetTicketPageSlots();
            var copiesPerPage = Math.Max(1, slots.Count);
            var firstCopyIndex = pageIndex * copiesPerPage;
            var copiesOnPage = Math.Max(0,
                Math.Min(copiesPerPage, _previewCopyCount - firstCopyIndex));
            var baseNumber = GetBaseNumberText();

            for (var localIndex = 0; localIndex < copiesOnPage; localIndex++)
            {
                var copyIndex = firstCopyIndex + localIndex;
                var ticket = CreateTicketPreviewVisual(
                    IncrementNumericText(baseNumber, copyIndex), ticketWidth, ticketHeight);
                var slot = slots[localIndex];
                if (slot.IsRotated)
                {
                    ticket.RenderTransform = new RotateTransform(90);
                    Canvas.SetLeft(ticket,
                        slot.XInches * PreviewUnitsPerInch + ticketHeight);
                }
                else
                {
                    Canvas.SetLeft(ticket, slot.XInches * PreviewUnitsPerInch);
                }
                Canvas.SetTop(ticket, slot.YInches * PreviewUnitsPerInch);
                targetCanvas.Children.Add(ticket);
            }
        }

        private IList<TicketPageSlot> GetTicketPageSlots()
        {
            var slots = new List<TicketPageSlot>();
            var paper = PaperSizeBox.SelectedItem as PaperDefinition;
            if (paper == null || _ticketWidthInches <= 0 || _ticketHeightInches <= 0)
                return slots;

            double paperWidthMm;
            double paperHeightMm;
            GetOrientedPaperSize(paper, out paperWidthMm, out paperHeightMm);
            var availableWidth = Math.Max(0,
                paperWidthMm / MillimetersPerInch - _sideMarginInches * 2);
            var availableHeight = Math.Max(0,
                paperHeightMm / MillimetersPerInch - _topMarginInches);
            var columns = GetFitCount(availableWidth, _ticketWidthInches, _imageGapInches);
            var rows = GetFitCount(availableHeight, _ticketHeightInches, _imageGapInches);
            if (columns == 0 || rows == 0)
            {
                slots.Add(new TicketPageSlot
                {
                    XInches = _sideMarginInches,
                    YInches = _topMarginInches
                });
                return slots;
            }

            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                    slots.Add(new TicketPageSlot
                    {
                        XInches = _sideMarginInches +
                            column * (_ticketWidthInches + _imageGapInches),
                        YInches = _topMarginInches +
                            row * (_ticketHeightInches + _imageGapInches)
                    });

            if (!_autoRotateToFill ||
                Math.Abs(_ticketWidthInches - _ticketHeightInches) < 0.000001)
                return slots;

            var usedWidth = columns * _ticketWidthInches +
                            Math.Max(0, columns - 1) * _imageGapInches;
            var usedHeight = rows * _ticketHeightInches +
                             Math.Max(0, rows - 1) * _imageGapInches;
            var rotatedWidth = _ticketHeightInches;
            var rotatedHeight = _ticketWidthInches;

            var bottomY = _topMarginInches + usedHeight + _imageGapInches;
            var bottomHeight = availableHeight - usedHeight - _imageGapInches;
            var bottomColumns = GetFitCount(availableWidth, rotatedWidth, _imageGapInches);
            var bottomRows = GetFitCount(bottomHeight, rotatedHeight, _imageGapInches);
            for (var row = 0; row < bottomRows; row++)
                for (var column = 0; column < bottomColumns; column++)
                    slots.Add(new TicketPageSlot
                    {
                        XInches = _sideMarginInches +
                            column * (rotatedWidth + _imageGapInches),
                        YInches = bottomY + row * (rotatedHeight + _imageGapInches),
                        IsRotated = true
                    });

            var rightX = _sideMarginInches + usedWidth + _imageGapInches;
            var rightWidth = availableWidth - usedWidth - _imageGapInches;
            var rightColumns = GetFitCount(rightWidth, rotatedWidth, _imageGapInches);
            var rightRows = GetFitCount(usedHeight, rotatedHeight, _imageGapInches);
            for (var row = 0; row < rightRows; row++)
                for (var column = 0; column < rightColumns; column++)
                    slots.Add(new TicketPageSlot
                    {
                        XInches = rightX + column * (rotatedWidth + _imageGapInches),
                        YInches = _topMarginInches +
                            row * (rotatedHeight + _imageGapInches),
                        IsRotated = true
                    });

            return slots;
        }

        private static int GetFitCount(double availableSize, double itemSize, double gap)
        {
            if (availableSize + 0.000001 < itemSize || itemSize <= 0) return 0;
            return Math.Max(0, (int)Math.Floor(
                (availableSize + gap) / (itemSize + gap) + 0.000001));
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
            if (_printTicketBorders)
            {
                ticket.Children.Add(new Border
                {
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(
                        PrintedTicketBorderWidthPixels * PreviewUnitsPerInch / 96.0),
                    IsHitTestVisible = false
                });
            }
            return ticket;
        }

        private void UpdateDuplicateControls()
        {
            var hasImage = _previewImageSource != null;
            _previewCopyCount = Math.Max(1, Math.Min(_previewCopyCount, MaximumImageCopies));
            NumberRangePanel.IsEnabled = hasImage && _numberingBoxes.Count > 0;
            var baseNumber = GetBaseNumberText();
            UpdateNumberRangeControls(baseNumber);
            UpdatePageCapacityText();
        }

        private void UpdatePageCapacityText()
        {
            if (PageCapacityText == null) return;
            PageCapacityText.Text = _previewImageSource == null
                ? "Images per page: --"
                : string.Format(CultureInfo.CurrentCulture, "Images per page: {0:N0}",
                    GetCopyCapacity());
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
                endNumber.Any(character => character < '0' || character > '9'))
                return;

            var copyCount = GetCopyCountForRange(startNumber, endNumber);
            if (copyCount == 0) return;

            foreach (var box in _numberingBoxes)
                box.TextValue = startNumber;
            _previewCopyCount = copyCount;
            _currentPageIndex = 0;
            UpdateTicketPreview();
        }

        private static int GetCopyCountForRange(string startNumber, string endNumber)
        {
            if (string.IsNullOrEmpty(startNumber) || string.IsNullOrEmpty(endNumber))
                return 0;

            var numberLength = Math.Max(startNumber.Length, endNumber.Length);
            var paddedStart = startNumber.PadLeft(numberLength, '0');
            var paddedEnd = endNumber.PadLeft(numberLength, '0');
            if (string.CompareOrdinal(paddedStart, paddedEnd) > 0) return 0;

            var differenceDigits = new int[numberLength];
            var borrow = 0;
            for (var index = numberLength - 1; index >= 0; index--)
            {
                var difference = (paddedEnd[index] - '0') -
                                 (paddedStart[index] - '0') - borrow;
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

            var maximumDifference = MaximumImageCopies - 1;
            var totalDifference = 0;
            foreach (var digit in differenceDigits)
            {
                if (totalDifference > (maximumDifference - digit) / 10)
                    return MaximumImageCopies;
                totalDifference = totalDifference * 10 + digit;
            }
            return Math.Min(MaximumImageCopies, totalDifference + 1);
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

        private int GetCopyCapacity()
        {
            return Math.Max(1, Math.Min(MaximumImageCopies, GetTicketPageSlots().Count));
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
                    _numberingBoxes, _previewCopyCount,
                    _sideMarginInches * MillimetersPerInch,
                    _imageGapInches * MillimetersPerInch,
                    _topMarginInches * MillimetersPerInch,
                    _autoRotateToFill, _printTicketBorders);
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
