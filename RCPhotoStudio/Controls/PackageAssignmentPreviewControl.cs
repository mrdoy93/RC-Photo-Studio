using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Controls
{
    public class PackageAssignmentPreviewControl : FrameworkElement
    {
        public const string PhotoPathFormat = "RCPhotoStudio.PhotoPath";

        public static readonly DependencyProperty PackageProperty = DependencyProperty.Register(
            "Package", typeof(PhotoPackage), typeof(PackageAssignmentPreviewControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Package_Changed));

        private readonly Dictionary<string, string> _assignments = new Dictionary<string, string>();
        private readonly Dictionary<string, Point> _photoOffsets = new Dictionary<string, Point>();
        private readonly Dictionary<string, string> _photoFitModes = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> _pictureBorders = new Dictionary<string, bool>();
        private readonly Dictionary<string, ImageSource> _images = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PackageLayoutBox> _boxes = new List<PackageLayoutBox>();
        private readonly HashSet<PackageLayoutBox> _selectedBoxes = new HashSet<PackageLayoutBox>();
        private PhotoPackage _package;
        private PackageLayoutBox _selectedBox;
        private bool _panningPhoto;
        private Point _panStart;
        private Point _panStartOffset;
        private string _photoFitMode = "Crop";
        private bool _showPictureBorders;
        private Rect _pageRect;
        private double _scale;
        private double _paperWidthMm;
        private double _paperHeightMm;

        public event EventHandler PhotoAssigned;
        public event EventHandler SelectedPhotoChanged;

        public PackageAssignmentPreviewControl()
        {
            AllowDrop = true;
            Focusable = true;
            AllowPhotoReposition = true;
        }

        public bool AllowPhotoReposition { get; set; }

        public string PhotoFitMode
        {
            get { return _photoFitMode; }
            set
            {
                _photoFitMode = string.Equals(value, "Stretch", StringComparison.OrdinalIgnoreCase)
                    ? "Stretch"
                    : "Crop";
                InvalidateVisual();
            }
        }

        public PhotoPackage Package
        {
            get { return (PhotoPackage)GetValue(PackageProperty); }
            set { SetValue(PackageProperty, value); }
        }

        public bool CompactMode { get; set; }

        public bool ShowPictureBorders
        {
            get { return _showPictureBorders; }
            set
            {
                _showPictureBorders = value;
                InvalidateVisual();
            }
        }

        private static void Package_Changed(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var control = dependencyObject as PackageAssignmentPreviewControl;
            if (control == null) return;
            control._package = e.NewValue as PhotoPackage;
            control._showPictureBorders = control._package != null && control._package.PrintPictureBorder;
            control.BuildBoxes();
            control.InvalidateVisual();
        }

        public IList<string> GetSlotPhotoPaths()
        {
            return _boxes.Select(box => _assignments.ContainsKey(box.Id) ? _assignments[box.Id] : null).ToList();
        }

        public IList<Point> GetSlotPhotoOffsets()
        {
            return _boxes.Select(box => _photoOffsets.ContainsKey(box.Id)
                ? _photoOffsets[box.Id]
                : new Point()).ToList();
        }

        public IList<string> GetSlotPhotoFitModes()
        {
            return _boxes.Select(box => GetPhotoFitMode(box.Id)).ToList();
        }

        public IList<bool> GetSlotPictureBorders()
        {
            return _boxes.Select(box => GetPictureBorder(box.Id)).ToList();
        }

        public bool HasSelectedPhoto
        {
            get { return _selectedBoxes.Any(box => _assignments.ContainsKey(box.Id)); }
        }

        public string SelectedPhotoFitMode
        {
            get
            {
                var modes = _selectedBoxes.Where(box => _assignments.ContainsKey(box.Id))
                    .Select(box => GetPhotoFitMode(box.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return modes.Count == 1 ? modes[0] : null;
            }
        }

        public bool? SelectedPhotoPrintsBorder
        {
            get
            {
                var choices = _selectedBoxes.Where(box => _assignments.ContainsKey(box.Id))
                    .Select(box => GetPictureBorder(box.Id)).Distinct().ToList();
                return choices.Count == 1 ? (bool?)choices[0] : null;
            }
        }

        public bool SetSelectedPhotoFitMode(string mode)
        {
            if (!HasSelectedPhoto) return false;
            var normalized = NormalizePhotoFitMode(mode);
            foreach (var box in _selectedBoxes.Where(box => _assignments.ContainsKey(box.Id)))
                _photoFitModes[box.Id] = normalized;
            InvalidateVisual();
            return true;
        }

        public void SetAllPhotoFitModes(string mode)
        {
            PhotoFitMode = NormalizePhotoFitMode(mode);
            foreach (var boxId in _assignments.Keys.ToList())
                _photoFitModes[boxId] = PhotoFitMode;
            InvalidateVisual();
        }

        public bool SetSelectedPhotoPictureBorder(bool printBorder)
        {
            if (!HasSelectedPhoto) return false;
            foreach (var box in _selectedBoxes.Where(box => _assignments.ContainsKey(box.Id)))
                _pictureBorders[box.Id] = printBorder;
            InvalidateVisual();
            return true;
        }

        public void SetAllPhotoPictureBorders(bool printBorder)
        {
            foreach (var boxId in _assignments.Keys.ToList())
                _pictureBorders[boxId] = printBorder;
            InvalidateVisual();
        }

        public int AssignedCount
        {
            get { return _assignments.Count; }
        }

        public int AssignPhotoToAll(string path)
        {
            if (!IsSupportedImage(path) || _boxes.Count == 0) return 0;
            foreach (var box in _boxes)
            {
                _assignments[box.Id] = path;
                _photoOffsets[box.Id] = new Point();
                _photoFitModes[box.Id] = PhotoFitMode;
                _pictureBorders[box.Id] = _package != null && _package.PrintPictureBorder;
            }
            LoadImage(path);
            ApplySinglePhotoLayout(path);
            InvalidateVisual();
            if (PhotoAssigned != null) PhotoAssigned(this, EventArgs.Empty);
            return _boxes.Count;
        }

        public int AssignPhotosToBoxes(IEnumerable<string> paths)
        {
            var selectedPaths = (paths ?? Enumerable.Empty<string>()).Where(IsSupportedImage).ToList();
            if (selectedPaths.Count == 0 || _boxes.Count == 0) return 0;

            for (var index = 0; index < _boxes.Count; index++)
            {
                var path = selectedPaths.Count == 1
                    ? selectedPaths[0]
                    : selectedPaths[index % selectedPaths.Count];
                _assignments[_boxes[index].Id] = path;
                _photoOffsets[_boxes[index].Id] = new Point();
                _photoFitModes[_boxes[index].Id] = PhotoFitMode;
                _pictureBorders[_boxes[index].Id] = _package != null && _package.PrintPictureBorder;
                LoadImage(path);
            }

            ApplySinglePhotoLayout(selectedPaths[0]);
            InvalidateVisual();
            if (PhotoAssigned != null) PhotoAssigned(this, EventArgs.Empty);
            return _boxes.Count;
        }

        private void BuildBoxes()
        {
            _boxes.Clear();
            _assignments.Clear();
            _photoOffsets.Clear();
            _photoFitModes.Clear();
            _pictureBorders.Clear();
            _selectedBox = null;
            _selectedBoxes.Clear();
            if (_package == null) return;

            var paper = PaperDefinition.Find(_package.PaperName);
            var landscape = _package.Orientation == "Landscape";
            _paperWidthMm = landscape ? Math.Max(paper.WidthMm, paper.HeightMm) : Math.Min(paper.WidthMm, paper.HeightMm);
            _paperHeightMm = landscape ? Math.Min(paper.WidthMm, paper.HeightMm) : Math.Max(paper.WidthMm, paper.HeightMm);

            if (_package.LayoutBoxes != null && _package.LayoutBoxes.Count > 0 && !IsRushId(_package))
            {
                foreach (var box in _package.LayoutBoxes)
                {
                    _boxes.Add(new PackageLayoutBox
                    {
                        Id = box.Id,
                        Label = box.Label,
                        Xmm = box.Xmm,
                        Ymm = box.Ymm,
                        WidthMm = box.WidthMm,
                        HeightMm = box.HeightMm,
                        RotationDegrees = box.RotationDegrees
                    });
                }
                return;
            }

            var layout = PrintLayoutEngine.Create(_package, paper, _package.Orientation,
                _package.PlacementMode, new List<string>(), 1, _package.Borderless);
            var index = 0;
            foreach (var placement in layout.Placements.Where(item => item.PageIndex == 0))
            {
                _boxes.Add(new PackageLayoutBox
                {
                    Id = "preview-" + index++,
                    Label = placement.Label,
                    Xmm = placement.BoundsMm.X,
                    Ymm = placement.BoundsMm.Y,
                    WidthMm = placement.BoundsMm.Width,
                    HeightMm = placement.BoundsMm.Height,
                    RotationDegrees = placement.RotationDegrees
                });
            }
        }

        private void ApplySinglePhotoLayout(string path)
        {
            if (!IsPhotoPackage(_package) || _boxes.Count != 1 || !IsSupportedImage(path)) return;
            var paper = PaperDefinition.Find(_package.PaperName);
            var layout = PrintLayoutEngine.Create(_package, paper, _package.Orientation,
                _package.PlacementMode, new List<string> { path }, 1, _package.Borderless);
            var placement = layout.Placements.FirstOrDefault();
            if (placement == null) return;

            var original = _boxes[0];
            _paperWidthMm = layout.PaperWidthMm;
            _paperHeightMm = layout.PaperHeightMm;
            _boxes[0] = new PackageLayoutBox
            {
                Id = original.Id,
                Label = placement.Label,
                Xmm = placement.BoundsMm.X,
                Ymm = placement.BoundsMm.Y,
                WidthMm = placement.BoundsMm.Width,
                HeightMm = placement.BoundsMm.Height,
                RotationDegrees = placement.RotationDegrees
            };
            if (ReferenceEquals(_selectedBox, original)) _selectedBox = _boxes[0];
            if (_selectedBoxes.Remove(original)) _selectedBoxes.Add(_boxes[0]);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(43, 50, 61)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));
            if (_package == null || _paperWidthMm <= 0 || _paperHeightMm <= 0)
            {
                DrawCenteredText(dc, "Select a package to preview");
                return;
            }

            var padding = CompactMode ? 5.0 : 28.0;
            _scale = Math.Min((ActualWidth - padding * 2) / _paperWidthMm,
                              (ActualHeight - padding * 2) / _paperHeightMm);
            if (double.IsNaN(_scale) || double.IsInfinity(_scale) || _scale <= 0) return;
            var pageWidth = _paperWidthMm * _scale;
            var pageHeight = _paperHeightMm * _scale;
            _pageRect = new Rect((ActualWidth - pageWidth) / 2, (ActualHeight - pageHeight) / 2,
                pageWidth, pageHeight);

            if (!CompactMode)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
                    new Rect(_pageRect.Left + 7, _pageRect.Top + 8, _pageRect.Width, _pageRect.Height));
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(150, 160, 175)), 1), _pageRect);
            dc.PushClip(new RectangleGeometry(_pageRect));
            foreach (var box in _boxes) DrawBox(dc, box);
            dc.Pop();
        }

        private void DrawBox(DrawingContext dc, PackageLayoutBox box)
        {
            var rect = BoxToScreen(box);
            dc.PushTransform(new RotateTransform(box.RotationDegrees, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
            string path;
            if (_assignments.TryGetValue(box.Id, out path) && File.Exists(path))
            {
                var source = LoadImage(path);
                if (source != null)
                {
                    source = OrientForBox(source, IsPhotoPackage(_package),
                        rect.Width, rect.Height);
                    Point offset;
                    if (!_photoOffsets.TryGetValue(box.Id, out offset)) offset = new Point();
                    var brush = string.Equals(GetPhotoFitMode(box.Id), "Stretch", StringComparison.OrdinalIgnoreCase)
                        ? new ImageBrush(source) { Stretch = Stretch.Fill }
                        : CreateCropBrush(source, rect, offset);
                    dc.DrawRectangle(brush,
                        new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 2), rect);
                    if (ShowPictureBorders && GetPictureBorder(box.Id))
                        dc.DrawRectangle(null, new Pen(Brushes.Black, 1.2), rect);
                }
                else DrawPlaceholder(dc, box, rect, CompactMode);
            }
            else DrawPlaceholder(dc, box, rect, CompactMode);
            if (AllowPhotoReposition && _selectedBoxes.Contains(box) && !CompactMode)
            {
                var selectionRect = rect;
                selectionRect.Inflate(3, 3);
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(96, 165, 250)), 2),
                    selectionRect);
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

        private static bool IsPhotoPackage(PhotoPackage package)
        {
            return package != null &&
                   (string.Equals(package.Category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(package.Category, "Custom", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(package.CustomKind, "Photo", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool IsRushId(PhotoPackage package)
        {
            return package != null &&
                   string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase);
        }

        private static double ClampOffset(double value)
        {
            return Math.Max(-1, Math.Min(1, value));
        }

        private void DrawPlaceholder(DrawingContext dc, PackageLayoutBox box, Rect rect, bool compactMode = false)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 240, 248)),
                new Pen(new SolidColorBrush(Color.FromRgb(91, 106, 128)), 1), rect);
            if (compactMode || rect.Width < 12 || rect.Height < 10) return;

            const string label = "Picture";
            var unit = _package == null || string.IsNullOrWhiteSpace(_package.MeasurementUnit)
                ? "mm"
                : _package.MeasurementUnit;
            var width = unit == "in" ? box.WidthMm / 25.4 : unit == "cm" ? box.WidthMm / 10.0 : box.WidthMm;
            var height = unit == "in" ? box.HeightMm / 25.4 : unit == "cm" ? box.HeightMm / 10.0 : box.HeightMm;
            var size = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                "{0:0.##} × {1:0.##} {2}", width, height, unit);
            var value = label + "\n" + size;
            var availableWidth = Math.Max(1, rect.Width - 4);
            var availableHeight = Math.Max(1, rect.Height - 4);
            FormattedText text = null;

            for (var fontSize = 9.0; fontSize >= 3.0; fontSize -= 0.5)
            {
                text = new FormattedText(value,
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), fontSize,
                    new SolidColorBrush(Color.FromRgb(37, 48, 66)))
                {
                    TextAlignment = TextAlignment.Center
                };
                var naturalWidthFits = text.WidthIncludingTrailingWhitespace <= availableWidth;
                text.MaxTextWidth = availableWidth;
                if (text.Height <= availableHeight && naturalWidthFits) break;
            }

            if (text == null) return;
            text.MaxTextHeight = availableHeight;
            text.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(text, new Point(rect.Left + 2,
                rect.Top + Math.Max(2, (rect.Height - text.Height) / 2)));
        }

        private Rect BoxToScreen(PackageLayoutBox box)
        {
            return new Rect(_pageRect.Left + box.Xmm * _scale, _pageRect.Top + box.Ymm * _scale,
                box.WidthMm * _scale, box.HeightMm * _scale);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            var path = GetDraggedPath(e.Data);
            e.Effects = path != null && HitTestBox(e.GetPosition(this)) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        protected override void OnDrop(DragEventArgs e)
        {
            base.OnDrop(e);
            var path = GetDraggedPath(e.Data);
            var box = HitTestBox(e.GetPosition(this));
            if (path == null || box == null) return;
            _assignments[box.Id] = path;
            _photoOffsets[box.Id] = new Point();
            _photoFitModes[box.Id] = PhotoFitMode;
            _pictureBorders[box.Id] = _package != null && _package.PrintPictureBorder;
            ApplySinglePhotoLayout(path);
            _selectedBoxes.Clear();
            _selectedBox = AllowPhotoReposition
                ? _boxes.FirstOrDefault(item => item.Id == box.Id)
                : null;
            if (_selectedBox != null) _selectedBoxes.Add(_selectedBox);
            InvalidateVisual();
            OnSelectedPhotoChanged();
            if (PhotoAssigned != null) PhotoAssigned(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!AllowPhotoReposition)
            {
                _selectedBox = null;
                _selectedBoxes.Clear();
                _panningPhoto = false;
                return;
            }
            Focus();
            var point = e.GetPosition(this);
            var clickedBox = HitTestBox(point);
            if (clickedBox != null && !_assignments.ContainsKey(clickedBox.Id)) clickedBox = null;
            var extendSelection = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            SelectBox(clickedBox, extendSelection);
            _panningPhoto = false;
            if (_selectedBox != null && !extendSelection)
            {
                _panStart = point;
                if (!_photoOffsets.TryGetValue(_selectedBox.Id, out _panStartOffset))
                    _panStartOffset = new Point();
                _panningPhoto = true;
                CaptureMouse();
                Cursor = Cursors.SizeAll;
            }
            InvalidateVisual();
            OnSelectedPhotoChanged();
            e.Handled = true;
        }

        private void SelectBox(PackageLayoutBox box, bool extendSelection)
        {
            if (!extendSelection) _selectedBoxes.Clear();
            if (box == null)
            {
                _selectedBox = _selectedBoxes.LastOrDefault();
                return;
            }

            if (extendSelection && _selectedBoxes.Contains(box))
            {
                _selectedBoxes.Remove(box);
                _selectedBox = _selectedBoxes.LastOrDefault();
            }
            else
            {
                _selectedBoxes.Add(box);
                _selectedBox = box;
            }
        }

        private string GetPhotoFitMode(string boxId)
        {
            string mode;
            return _photoFitModes.TryGetValue(boxId, out mode)
                ? NormalizePhotoFitMode(mode)
                : PhotoFitMode;
        }

        private bool GetPictureBorder(string boxId)
        {
            bool printBorder;
            return _pictureBorders.TryGetValue(boxId, out printBorder)
                ? printBorder
                : _package != null && _package.PrintPictureBorder;
        }

        private static string NormalizePhotoFitMode(string mode)
        {
            return string.Equals(mode, "Stretch", StringComparison.OrdinalIgnoreCase)
                ? "Stretch"
                : "Crop";
        }

        private void OnSelectedPhotoChanged()
        {
            if (SelectedPhotoChanged != null) SelectedPhotoChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!AllowPhotoReposition) return;
            if (!_panningPhoto || _selectedBox == null || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this);
            var rect = BoxToScreen(_selectedBox);
            _photoOffsets[_selectedBox.Id] = new Point(
                ClampOffset(_panStartOffset.X - (point.X - _panStart.X) * 2 / Math.Max(1, rect.Width)),
                ClampOffset(_panStartOffset.Y - (point.Y - _panStart.Y) * 2 / Math.Max(1, rect.Height)));
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
                _selectedBoxes.Clear();
                foreach (var box in _boxes.Where(box => _assignments.ContainsKey(box.Id)))
                    _selectedBoxes.Add(box);
                _selectedBox = _selectedBoxes.LastOrDefault();
                _panningPhoto = false;
                InvalidateVisual();
                OnSelectedPhotoChanged();
                e.Handled = true;
                return;
            }

            if (_selectedBox == null || !_assignments.ContainsKey(_selectedBox.Id)) return;
            Point offset;
            if (!_photoOffsets.TryGetValue(_selectedBox.Id, out offset)) offset = new Point();
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 0.01 : 0.05;
            switch (e.Key)
            {
                case Key.Left: offset.X += step; break;
                case Key.Right: offset.X -= step; break;
                case Key.Up: offset.Y += step; break;
                case Key.Down: offset.Y -= step; break;
                default: return;
            }
            _photoOffsets[_selectedBox.Id] = new Point(ClampOffset(offset.X), ClampOffset(offset.Y));
            InvalidateVisual();
            e.Handled = true;
        }

        private PackageLayoutBox HitTestBox(Point point)
        {
            for (var i = _boxes.Count - 1; i >= 0; i--)
            {
                var rect = BoxToScreen(_boxes[i]);
                if (rect.Contains(UnrotatePoint(point, rect, _boxes[i].RotationDegrees))) return _boxes[i];
            }
            return null;
        }

        private static Point UnrotatePoint(Point point, Rect rect, double degrees)
        {
            if (Math.Abs(degrees) < 0.001) return point;
            var centerX = rect.Left + rect.Width / 2;
            var centerY = rect.Top + rect.Height / 2;
            var radians = -degrees * Math.PI / 180.0;
            var dx = point.X - centerX;
            var dy = point.Y - centerY;
            return new Point(centerX + dx * Math.Cos(radians) - dy * Math.Sin(radians),
                centerY + dx * Math.Sin(radians) + dy * Math.Cos(radians));
        }

        private static string GetDraggedPath(IDataObject data)
        {
            if (data.GetDataPresent(PhotoPathFormat)) return data.GetData(PhotoPathFormat) as string;
            var files = data.GetData(DataFormats.FileDrop) as string[];
            return files == null ? null : files.FirstOrDefault(IsSupportedImage);
        }

        private static bool IsSupportedImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var extension = Path.GetExtension(path);
            return new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private ImageSource LoadImage(string path)
        {
            ImageSource source;
            if (_images.TryGetValue(path, out source)) return source;
            var bitmap = ImageLoadingService.Load(path, 900) as BitmapSource;
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

        private static void DrawCenteredText(DrawingContext dc, string value)
        {
            var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.White);
            dc.DrawText(text, new Point(20, 20));
        }
    }
}
