using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Views
{
    public partial class TicketImageSetupWindow : Window
    {
        private const double PreviewUnitsPerInch = 100.0;
        private readonly List<TicketNumberBoxSettings> _numberingBoxes = new List<TicketNumberBoxSettings>();
        private bool _ready;
        private bool _updatingSelectedControls;
        private bool _updatingSizeControls;
        private TicketNumberBoxSettings _selectedBox;
        private string _sizeUnit = "in";
        private readonly List<EditorState> _undoStates = new List<EditorState>();
        private EditorState _pendingTextUndoState;

        private sealed class EditorState
        {
            public double WidthInches { get; set; }
            public double HeightInches { get; set; }
            public int SelectedIndex { get; set; }
            public List<TicketNumberBoxSettings> Boxes { get; set; }
        }

        public double TicketWidthInches { get; private set; }
        public double TicketHeightInches { get; private set; }
        public int NumberingBoxCount { get { return _numberingBoxes.Count; } }
        public IList<TicketNumberBoxSettings> NumberingBoxes
        {
            get { return _numberingBoxes.Select(item => item.Clone()).ToList(); }
        }

        public TicketImageSetupWindow(string imagePath, double widthInches, double heightInches,
            int numberingBoxCount,
            IList<TicketNumberBoxSettings> existingBoxes = null,
            bool editMode = false)
        {
            InitializeComponent();
            if (editMode)
            {
                Title = "Edit Ticket Image";
                DoneButton.Content = "Update";
            }
            WindowSizingService.Maximize(this);
            TicketWidthInches = widthInches;
            TicketHeightInches = heightInches;

            if (existingBoxes != null)
                foreach (var box in existingBoxes) _numberingBoxes.Add(box.Clone());
            EnsureBoxCount(Math.Max(0, Math.Min(20, numberingBoxCount)));
            foreach (var box in _numberingBoxes) EnsureAppearanceDefaults(box);
            ClampAllBoxes();

            TicketImage.Source = ImageLoadingService.Load(imagePath, 2400);
            SizeUnitBox.ItemsSource = new[] { "Inches (in)", "Centimeters (cm)", "Millimeters (mm)" };
            SizeUnitBox.SelectedIndex = 0;
            UpdateDisplayedSizeValues();
            FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies
                .Select(font => font.Source)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _ready = true;
            UpdatePreview();
            SelectBox(_numberingBoxes.FirstOrDefault());
        }

        private void TicketSizeValue_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _updatingSizeControls) return;
            ApplySize(false);
        }

        private bool ApplySize(bool showMessage)
        {
            double width;
            double height;
            if (!TryReadDisplayedDimension(TicketWidthBox.Text, out width) ||
                !TryReadDisplayedDimension(TicketHeightBox.Text, out height))
            {
                if (showMessage)
                    MessageBox.Show(this, "Enter a valid width and height greater than zero.",
                        "Ticket Size", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var previousWidth = TicketWidthInches;
            var previousHeight = TicketHeightInches;
            var sizeChanged = Math.Abs(previousWidth - width) > 0.0001 ||
                              Math.Abs(previousHeight - height) > 0.0001;
            if (sizeChanged)
                PushUndoState();
            TicketWidthInches = width;
            TicketHeightInches = height;
            if (sizeChanged)
                ResizeTextBoxesForImageSize(previousWidth, previousHeight);
            else
                ClampAllBoxes();
            UpdatePreview();
            return true;
        }

        private bool TryReadDisplayedDimension(string text, out double value)
        {
            double displayedValue;
            var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out displayedValue) ||
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out displayedValue);
            value = valid ? displayedValue / GetUnitFactor(_sizeUnit) : 0;
            return valid && value > 0 && value <= 100;
        }

        private void SizeUnit_Changed(object sender, SelectionChangedEventArgs e)
        {
            var previousUnit = _sizeUnit;
            var nextUnit = GetSelectedSizeUnit();
            if (string.Equals(previousUnit, nextUnit, StringComparison.OrdinalIgnoreCase)) return;

            double widthInches;
            double heightInches;
            if (!TryConvertDisplayedValue(TicketWidthBox.Text, previousUnit, out widthInches))
                widthInches = TicketWidthInches;
            if (!TryConvertDisplayedValue(TicketHeightBox.Text, previousUnit, out heightInches))
                heightInches = TicketHeightInches;

            _sizeUnit = nextUnit;
            SetDisplayedSizeValues(widthInches, heightInches);
        }

        private string GetSelectedSizeUnit()
        {
            if (SizeUnitBox == null) return "in";
            if (SizeUnitBox.SelectedIndex == 1) return "cm";
            if (SizeUnitBox.SelectedIndex == 2) return "mm";
            return "in";
        }

        private static bool TryConvertDisplayedValue(string text, string unit, out double inches)
        {
            double displayedValue;
            var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out displayedValue) ||
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out displayedValue);
            inches = valid ? displayedValue / GetUnitFactor(unit) : 0;
            return valid && inches > 0;
        }

        private static double GetUnitFactor(string unit)
        {
            if (string.Equals(unit, "cm", StringComparison.OrdinalIgnoreCase)) return 2.54;
            if (string.Equals(unit, "mm", StringComparison.OrdinalIgnoreCase)) return 25.4;
            return 1.0;
        }

        private void UpdateDisplayedSizeValues()
        {
            SetDisplayedSizeValues(TicketWidthInches, TicketHeightInches);
        }

        private void SetDisplayedSizeValues(double widthInches, double heightInches)
        {
            var factor = GetUnitFactor(_sizeUnit);
            _updatingSizeControls = true;
            try
            {
                TicketWidthBox.Text = (widthInches * factor).ToString("0.###", CultureInfo.CurrentCulture);
                TicketHeightBox.Text = (heightInches * factor).ToString("0.###", CultureInfo.CurrentCulture);
            }
            finally
            {
                _updatingSizeControls = false;
            }
        }

        private void AddTextBox_Click(object sender, RoutedEventArgs e)
        {
            if (NumberingBoxCount >= 20)
            {
                MessageBox.Show(this, "A maximum of 20 text boxes can be added.",
                    "Numbering", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PushUndoState();
            EnsureBoxCount(NumberingBoxCount + 1);
            RenderNumberingBoxes();
            SelectBox(_numberingBoxes.LastOrDefault());
            var addedVisual = FindSelectedVisual();
            var addedTextBox = addedVisual == null ? null : addedVisual.Children.OfType<TextBox>().FirstOrDefault();
            if (addedTextBox != null) addedTextBox.Focus();
        }

        private void EnsureBoxCount(int count)
        {
            while (_numberingBoxes.Count < count)
                _numberingBoxes.Add(CreateDefaultBox(_numberingBoxes.Count));
            while (_numberingBoxes.Count > count)
            {
                var removed = _numberingBoxes[_numberingBoxes.Count - 1];
                _numberingBoxes.RemoveAt(_numberingBoxes.Count - 1);
                if (ReferenceEquals(_selectedBox, removed)) _selectedBox = null;
            }
        }

        private TicketNumberBoxSettings CreateDefaultBox(int index)
        {
            const double gap = 0.1;
            var template = _selectedBox ?? _numberingBoxes.LastOrDefault();
            var actualWidth = Math.Min(TicketWidthInches,
                Math.Max(0.1, template == null
                    ? TicketWidthInches * 0.10
                    : template.WidthInches));
            var actualHeight = Math.Min(TicketHeightInches,
                Math.Max(0.1, template == null
                    ? TicketHeightInches * 0.10
                    : template.HeightInches));
            var initialFontSizePoints = template == null
                ? GetTenPercentFontSizePoints()
                : Math.Max(4, Math.Min(200, template.FontSizePoints));
            var fontFamilyName = template == null || string.IsNullOrWhiteSpace(template.FontFamilyName)
                ? "Segoe UI"
                : template.FontFamilyName;
            var x = Math.Max(0, (TicketWidthInches - actualWidth) / 2);
            var y = Math.Max(0, (TicketHeightInches - actualHeight) / 2);
            if (index > 0 && _numberingBoxes.Count > 0)
            {
                var previous = template ?? _numberingBoxes[Math.Min(index - 1, _numberingBoxes.Count - 1)];
                var right = previous.XInches + previous.WidthInches + gap;
                var left = previous.XInches - gap - actualWidth;
                if (right + actualWidth <= TicketWidthInches)
                {
                    x = right;
                    y = previous.YInches;
                }
                else if (left >= 0)
                {
                    x = left;
                    y = previous.YInches;
                }
                else
                {
                    x = Math.Max(0, (TicketWidthInches - actualWidth) / 2);
                    y = Math.Min(Math.Max(0, TicketHeightInches - actualHeight),
                        previous.YInches + previous.HeightInches + gap);
                }
            }
            return new TicketNumberBoxSettings
            {
                XInches = x,
                YInches = y,
                WidthInches = actualWidth,
                HeightInches = actualHeight,
                RotationDegrees = 0,
                TextValue = "",
                FontFamilyName = fontFamilyName,
                FontSizePoints = initialFontSizePoints,
                TextColor = "#FF000000",
                BackgroundColor = "#FFFFFFFF",
                IsBackgroundTransparent = false,
                BorderColor = "#FF000000",
                IsBorderTransparent = false,
                BorderThicknessPixels = 1
            };
        }

        private static void EnsureAppearanceDefaults(TicketNumberBoxSettings box)
        {
            box.TextValue = KeepAsciiDigits(box.TextValue);
            if (string.IsNullOrWhiteSpace(box.TextColor)) box.TextColor = "#FF000000";
            if (string.IsNullOrWhiteSpace(box.BackgroundColor)) box.BackgroundColor = "#FFFFFFFF";
            if (string.IsNullOrWhiteSpace(box.BorderColor)) box.BorderColor = "#FF000000";
            if (double.IsNaN(box.BorderThicknessPixels) || double.IsInfinity(box.BorderThicknessPixels) ||
                box.BorderThicknessPixels <= 0)
                box.BorderThicknessPixels = 1;
            box.BorderThicknessPixels = Math.Max(0.1, Math.Min(20, box.BorderThicknessPixels));
        }

        private void ClampAllBoxes()
        {
            foreach (var box in _numberingBoxes)
            {
                box.WidthInches = Math.Max(0.1, Math.Min(box.WidthInches, TicketWidthInches));
                box.HeightInches = Math.Max(0.1, Math.Min(box.HeightInches, TicketHeightInches));
                ClampBoxToTicket(box);
            }
        }

        private void ClampBoxToTicket(TicketNumberBoxSettings box)
        {
            if (box == null) return;

            var radians = box.RotationDegrees * Math.PI / 180.0;
            var rotatedWidth = Math.Abs(box.WidthInches * Math.Cos(radians)) +
                               Math.Abs(box.HeightInches * Math.Sin(radians));
            var rotatedHeight = Math.Abs(box.WidthInches * Math.Sin(radians)) +
                                Math.Abs(box.HeightInches * Math.Cos(radians));
            var centerX = box.XInches + box.WidthInches / 2.0;
            var centerY = box.YInches + box.HeightInches / 2.0;

            centerX = rotatedWidth <= TicketWidthInches
                ? Math.Max(rotatedWidth / 2.0,
                    Math.Min(centerX, TicketWidthInches - rotatedWidth / 2.0))
                : TicketWidthInches / 2.0;
            centerY = rotatedHeight <= TicketHeightInches
                ? Math.Max(rotatedHeight / 2.0,
                    Math.Min(centerY, TicketHeightInches - rotatedHeight / 2.0))
                : TicketHeightInches / 2.0;

            box.XInches = centerX - box.WidthInches / 2.0;
            box.YInches = centerY - box.HeightInches / 2.0;
        }

        private double GetTenPercentFontSizePoints()
        {
            return Math.Max(4, Math.Min(200,
                TicketHeightInches * 0.10 * 72.0));
        }

        private void ResizeTextBoxesForImageSize(double previousWidth, double previousHeight)
        {
            var scaleX = previousWidth > 0 ? TicketWidthInches / previousWidth : 1.0;
            var scaleY = previousHeight > 0 ? TicketHeightInches / previousHeight : 1.0;
            var baseWidth = Math.Min(TicketWidthInches,
                Math.Max(0.1, TicketWidthInches * 0.10));
            var baseHeight = Math.Min(TicketHeightInches,
                Math.Max(0.1, TicketHeightInches * 0.10));
            var fontSizePoints = GetTenPercentFontSizePoints();

            foreach (var box in _numberingBoxes)
            {
                var centerX = (box.XInches + box.WidthInches / 2.0) * scaleX;
                var centerY = (box.YInches + box.HeightInches / 2.0) * scaleY;

                box.WidthInches = baseWidth;
                box.HeightInches = baseHeight;
                box.FontSizePoints = fontSizePoints;

                if (!string.IsNullOrEmpty(box.TextValue))
                    ResizeBoxToFitText(box);

                box.XInches = centerX - box.WidthInches / 2.0;
                box.YInches = centerY - box.HeightInches / 2.0;
                ClampBoxToTicket(box);
            }
        }

        private void UpdatePreview()
        {
            TicketPreview.Width = TicketWidthInches * PreviewUnitsPerInch;
            TicketPreview.Height = TicketHeightInches * PreviewUnitsPerInch;
            NumberingCanvas.Width = TicketPreview.Width;
            NumberingCanvas.Height = TicketPreview.Height;
            RenderNumberingBoxes();
        }

        private void RenderNumberingBoxes()
        {
            if (NumberingCanvas == null) return;
            NumberingCanvas.Children.Clear();
            foreach (var box in _numberingBoxes)
                NumberingCanvas.Children.Add(CreateBoxVisual(box));
            RefreshSelectionVisuals();
        }

        private FrameworkElement CreateBoxVisual(TicketNumberBoxSettings box)
        {
            var root = new Grid
            {
                Width = box.WidthInches * PreviewUnitsPerInch,
                Height = box.HeightInches * PreviewUnitsPerInch,
                Tag = box,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(box.RotationDegrees)
            };
            Canvas.SetLeft(root, box.XInches * PreviewUnitsPerInch);
            Canvas.SetTop(root, box.YInches * PreviewUnitsPerInch);

            var border = new Border
            {
                Background = GetBackgroundBrush(box),
                BorderBrush = GetBorderBrush(box),
                BorderThickness = new Thickness(GetBorderThicknessForPreview(box))
            };
            root.Children.Add(border);

            var moveThumb = new Thumb
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll,
                Tag = box
            };
            moveThumb.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            Panel.SetZIndex(moveThumb, 4);
            moveThumb.PreviewMouseLeftButtonDown += delegate { SelectBox(box); };
            var previousMovePoint = new Point();
            moveThumb.DragStarted += delegate
            {
                SelectBox(box);
                PushUndoState();
                previousMovePoint = Mouse.GetPosition(NumberingCanvas);
            };
            moveThumb.DragDelta += delegate(object sender, DragDeltaEventArgs e)
            {
                var currentMovePoint = Mouse.GetPosition(NumberingCanvas);
                box.XInches += (currentMovePoint.X - previousMovePoint.X) / PreviewUnitsPerInch;
                box.YInches += (currentMovePoint.Y - previousMovePoint.Y) / PreviewUnitsPerInch;
                previousMovePoint = currentMovePoint;
                ClampBoxToTicket(box);
                Canvas.SetLeft(root, box.XInches * PreviewUnitsPerInch);
                Canvas.SetTop(root, box.YInches * PreviewUnitsPerInch);
            };
            root.Children.Add(moveThumb);

            var numberText = new TextBox
            {
                Text = box.TextValue ?? "",
                Foreground = GetBrush(box.TextColor, Colors.Black),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontFamily = GetFontFamily(box.FontFamilyName),
                FontSize = box.FontSizePoints * PreviewUnitsPerInch / 72.0,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = false,
                Margin = new Thickness(0),
                Padding = new Thickness(0)
            };
            var emptyHint = new Border
            {
                Background = Brushes.Transparent,
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Tag = "EmptyNumberHint",
                Child = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.Both,
                    Child = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(235, 37, 99, 235)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 2, 5, 2),
                        Child = new TextBlock
                        {
                            Text = "Enter number",
                            Foreground = Brushes.White,
                            FontFamily = new FontFamily("Segoe UI"),
                            FontSize = 9,
                            FontWeight = FontWeights.SemiBold
                        }
                    }
                }
            };
            numberText.PreviewMouseLeftButtonDown += delegate { SelectBox(box); };
            numberText.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e)
            {
                e.Handled = string.IsNullOrEmpty(e.Text) || e.Text.Any(character => character < '0' || character > '9');
            };
            DataObject.AddPastingHandler(numberText, delegate(object sender, DataObjectPastingEventArgs e)
            {
                if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
                {
                    e.CancelCommand();
                    return;
                }
                var pastedText = Convert.ToString(e.SourceDataObject.GetData(DataFormats.UnicodeText, true));
                if (string.IsNullOrEmpty(pastedText) || pastedText.Any(character => character < '0' || character > '9'))
                    e.CancelCommand();
            });
            numberText.GotKeyboardFocus += delegate
            {
                SelectBox(box);
                _pendingTextUndoState = CaptureState();
            };
            numberText.TextChanged += delegate
            {
                if (_pendingTextUndoState != null)
                {
                    PushUndoState(_pendingTextUndoState);
                    _pendingTextUndoState = null;
                }
                box.TextValue = numberText.Text ?? "";
                ResizeBoxToFitText(box);
                root.Width = box.WidthInches * PreviewUnitsPerInch;
                root.Height = box.HeightInches * PreviewUnitsPerInch;
                UpdateCornerHandleSizes(root);
                Canvas.SetLeft(root, box.XInches * PreviewUnitsPerInch);
                Canvas.SetTop(root, box.YInches * PreviewUnitsPerInch);
                emptyHint.Visibility = ReferenceEquals(box, _selectedBox) &&
                                       string.IsNullOrEmpty(box.TextValue)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            };
            Panel.SetZIndex(numberText, 2);
            root.Children.Add(numberText);
            Panel.SetZIndex(emptyHint, 3);
            root.Children.Add(emptyHint);

            var resizeThumb = new Thumb
            {
                Width = 14,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeNWSE,
                Tag = box
            };
            Panel.SetZIndex(resizeThumb, 4);
            resizeThumb.DragStarted += delegate
            {
                SelectBox(box);
                PushUndoState();
            };
            resizeThumb.DragDelta += delegate(object sender, DragDeltaEventArgs e)
            {
                var minimumSize = MeasureTextBoxContent(box);
                box.WidthInches = Math.Max(minimumSize.Width, Math.Min(
                    box.WidthInches + e.HorizontalChange / PreviewUnitsPerInch,
                    TicketWidthInches));
                box.HeightInches = Math.Max(minimumSize.Height, Math.Min(
                    box.HeightInches + e.VerticalChange / PreviewUnitsPerInch,
                    TicketHeightInches));
                ClampBoxToTicket(box);
                root.Width = box.WidthInches * PreviewUnitsPerInch;
                root.Height = box.HeightInches * PreviewUnitsPerInch;
                UpdateCornerHandleSizes(root);
                Canvas.SetLeft(root, box.XInches * PreviewUnitsPerInch);
                Canvas.SetTop(root, box.YInches * PreviewUnitsPerInch);
            };
            root.Children.Add(resizeThumb);
            UpdateCornerHandleSizes(root);
            return root;
        }

        private static void UpdateCornerHandleSizes(Grid visual)
        {
            if (visual == null) return;
            var shorterSide = Math.Min(visual.Width, visual.Height);
            var handleSize = double.IsNaN(shorterSide) || double.IsInfinity(shorterSide)
                ? 14.0
                : Math.Max(4.0, Math.Min(14.0, shorterSide * 0.28));
            foreach (var handle in visual.Children.OfType<Thumb>())
            {
                handle.Width = handleSize;
                handle.Height = handleSize;
            }
        }

        private static string KeepAsciiDigits(string value)
        {
            return string.IsNullOrEmpty(value)
                ? ""
                : new string(value.Where(character => character >= '0' && character <= '9').ToArray());
        }

        private static Brush GetBackgroundBrush(TicketNumberBoxSettings box)
        {
            return box.IsBackgroundTransparent ? Brushes.Transparent : GetBrush(box.BackgroundColor, Colors.White);
        }

        private static Brush GetBorderBrush(TicketNumberBoxSettings box)
        {
            return box.IsBorderTransparent ? Brushes.Transparent : GetBrush(box.BorderColor, Colors.Black);
        }

        private static double GetBorderThicknessForPreview(TicketNumberBoxSettings box)
        {
            return box.IsBorderTransparent
                ? 0
                : box.BorderThicknessPixels * PreviewUnitsPerInch / 96.0;
        }

        private static SolidColorBrush GetBrush(string value, Color fallback)
        {
            return new SolidColorBrush(GetColor(value, fallback));
        }

        private static Color GetColor(string value, Color fallback)
        {
            try
            {
                var converted = ColorConverter.ConvertFromString(value);
                return converted is Color ? (Color)converted : fallback;
            }
            catch { return fallback; }
        }

        private static string ToColorString(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}",
                color.A, color.R, color.G, color.B);
        }

        private static FontFamily GetFontFamily(string name)
        {
            try { return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name); }
            catch { return new FontFamily("Segoe UI"); }
        }

        private void NumberingCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, NumberingCanvas)) SelectBox(null);
        }

        private void SelectBox(TicketNumberBoxSettings box)
        {
            _selectedBox = box;
            RefreshSelectionVisuals();
            _updatingSelectedControls = true;
            try
            {
                SelectedBoxControls.IsEnabled = box != null;
                FontFamilyBox.SelectedItem = box == null ? null : box.FontFamilyName;
                FontSizeBox.Text = box == null ? "" : box.FontSizePoints.ToString("0.##", CultureInfo.CurrentCulture);
                BackgroundTransparentBox.IsChecked = box != null && box.IsBackgroundTransparent;
                BorderTransparentBox.IsChecked = box != null && box.IsBorderTransparent;
                BorderThicknessBox.Text = box == null
                    ? ""
                    : box.BorderThicknessPixels.ToString("0.##", CultureInfo.CurrentCulture);
                BackgroundColorButton.IsEnabled = box != null && !box.IsBackgroundTransparent;
                BorderColorButton.IsEnabled = box != null && !box.IsBorderTransparent;
                BorderThicknessBox.IsEnabled = box != null && !box.IsBorderTransparent;
                BackgroundColorSwatch.Background = box == null
                    ? Brushes.Transparent : GetBrush(box.BackgroundColor, Colors.White);
                BorderColorSwatch.Background = box == null
                    ? Brushes.Transparent : GetBrush(box.BorderColor, Colors.Black);
                FontColorSwatch.Background = box == null
                    ? Brushes.Transparent : GetBrush(box.TextColor, Colors.Black);
            }
            finally
            {
                _updatingSelectedControls = false;
            }
        }

        private void RefreshSelectionVisuals()
        {
            if (NumberingCanvas == null) return;
            foreach (var child in NumberingCanvas.Children.OfType<Grid>())
            {
                var box = child.Tag as TicketNumberBoxSettings;
                var selected = ReferenceEquals(box, _selectedBox);
                var border = child.Children.Count > 0 ? child.Children[0] as Border : null;
                if (border != null)
                {
                    border.BorderBrush = GetBorderBrush(box);
                    border.BorderThickness = new Thickness(GetBorderThicknessForPreview(box));
                }
                foreach (var handle in child.Children.OfType<Thumb>())
                    handle.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                var emptyHint = child.Children.OfType<Border>()
                    .FirstOrDefault(item => Convert.ToString(item.Tag) == "EmptyNumberHint");
                if (emptyHint != null)
                    emptyHint.Visibility = selected && string.IsNullOrEmpty(box.TextValue)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
        }

        private Grid FindSelectedVisual()
        {
            return NumberingCanvas.Children.OfType<Grid>()
                .FirstOrDefault(item => ReferenceEquals(item.Tag, _selectedBox));
        }

        private void UpdateSelectedVisual()
        {
            var visual = FindSelectedVisual();
            if (visual == null || _selectedBox == null) return;
            visual.Width = _selectedBox.WidthInches * PreviewUnitsPerInch;
            visual.Height = _selectedBox.HeightInches * PreviewUnitsPerInch;
            UpdateCornerHandleSizes(visual);
            Canvas.SetLeft(visual, _selectedBox.XInches * PreviewUnitsPerInch);
            Canvas.SetTop(visual, _selectedBox.YInches * PreviewUnitsPerInch);
            visual.RenderTransform = new RotateTransform(_selectedBox.RotationDegrees);
            var border = visual.Children[0] as Border;
            var text = visual.Children.OfType<TextBox>().FirstOrDefault();
            if (border != null)
            {
                border.Background = GetBackgroundBrush(_selectedBox);
                border.BorderBrush = GetBorderBrush(_selectedBox);
                border.BorderThickness = new Thickness(GetBorderThicknessForPreview(_selectedBox));
            }
            if (text != null)
            {
                text.Foreground = GetBrush(_selectedBox.TextColor, Colors.Black);
                text.FontFamily = GetFontFamily(_selectedBox.FontFamilyName);
                text.FontSize = _selectedBox.FontSizePoints * PreviewUnitsPerInch / 72.0;
            }
        }

        private void BackgroundColor_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBox == null) return;
            var selected = ChooseColor(_selectedBox.BackgroundColor, Colors.White);
            if (!selected.HasValue) return;
            PushUndoState();
            _selectedBox.BackgroundColor = ToColorString(selected.Value);
            _selectedBox.IsBackgroundTransparent = false;
            SelectBox(_selectedBox);
            UpdateSelectedVisual();
        }

        private void FontColor_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBox == null) return;
            var selected = ChooseColor(_selectedBox.TextColor, Colors.Black);
            if (!selected.HasValue) return;
            PushUndoState();
            _selectedBox.TextColor = ToColorString(selected.Value);
            SelectBox(_selectedBox);
            UpdateSelectedVisual();
        }

        private void BorderColor_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBox == null) return;
            var selected = ChooseColor(_selectedBox.BorderColor, Colors.Black);
            if (!selected.HasValue) return;
            PushUndoState();
            _selectedBox.BorderColor = ToColorString(selected.Value);
            _selectedBox.IsBorderTransparent = false;
            SelectBox(_selectedBox);
            UpdateSelectedVisual();
        }

        private Color? ChooseColor(string currentValue, Color fallback)
        {
            var current = GetColor(currentValue, fallback);
            using (var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B)
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                return Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
            }
        }

        private void BackgroundTransparent_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready || _updatingSelectedControls || _selectedBox == null) return;
            PushUndoState();
            _selectedBox.IsBackgroundTransparent = BackgroundTransparentBox.IsChecked == true;
            BackgroundColorButton.IsEnabled = !_selectedBox.IsBackgroundTransparent;
            UpdateSelectedVisual();
        }

        private void BorderTransparent_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready || _updatingSelectedControls || _selectedBox == null) return;
            PushUndoState();
            _selectedBox.IsBorderTransparent = BorderTransparentBox.IsChecked == true;
            BorderColorButton.IsEnabled = !_selectedBox.IsBorderTransparent;
            BorderThicknessBox.IsEnabled = !_selectedBox.IsBorderTransparent;
            EnsureBoxContainsText(_selectedBox);
            UpdateSelectedVisual();
        }

        private void BorderThickness_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _updatingSelectedControls || _selectedBox == null) return;
            double thickness;
            if (!TryReadNumber(BorderThicknessBox.Text, out thickness) ||
                thickness < 0.1 || thickness > 20)
                return;
            if (Math.Abs(_selectedBox.BorderThicknessPixels - thickness) < 0.0001) return;
            PushUndoState();
            _selectedBox.BorderThicknessPixels = thickness;
            EnsureBoxContainsText(_selectedBox);
            UpdateSelectedVisual();
        }

        private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _updatingSelectedControls || _selectedBox == null) return;
            var name = Convert.ToString(FontFamilyBox.SelectedItem);
            if (string.IsNullOrWhiteSpace(name)) return;
            PushUndoState();
            _selectedBox.FontFamilyName = name;
            UpdateSelectedVisual();
        }

        private void FontSize_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _updatingSelectedControls || _selectedBox == null) return;
            double size;
            if (!TryReadNumber(FontSizeBox.Text, out size) || size < 4 || size > 200) return;
            if (Math.Abs(_selectedBox.FontSizePoints - size) < 0.0001) return;
            PushUndoState();
            _selectedBox.FontSizePoints = size;
            ResizeBoxToFitText(_selectedBox);
            UpdateSelectedVisual();
        }

        private void ResizeBoxToFitText(TicketNumberBoxSettings box)
        {
            if (box == null) return;
            var desiredSize = MeasureTextBoxContent(box);
            box.WidthInches = desiredSize.Width;
            box.HeightInches = desiredSize.Height;
            ClampBoxToTicket(box);
        }

        private Size MeasureTextBoxContent(TicketNumberBoxSettings box)
        {
            var measurement = new TextBlock
            {
                Text = string.IsNullOrEmpty(box.TextValue) ? " " : box.TextValue,
                FontFamily = GetFontFamily(box.FontFamilyName),
                FontSize = box.FontSizePoints * PreviewUnitsPerInch / 72.0,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap
            };
            measurement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var borderAllowance = box.IsBorderTransparent
                ? 0
                : box.BorderThicknessPixels * 2.0 / 96.0;

            return new Size(
                Math.Min(TicketWidthInches, Math.Max(0.1,
                    measurement.DesiredSize.Width / PreviewUnitsPerInch + borderAllowance)),
                Math.Min(TicketHeightInches, Math.Max(0.1,
                    measurement.DesiredSize.Height / PreviewUnitsPerInch + borderAllowance)));
        }

        private void EnsureBoxContainsText(TicketNumberBoxSettings box)
        {
            if (box == null) return;
            var minimumSize = MeasureTextBoxContent(box);
            if (box.WidthInches >= minimumSize.Width && box.HeightInches >= minimumSize.Height)
                return;

            var centerX = box.XInches + box.WidthInches / 2.0;
            var centerY = box.YInches + box.HeightInches / 2.0;
            box.WidthInches = Math.Max(box.WidthInches, minimumSize.Width);
            box.HeightInches = Math.Max(box.HeightInches, minimumSize.Height);
            box.XInches = centerX - box.WidthInches / 2.0;
            box.YInches = centerY - box.HeightInches / 2.0;
            ClampBoxToTicket(box);
        }

        private void RotateCounterClockwise_Click(object sender, RoutedEventArgs e)
        {
            RotateSelectedBox(-90);
        }

        private void RotateClockwise_Click(object sender, RoutedEventArgs e)
        {
            RotateSelectedBox(90);
        }

        private void RotateSelectedBox(double change)
        {
            if (!_ready || _selectedBox == null) return;
            PushUndoState();
            _selectedBox.RotationDegrees = NormalizeRotation(_selectedBox.RotationDegrees + change);
            ClampBoxToTicket(_selectedBox);
            UpdateSelectedVisual();
        }

        private static double NormalizeRotation(double rotation)
        {
            rotation %= 360;
            if (rotation < 0) rotation += 360;
            return rotation;
        }

        private EditorState CaptureState()
        {
            return new EditorState
            {
                WidthInches = TicketWidthInches,
                HeightInches = TicketHeightInches,
                SelectedIndex = _selectedBox == null ? -1 : _numberingBoxes.IndexOf(_selectedBox),
                Boxes = _numberingBoxes.Select(box => box.Clone()).ToList()
            };
        }

        private void PushUndoState(EditorState state = null)
        {
            _undoStates.Add(state ?? CaptureState());
            if (_undoStates.Count > 100) _undoStates.RemoveAt(0);
            if (UndoButton != null) UndoButton.IsEnabled = true;
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            UndoLastEdit();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _selectedBox != null)
            {
                DeleteSelectedBox();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                UndoLastEdit();
                e.Handled = true;
            }
        }

        private void DeleteSelectedBox()
        {
            if (_selectedBox == null) return;
            var removedIndex = _numberingBoxes.IndexOf(_selectedBox);
            if (removedIndex < 0) return;

            PushUndoState();
            _pendingTextUndoState = null;
            _numberingBoxes.RemoveAt(removedIndex);
            _selectedBox = null;
            RenderNumberingBoxes();

            var nextBox = _numberingBoxes.Count == 0
                ? null
                : _numberingBoxes[Math.Min(removedIndex, _numberingBoxes.Count - 1)];
            SelectBox(nextBox);
        }

        private void UndoLastEdit()
        {
            if (_undoStates.Count == 0) return;
            var index = _undoStates.Count - 1;
            var state = _undoStates[index];
            _undoStates.RemoveAt(index);
            _pendingTextUndoState = null;

            TicketWidthInches = state.WidthInches;
            TicketHeightInches = state.HeightInches;
            _numberingBoxes.Clear();
            foreach (var box in state.Boxes) _numberingBoxes.Add(box.Clone());
            UpdateDisplayedSizeValues();
            UpdatePreview();
            SelectBox(state.SelectedIndex >= 0 && state.SelectedIndex < _numberingBoxes.Count
                ? _numberingBoxes[state.SelectedIndex]
                : null);
            UndoButton.IsEnabled = _undoStates.Count > 0;
        }

        private static bool TryReadNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void Done_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplySize(true)) return;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
