using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Controls
{
    public class PaperPreviewControl : FrameworkElement
    {
        private readonly Dictionary<string, ImageSource> _images = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<PrintPlacement> _selectedPlacements = new HashSet<PrintPlacement>();
        private PrintPlacement _selectedPlacement;
        private bool _panningPhoto;
        private Point _panStart;
        private Point _panStartOffset;
        private Rect _pageRect;
        private double _scale;

        public event EventHandler SelectedPhotoChanged;

        public PaperPreviewControl()
        {
            Focusable = true;
            AllowPhotoReposition = true;
            ShowEmptyPictureBoxes = true;
        }

        public bool AllowPhotoReposition { get; set; }
        public bool ShowEmptyPictureBoxes { get; set; }

        public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
            "Layout", typeof(PrintLayout), typeof(PaperPreviewControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, PreviewContext_Changed));

        public static readonly DependencyProperty PageIndexProperty = DependencyProperty.Register(
            "PageIndex", typeof(int), typeof(PaperPreviewControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, PreviewContext_Changed));

        public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
            "ShowGrid", typeof(bool), typeof(PaperPreviewControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowCutGuidesProperty = DependencyProperty.Register(
            "ShowCutGuides", typeof(bool), typeof(PaperPreviewControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowPictureBordersProperty = DependencyProperty.Register(
            "ShowPictureBorders", typeof(bool), typeof(PaperPreviewControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public PrintLayout Layout
        {
            get { return (PrintLayout)GetValue(LayoutProperty); }
            set { SetValue(LayoutProperty, value); }
        }

        public int PageIndex
        {
            get { return (int)GetValue(PageIndexProperty); }
            set { SetValue(PageIndexProperty, value); }
        }

        public bool ShowGrid
        {
            get { return (bool)GetValue(ShowGridProperty); }
            set { SetValue(ShowGridProperty, value); }
        }

        public bool ShowCutGuides
        {
            get { return (bool)GetValue(ShowCutGuidesProperty); }
            set { SetValue(ShowCutGuidesProperty, value); }
        }

        public bool ShowPictureBorders
        {
            get { return (bool)GetValue(ShowPictureBordersProperty); }
            set { SetValue(ShowPictureBordersProperty, value); }
        }

        public bool HasSelectedPhoto
        {
            get { return _selectedPlacements.Any(item => !string.IsNullOrWhiteSpace(item.PhotoPath)); }
        }

        public bool HasPhotos
        {
            get
            {
                return Layout != null && Layout.Placements.Any(item =>
                    !string.IsNullOrWhiteSpace(item.PhotoPath));
            }
        }

        public string SelectedPhotoFitMode
        {
            get
            {
                var modes = _selectedPlacements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => string.Equals(item.PhotoFitMode, "Stretch", StringComparison.OrdinalIgnoreCase)
                        ? "Stretch"
                        : "Crop")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return modes.Count == 1 ? modes[0] : null;
            }
        }

        public bool? SelectedPhotoPrintsBorder
        {
            get
            {
                var choices = _selectedPlacements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => item.PrintPictureBorder).Distinct().ToList();
                return choices.Count == 1 ? (bool?)choices[0] : null;
            }
        }

        public string AllPhotoFitMode
        {
            get
            {
                if (Layout == null) return null;
                var modes = Layout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => string.Equals(item.PhotoFitMode, "Stretch", StringComparison.OrdinalIgnoreCase)
                        ? "Stretch"
                        : "Crop")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return modes.Count == 1 ? modes[0] : null;
            }
        }

        public bool? AllPhotosPrintBorder
        {
            get
            {
                if (Layout == null) return null;
                var choices = Layout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => item.PrintPictureBorder).Distinct().ToList();
                return choices.Count == 1 ? (bool?)choices[0] : null;
            }
        }

        public bool SetSelectedPhotoFitMode(string mode)
        {
            if (!HasSelectedPhoto) return false;
            var normalized = string.Equals(mode, "Stretch", StringComparison.OrdinalIgnoreCase)
                ? "Stretch"
                : "Crop";
            foreach (var placement in _selectedPlacements)
                placement.PhotoFitMode = normalized;
            InvalidateVisual();
            return true;
        }

        public bool SetSelectedPhotoPictureBorder(bool printBorder)
        {
            if (!HasSelectedPhoto) return false;
            foreach (var placement in _selectedPlacements)
                placement.PrintPictureBorder = printBorder;
            InvalidateVisual();
            return true;
        }

        public bool SetAllPhotoFitMode(string mode)
        {
            if (!HasPhotos) return false;
            var normalized = string.Equals(mode, "Stretch", StringComparison.OrdinalIgnoreCase)
                ? "Stretch"
                : "Crop";
            foreach (var placement in Layout.Placements.Where(item =>
                !string.IsNullOrWhiteSpace(item.PhotoPath)))
                placement.PhotoFitMode = normalized;
            InvalidateVisual();
            return true;
        }

        public bool SetAllPhotoPictureBorder(bool printBorder)
        {
            if (!HasPhotos) return false;
            foreach (var placement in Layout.Placements.Where(item =>
                !string.IsNullOrWhiteSpace(item.PhotoPath)))
                placement.PrintPictureBorder = printBorder;
            InvalidateVisual();
            return true;
        }

        private static void PreviewContext_Changed(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs e)
        {
            var control = dependencyObject as PaperPreviewControl;
            if (control == null) return;
            control._selectedPlacement = null;
            control._selectedPlacements.Clear();
            control._panningPhoto = false;
            if (control.SelectedPhotoChanged != null)
                control.SelectedPhotoChanged(control, EventArgs.Empty);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(43, 50, 61)), null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (Layout == null || Layout.PaperWidthMm <= 0 || Layout.PaperHeightMm <= 0)
            {
                DrawCenteredText(dc, "Select a package and paper to begin", Brushes.White);
                return;
            }

            const double padding = 30;
            _scale = Math.Min((ActualWidth - padding * 2) / Layout.PaperWidthMm,
                              (ActualHeight - padding * 2) / Layout.PaperHeightMm);
            if (double.IsNaN(_scale) || double.IsInfinity(_scale) || _scale <= 0) return;
            var pageWidth = Layout.PaperWidthMm * _scale;
            var pageHeight = Layout.PaperHeightMm * _scale;
            var left = (ActualWidth - pageWidth) / 2;
            var top = (ActualHeight - pageHeight) / 2;
            _pageRect = new Rect(left, top, pageWidth, pageHeight);

            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)), null,
                new Rect(left + 6, top + 8, pageWidth, pageHeight));
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(180, 185, 194)), 1), _pageRect);
            dc.PushClip(new RectangleGeometry(_pageRect));

            if (ShowGrid) DrawGrid(dc, _pageRect, _scale);

            foreach (var placement in Layout.Placements)
            {
                if (placement.PageIndex != PageIndex) continue;
                var bounds = PlacementToScreen(placement);
                DrawPlacement(dc, placement, bounds);
            }
            dc.Pop();
        }

        private void DrawGrid(DrawingContext dc, Rect page, double scale)
        {
            var minor = new Pen(new SolidColorBrush(Color.FromArgb(48, 37, 99, 235)), 0.5);
            var major = new Pen(new SolidColorBrush(Color.FromArgb(100, 37, 99, 235)), 0.8);
            for (var mm = 5.0; mm < Layout.PaperWidthMm; mm += 5.0)
            {
                var x = page.Left + mm * scale;
                dc.DrawLine(((int)mm % 10 == 0) ? major : minor, new Point(x, page.Top), new Point(x, page.Bottom));
            }
            for (var mm = 5.0; mm < Layout.PaperHeightMm; mm += 5.0)
            {
                var y = page.Top + mm * scale;
                dc.DrawLine(((int)mm % 10 == 0) ? major : minor, new Point(page.Left, y), new Point(page.Right, y));
            }

            var marginPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 230, 126, 34)), 1) { DashStyle = DashStyles.Dash };
            var m = Layout.MarginMm * scale;
            dc.DrawRectangle(null, marginPen, new Rect(page.Left + m, page.Top + m,
                Math.Max(0, page.Width - m * 2), Math.Max(0, page.Height - m * 2)));
        }

        private void DrawPlacement(DrawingContext dc, PrintPlacement placement, Rect bounds)
        {
            var hasPhoto = !string.IsNullOrWhiteSpace(placement.PhotoPath) && File.Exists(placement.PhotoPath);
            if (!hasPhoto && !ShowEmptyPictureBoxes) return;

            dc.PushTransform(new RotateTransform(placement.RotationDegrees,
                bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            var border = new Pen(new SolidColorBrush(Color.FromRgb(94, 104, 123)), 1);
            var cornerRadius = Math.Max(0, placement.CornerRadiusMm * _scale);
            if (hasPhoto)
            {
                var source = LoadImage(placement.PhotoPath);
                if (source != null)
                {
                    source = OrientForBox(source, placement.AutoRotatePhotoToBox,
                        placement.BoundsMm.Width, placement.BoundsMm.Height);
                    var brush = string.Equals(placement.PhotoFitMode, "Stretch", StringComparison.OrdinalIgnoreCase)
                        ? new ImageBrush(source) { Stretch = Stretch.Fill }
                        : CreateCropBrush(source, bounds,
                            new Point(placement.PhotoOffsetX, placement.PhotoOffsetY));
                    dc.DrawRoundedRectangle(brush, border, bounds, cornerRadius, cornerRadius);
                    if (ShowPictureBorders && placement.PrintPictureBorder)
                        dc.DrawRoundedRectangle(null, new Pen(Brushes.Black, 1.2), bounds,
                            cornerRadius, cornerRadius);
                }
                else DrawPlaceholder(dc, placement, bounds, border, cornerRadius);
            }
            else DrawPlaceholder(dc, placement, bounds, border, cornerRadius);

            if (ShowCutGuides)
            {
                var cutPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 40, 40, 40)), 0.8) { DashStyle = DashStyles.Dash };
                dc.DrawRoundedRectangle(null, cutPen, bounds, cornerRadius, cornerRadius);
            }
            if (AllowPhotoReposition && _selectedPlacements.Contains(placement))
            {
                var selectionBounds = bounds;
                selectionBounds.Inflate(3, 3);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(96, 165, 250)), 2),
                    selectionBounds, cornerRadius + 3, cornerRadius + 3);
            }
            dc.Pop();
        }

        private static ImageBrush CreateCropBrush(ImageSource source, Rect target, Point offset)
        {
            var brush = new ImageBrush(source) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0 || target.Width <= 0 || target.Height <= 0)
            {
                brush.Stretch = Stretch.UniformToFill;
                return brush;
            }

            var targetRatio = target.Width / target.Height;
            var imageRatio = sourceWidth / sourceHeight;
            if (imageRatio > targetRatio)
            {
                var cropWidth = sourceHeight * targetRatio;
                var available = sourceWidth - cropWidth;
                var x = available / 2 * (1 + ClampOffset(offset.X));
                brush.Viewbox = new Rect(x, 0, cropWidth, sourceHeight);
            }
            else
            {
                var cropHeight = sourceWidth / targetRatio;
                var available = sourceHeight - cropHeight;
                var y = available / 2 * (1 + ClampOffset(offset.Y));
                brush.Viewbox = new Rect(0, y, sourceWidth, cropHeight);
            }
            return brush;
        }

        private static ImageSource OrientForBox(ImageSource source, bool autoRotate,
            double boxWidth, double boxHeight)
        {
            var bitmap = source as BitmapSource;
            if (!autoRotate || bitmap == null ||
                !ImageLoadingService.ShouldRotateToMatch(bitmap.PixelWidth, bitmap.PixelHeight,
                    boxWidth, boxHeight))
                return source;

            var rotated = new TransformedBitmap(bitmap, new RotateTransform(90));
            if (rotated.CanFreeze) rotated.Freeze();
            return rotated;
        }

        private Rect PlacementToScreen(PrintPlacement placement)
        {
            return new Rect(_pageRect.Left + placement.BoundsMm.X * _scale,
                _pageRect.Top + placement.BoundsMm.Y * _scale,
                placement.BoundsMm.Width * _scale, placement.BoundsMm.Height * _scale);
        }

        private static double ClampOffset(double value)
        {
            return Math.Max(-1, Math.Min(1, value));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!AllowPhotoReposition)
            {
                _selectedPlacement = null;
                _selectedPlacements.Clear();
                _panningPhoto = false;
                return;
            }
            Focus();
            var point = e.GetPosition(this);
            var clickedPlacement = Layout == null ? null : Layout.Placements
                .Where(item => item.PageIndex == PageIndex &&
                               !string.IsNullOrWhiteSpace(item.PhotoPath) && File.Exists(item.PhotoPath))
                .Reverse().FirstOrDefault(item => PlacementToScreen(item).Contains(point));
            var extendSelection = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            SelectPlacement(clickedPlacement, extendSelection);
            _panningPhoto = false;
            if (_selectedPlacement != null && !extendSelection)
            {
                _panStart = point;
                _panStartOffset = new Point(_selectedPlacement.PhotoOffsetX, _selectedPlacement.PhotoOffsetY);
                _panningPhoto = true;
                CaptureMouse();
                Cursor = Cursors.SizeAll;
            }
            InvalidateVisual();
            if (SelectedPhotoChanged != null) SelectedPhotoChanged(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void SelectPlacement(PrintPlacement placement, bool extendSelection)
        {
            if (!extendSelection) _selectedPlacements.Clear();
            if (placement == null)
            {
                _selectedPlacement = _selectedPlacements.LastOrDefault();
                return;
            }

            if (extendSelection && _selectedPlacements.Contains(placement))
            {
                _selectedPlacements.Remove(placement);
                _selectedPlacement = _selectedPlacements.LastOrDefault();
            }
            else
            {
                _selectedPlacements.Add(placement);
                _selectedPlacement = placement;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!AllowPhotoReposition) return;
            if (!_panningPhoto || _selectedPlacement == null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this);
            var rect = PlacementToScreen(_selectedPlacement);
            _selectedPlacement.PhotoOffsetX = ClampOffset(
                _panStartOffset.X - (point.X - _panStart.X) * 2 / Math.Max(1, rect.Width));
            _selectedPlacement.PhotoOffsetY = ClampOffset(
                _panStartOffset.Y - (point.Y - _panStart.Y) * 2 / Math.Max(1, rect.Height));
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!AllowPhotoReposition) return;
            if (!_panningPhoto) return;
            _panningPhoto = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!AllowPhotoReposition) return;

            if (e.Key == Key.A &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _selectedPlacements.Clear();
                if (Layout != null)
                {
                    foreach (var placement in Layout.Placements.Where(item =>
                        item.PageIndex == PageIndex &&
                        !string.IsNullOrWhiteSpace(item.PhotoPath) &&
                        File.Exists(item.PhotoPath)))
                        _selectedPlacements.Add(placement);
                }
                _selectedPlacement = _selectedPlacements.LastOrDefault();
                _panningPhoto = false;
                InvalidateVisual();
                if (SelectedPhotoChanged != null) SelectedPhotoChanged(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (_selectedPlacement == null || _selectedPlacement.PageIndex != PageIndex) return;
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 0.01 : 0.05;
            switch (e.Key)
            {
                case Key.Left: _selectedPlacement.PhotoOffsetX += step; break;
                case Key.Right: _selectedPlacement.PhotoOffsetX -= step; break;
                case Key.Up: _selectedPlacement.PhotoOffsetY += step; break;
                case Key.Down: _selectedPlacement.PhotoOffsetY -= step; break;
                default: return;
            }
            _selectedPlacement.PhotoOffsetX = ClampOffset(_selectedPlacement.PhotoOffsetX);
            _selectedPlacement.PhotoOffsetY = ClampOffset(_selectedPlacement.PhotoOffsetY);
            InvalidateVisual();
            e.Handled = true;
        }

        private static void DrawPlaceholder(DrawingContext dc, PrintPlacement placement, Rect bounds, Pen border,
            double cornerRadius)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(229, 233, 240)), border, bounds,
                cornerRadius, cornerRadius);
            if (bounds.Width > 42 && bounds.Height > 28)
            {
                var text = new FormattedText(placement.Label ?? "Photo", System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Min(12, bounds.Height / 5),
                    new SolidColorBrush(Color.FromRgb(70, 78, 92)));
                text.MaxTextWidth = Math.Max(1, bounds.Width - 8);
                text.TextAlignment = TextAlignment.Center;
                dc.DrawText(text, new Point(bounds.Left + 4, bounds.Top + (bounds.Height - text.Height) / 2));
            }
        }

        private ImageSource LoadImage(string path)
        {
            ImageSource source;
            if (_images.TryGetValue(path, out source)) return source;
            var bitmap = ImageLoadingService.Load(path, 800) as BitmapSource;
            if (bitmap == null) return null;
            var oriented = ApplyExifOrientation(bitmap);
            _images[path] = oriented;
            return oriented;
        }

        private static ImageSource ApplyExifOrientation(BitmapSource source)
        {
            try
            {
                var metadata = source.Metadata as BitmapMetadata;
                if (metadata == null) return source;
                var value = metadata.GetQuery("/app1/ifd/{ushort=274}");
                if (value == null) value = metadata.GetQuery("/ifd/{ushort=274}");
                if (value == null) return source;
                var orientation = Convert.ToUInt16(value);
                var degrees = orientation == 3 ? 180 : orientation == 5 || orientation == 6
                    ? 90 : orientation == 7 || orientation == 8 ? 270 : 0;
                if (degrees == 0) return source;
                var rotated = new TransformedBitmap(source, new RotateTransform(degrees));
                if (rotated.CanFreeze) rotated.Freeze();
                return rotated;
            }
            catch { return source; }
        }

        private void DrawCenteredText(DrawingContext dc, string value, Brush brush)
        {
            var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, brush);
            dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
        }
    }
}
