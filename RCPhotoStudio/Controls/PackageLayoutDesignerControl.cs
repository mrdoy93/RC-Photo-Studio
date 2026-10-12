using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RCPhotoStudio.Models;

namespace RCPhotoStudio.Controls
{
    public class PackageLayoutDesignerControl : FrameworkElement
    {
        private ObservableCollection<PackageLayoutBox> _items;
        private PackageLayoutBox _selectedBox;
        private readonly HashSet<PackageLayoutBox> _selectedBoxes = new HashSet<PackageLayoutBox>();
        private readonly Dictionary<PackageLayoutBox, Point> _dragStartLocations = new Dictionary<PackageLayoutBox, Point>();
        private Rect _pageRect;
        private double _scale;
        private Point _dragStart;
        private double _startX;
        private double _startY;
        private double _startWidth;
        private double _startHeight;
        private bool _dragging;
        private bool _resizing;
        private bool _marqueeSelecting;
        private bool _marqueeAdditive;
        private Point _marqueeStart;
        private Point _marqueeCurrent;
        private bool _controlAtMouseDown;
        private bool _pressedBoxWasSelected;
        private bool _controlDragDuplicated;
        private PackageLayoutBox _pressedBox;
        private List<PackageLayoutBox> _controlDragSources = new List<PackageLayoutBox>();

        public event EventHandler SelectedBoxChanged;
        public event EventHandler LayoutBoxChanged;

        public PackageLayoutDesignerControl()
        {
            Focusable = true;
            MinHeight = 200;
            Cursor = Cursors.Arrow;
        }

        public ObservableCollection<PackageLayoutBox> Items
        {
            get { return _items; }
            set
            {
                if (_items != null) _items.CollectionChanged -= Items_CollectionChanged;
                _items = value;
                if (_items != null) _items.CollectionChanged += Items_CollectionChanged;
                if (_selectedBox != null && (_items == null || !_items.Contains(_selectedBox))) SelectedBox = null;
                InvalidateVisual();
            }
        }

        public PackageLayoutBox SelectedBox
        {
            get { return _selectedBox; }
            set
            {
                if (ReferenceEquals(_selectedBox, value) && _selectedBoxes.Count == (value == null ? 0 : 1)) return;
                _selectedBoxes.Clear();
                if (value != null) _selectedBoxes.Add(value);
                SetPrimarySelection(value);
            }
        }

        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public bool ShowRulers { get; set; }
        public bool SnapToGrid { get; set; }
        public string MeasurementUnit { get; set; }

        public void RefreshLayout()
        {
            InvalidateVisual();
        }

        private void Items_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _selectedBoxes.RemoveWhere(item => _items == null || !_items.Contains(item));
            if (_selectedBox != null && !_selectedBoxes.Contains(_selectedBox))
                SetPrimarySelection(_selectedBoxes.LastOrDefault());
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(43, 50, 61)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));

            if (PaperWidthMm <= 0 || PaperHeightMm <= 0) return;
            var padding = ShowRulers ? 42.0 : 18.0;
            _scale = Math.Min((ActualWidth - padding * 2) / PaperWidthMm,
                              (ActualHeight - padding * 2) / PaperHeightMm);
            if (double.IsNaN(_scale) || double.IsInfinity(_scale) || _scale <= 0) return;

            var width = PaperWidthMm * _scale;
            var height = PaperHeightMm * _scale;
            var left = (ActualWidth - width) / 2;
            var top = (ActualHeight - height) / 2;
            _pageRect = new Rect(left, top, width, height);

            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
                new Rect(left + 7, top + 9, width, height));
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(150, 160, 175)), 1), _pageRect);

            if (ShowRulers) DrawRulersAndGrid(dc);

            dc.PushClip(new RectangleGeometry(_pageRect));
            if (_items != null)
            {
                foreach (var item in _items)
                    DrawBox(dc, item, _selectedBoxes.Contains(item), ReferenceEquals(item, _selectedBox));
            }
            dc.Pop();

            if (_marqueeSelecting)
            {
                var marquee = CreateRect(_marqueeStart, _marqueeCurrent);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 1)
                {
                    DashStyle = DashStyles.Dash
                };
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 37, 99, 235)), pen, marquee);
            }
        }

        private void DrawRulersAndGrid(DrawingContext dc)
        {
            var minorGrid = new Pen(new SolidColorBrush(Color.FromArgb(36, 37, 99, 235)), 0.5);
            var majorGrid = new Pen(new SolidColorBrush(Color.FromArgb(72, 37, 99, 235)), 0.8);
            var tickPen = new Pen(new SolidColorBrush(Color.FromRgb(208, 216, 229)), 1);
            var labelBrush = new SolidColorBrush(Color.FromRgb(232, 237, 245));
            var minorStepMm = MeasurementUnit == "in" ? 6.35 : 5.0;
            var majorInterval = MeasurementUnit == "in" ? 4 : 2;

            var tick = 0;
            for (var mm = 0.0; mm <= PaperWidthMm + 0.01; mm += minorStepMm, tick++)
            {
                var x = _pageRect.Left + mm * _scale;
                var major = tick % majorInterval == 0;
                if (mm > 0 && mm < PaperWidthMm)
                    dc.DrawLine(major ? majorGrid : minorGrid, new Point(x, _pageRect.Top), new Point(x, _pageRect.Bottom));
                dc.DrawLine(tickPen, new Point(x, _pageRect.Top - (major ? 9 : 5)), new Point(x, _pageRect.Top));
                if (major && mm < PaperWidthMm)
                    DrawText(dc, UnitValue(mm).ToString(MeasurementUnit == "mm" ? "0" : "0.#", CultureInfo.CurrentCulture), 9, labelBrush,
                        new Point(x + 2, _pageRect.Top - 24), 34, TextAlignment.Left);
            }

            tick = 0;
            for (var mm = 0.0; mm <= PaperHeightMm + 0.01; mm += minorStepMm, tick++)
            {
                var y = _pageRect.Top + mm * _scale;
                var major = tick % majorInterval == 0;
                if (mm > 0 && mm < PaperHeightMm)
                    dc.DrawLine(major ? majorGrid : minorGrid, new Point(_pageRect.Left, y), new Point(_pageRect.Right, y));
                dc.DrawLine(tickPen, new Point(_pageRect.Left - (major ? 9 : 5), y), new Point(_pageRect.Left, y));
                if (major && mm < PaperHeightMm)
                    DrawText(dc, UnitValue(mm).ToString(MeasurementUnit == "mm" ? "0" : "0.#", CultureInfo.CurrentCulture), 9, labelBrush,
                        new Point(_pageRect.Left - 38, y - 6), 27, TextAlignment.Right);
            }
        }

        private void DrawBox(DrawingContext dc, PackageLayoutBox item, bool selected, bool primary)
        {
            var rect = BoxToScreen(item);
            dc.PushTransform(new RotateTransform(item.RotationDegrees, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
            var fill = selected
                ? new SolidColorBrush(Color.FromArgb(225, 219, 234, 254))
                : new SolidColorBrush(Color.FromArgb(225, 235, 240, 248));
            var border = new Pen(new SolidColorBrush(selected ? Color.FromRgb(37, 99, 235) : Color.FromRgb(91, 106, 128)),
                0.5);
            dc.DrawRectangle(fill, border, rect);

            var title = string.IsNullOrWhiteSpace(item.Label) ? "Picture" : item.Label;
            var value = string.Format(CultureInfo.CurrentCulture, "{0}\n{1:0.###} x {2:0.###} {3}",
                title, UnitValue(item.WidthMm), UnitValue(item.HeightMm), UnitSuffix());
            DrawAutoFitBoxText(dc, value, rect, new SolidColorBrush(Color.FromRgb(37, 48, 66)));

            if (primary)
            {
                var handle = ResizeHandle(rect);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(37, 99, 235)), new Pen(Brushes.White, 1), handle);
            }
            dc.Pop();
        }

        private static void DrawAutoFitBoxText(DrawingContext dc, string text, Rect rect, Brush brush)
        {
            var availableWidth = Math.Max(1, rect.Width - 8);
            var availableHeight = Math.Max(1, rect.Height - 8);
            FormattedText formatted = null;

            for (var fontSize = 13.0; fontSize >= 4.0; fontSize -= 0.5)
            {
                formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), fontSize, brush)
                {
                    MaxTextWidth = availableWidth,
                    TextAlignment = TextAlignment.Center
                };
                if (formatted.Height <= availableHeight) break;
            }

            if (formatted == null) return;
            formatted.MaxTextHeight = availableHeight;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(formatted, new Point(rect.Left + 4,
                rect.Top + Math.Max(4, (rect.Height - formatted.Height) / 2)));
        }

        private static void DrawText(DrawingContext dc, string text, double size, Brush brush, Point point,
            double maxWidth, TextAlignment alignment)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush);
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            formatted.TextAlignment = alignment;
            dc.DrawText(formatted, point);
        }

        private Rect BoxToScreen(PackageLayoutBox item)
        {
            return new Rect(_pageRect.Left + item.Xmm * _scale,
                _pageRect.Top + item.Ymm * _scale,
                item.WidthMm * _scale, item.HeightMm * _scale);
        }

        private static Rect ResizeHandle(Rect rect)
        {
            const double size = 11;
            return new Rect(rect.Right - size / 2, rect.Bottom - size / 2, size, size);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var point = e.GetPosition(this);
            var hit = HitTestBox(point);
            _controlAtMouseDown = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            _pressedBox = hit;
            _pressedBoxWasSelected = hit != null && _selectedBoxes.Contains(hit);
            _controlDragDuplicated = false;

            if (hit == null)
            {
                if (!_controlAtMouseDown) ClearSelection();
                _marqueeSelecting = true;
                _marqueeAdditive = _controlAtMouseDown;
                _marqueeStart = point;
                _marqueeCurrent = point;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_controlAtMouseDown)
            {
                _controlDragSources = _pressedBoxWasSelected
                    ? _selectedBoxes.ToList()
                    : new List<PackageLayoutBox> { hit };
                if (!_pressedBoxWasSelected) AddToSelection(hit);
                else SetPrimarySelection(hit);
            }
            else if (!_selectedBoxes.Contains(hit))
            {
                SelectOnly(hit);
            }
            else
            {
                SetPrimarySelection(hit);
            }

            _dragStart = point;
            _startX = hit.Xmm;
            _startY = hit.Ymm;
            _startWidth = hit.WidthMm;
            _startHeight = hit.HeightMm;
            _resizing = !_controlAtMouseDown && _selectedBoxes.Count == 1 && IsResizeHandleHit(hit, point);
            _dragging = true;
            CaptureDragStartLocations();
            CaptureMouse();
            Cursor = _resizing ? Cursors.SizeNWSE : Cursors.SizeAll;
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_marqueeSelecting)
            {
                _marqueeCurrent = e.GetPosition(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (!_dragging || _selectedBox == null || _scale <= 0) return;
            var point = e.GetPosition(this);
            var pixelDx = point.X - _dragStart.X;
            var pixelDy = point.Y - _dragStart.Y;

            if (_controlAtMouseDown && !_controlDragDuplicated)
            {
                if (Math.Abs(pixelDx) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pixelDy) < SystemParameters.MinimumVerticalDragDistance) return;
                DuplicateControlDragSelection();
            }

            var dx = (point.X - _dragStart.X) / _scale;
            var dy = (point.Y - _dragStart.Y) / _scale;

            if (_resizing)
            {
                var radians = -_selectedBox.RotationDegrees * Math.PI / 180.0;
                var screenDx = point.X - _dragStart.X;
                var screenDy = point.Y - _dragStart.Y;
                dx = (screenDx * Math.Cos(radians) - screenDy * Math.Sin(radians)) / _scale;
                dy = (screenDx * Math.Sin(radians) + screenDy * Math.Cos(radians)) / _scale;
                var maxWidth = Math.Max(5, PaperWidthMm - _selectedBox.Xmm);
                var maxHeight = Math.Max(5, PaperHeightMm - _selectedBox.Ymm);
                _selectedBox.WidthMm = Clamp(RoundValue(_startWidth + dx), 5, maxWidth);
                _selectedBox.HeightMm = Clamp(RoundValue(_startHeight + dy), 5, maxHeight);
            }
            else
            {
                MoveDragSelection(dx, dy);
            }

            InvalidateVisual();
            if (LayoutBoxChanged != null) LayoutBoxChanged(this, EventArgs.Empty);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_marqueeSelecting)
            {
                _marqueeCurrent = e.GetPosition(this);
                CompleteMarqueeSelection();
                _marqueeSelecting = false;
                ReleaseMouseCapture();
                Cursor = Cursors.Arrow;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (!_dragging) return;

            if (_controlAtMouseDown && !_controlDragDuplicated && _pressedBoxWasSelected)
                RemoveFromSelection(_pressedBox);
            _dragging = false;
            _resizing = false;
            _controlAtMouseDown = false;
            _controlDragDuplicated = false;
            _pressedBox = null;
            _controlDragSources.Clear();
            _dragStartLocations.Clear();
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }

        private void SelectOnly(PackageLayoutBox box)
        {
            _selectedBoxes.Clear();
            if (box != null) _selectedBoxes.Add(box);
            SetPrimarySelection(box);
        }

        private void AddToSelection(PackageLayoutBox box)
        {
            if (box == null) return;
            _selectedBoxes.Add(box);
            SetPrimarySelection(box);
        }

        private void RemoveFromSelection(PackageLayoutBox box)
        {
            if (box == null || !_selectedBoxes.Remove(box)) return;
            if (ReferenceEquals(_selectedBox, box)) SetPrimarySelection(_selectedBoxes.LastOrDefault());
            else InvalidateVisual();
        }

        private void ClearSelection()
        {
            _selectedBoxes.Clear();
            SetPrimarySelection(null);
        }

        private void SetPrimarySelection(PackageLayoutBox box)
        {
            var changed = !ReferenceEquals(_selectedBox, box);
            _selectedBox = box;
            InvalidateVisual();
            if (changed && SelectedBoxChanged != null) SelectedBoxChanged(this, EventArgs.Empty);
        }

        private void CaptureDragStartLocations()
        {
            _dragStartLocations.Clear();
            foreach (var box in _selectedBoxes)
                _dragStartLocations[box] = new Point(box.Xmm, box.Ymm);
        }

        private void DuplicateControlDragSelection()
        {
            if (_items == null || _controlDragSources.Count == 0) return;
            var copies = new List<PackageLayoutBox>();
            PackageLayoutBox primaryCopy = null;
            foreach (var source in _controlDragSources.Where(source => _items.Contains(source)))
            {
                var copy = source.Clone();
                _items.Add(copy);
                copies.Add(copy);
                if (ReferenceEquals(source, _pressedBox)) primaryCopy = copy;
            }
            if (copies.Count == 0) return;

            _selectedBoxes.Clear();
            foreach (var copy in copies) _selectedBoxes.Add(copy);
            SetPrimarySelection(primaryCopy ?? copies[copies.Count - 1]);
            CaptureDragStartLocations();
            _controlDragDuplicated = true;
        }

        private void MoveDragSelection(double dx, double dy)
        {
            if (_dragStartLocations.Count == 0) return;
            var minimumDx = _dragStartLocations.Max(item => -item.Value.X);
            var maximumDx = _dragStartLocations.Min(item =>
                PaperWidthMm - item.Value.X - item.Key.WidthMm);
            var minimumDy = _dragStartLocations.Max(item => -item.Value.Y);
            var maximumDy = _dragStartLocations.Min(item =>
                PaperHeightMm - item.Value.Y - item.Key.HeightMm);
            dx = Clamp(dx, minimumDx, maximumDx);
            dy = Clamp(dy, minimumDy, maximumDy);

            foreach (var item in _dragStartLocations)
            {
                item.Key.Xmm = Clamp(RoundValue(item.Value.X + dx), 0,
                    Math.Max(0, PaperWidthMm - item.Key.WidthMm));
                item.Key.Ymm = Clamp(RoundValue(item.Value.Y + dy), 0,
                    Math.Max(0, PaperHeightMm - item.Key.HeightMm));
            }
        }

        private void MoveSelectionBy(double dx, double dy)
        {
            CaptureDragStartLocations();
            MoveDragSelection(dx, dy);
            _dragStartLocations.Clear();
        }

        private void CompleteMarqueeSelection()
        {
            if (_items == null) return;
            var marquee = CreateRect(_marqueeStart, _marqueeCurrent);
            var hits = _items.Where(item => marquee.IntersectsWith(BoxToScreen(item))).ToList();
            if (!_marqueeAdditive) _selectedBoxes.Clear();
            foreach (var hit in hits) _selectedBoxes.Add(hit);
            SetPrimarySelection(hits.LastOrDefault() ?? _selectedBoxes.LastOrDefault());
        }

        private static Rect CreateRect(Point first, Point second)
        {
            return new Rect(new Point(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y)),
                new Point(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y)));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Delete)
            {
                DeleteSelectedBoxes();
                e.Handled = true;
                return;
            }
            if (_selectedBox == null) return;
            if (e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Up && e.Key != Key.Down) return;

            var step = SnapToGrid ? 1.0 : 0.1;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) step = 0.1;
            var resize = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (resize)
            {
                if (e.Key == Key.Left)
                    _selectedBox.WidthMm = Clamp(RoundValue(_selectedBox.WidthMm - step), 5,
                        Math.Max(5, PaperWidthMm - _selectedBox.Xmm));
                else if (e.Key == Key.Right)
                    _selectedBox.WidthMm = Clamp(RoundValue(_selectedBox.WidthMm + step), 5,
                        Math.Max(5, PaperWidthMm - _selectedBox.Xmm));
                else if (e.Key == Key.Up)
                    _selectedBox.HeightMm = Clamp(RoundValue(_selectedBox.HeightMm - step), 5,
                        Math.Max(5, PaperHeightMm - _selectedBox.Ymm));
                else if (e.Key == Key.Down)
                    _selectedBox.HeightMm = Clamp(RoundValue(_selectedBox.HeightMm + step), 5,
                        Math.Max(5, PaperHeightMm - _selectedBox.Ymm));
            }
            else
            {
                if (_selectedBoxes.Count == 0) _selectedBoxes.Add(_selectedBox);
                if (e.Key == Key.Left) MoveSelectionBy(-step, 0);
                else if (e.Key == Key.Right) MoveSelectionBy(step, 0);
                else if (e.Key == Key.Up) MoveSelectionBy(0, -step);
                else if (e.Key == Key.Down) MoveSelectionBy(0, step);
            }

            InvalidateVisual();
            if (LayoutBoxChanged != null) LayoutBoxChanged(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void DeleteSelectedBoxes()
        {
            if (_items == null || _selectedBoxes.Count == 0) return;
            var boxesToDelete = _selectedBoxes.Where(box => _items.Contains(box)).ToList();
            ClearSelection();
            foreach (var box in boxesToDelete) _items.Remove(box);
            if (LayoutBoxChanged != null) LayoutBoxChanged(this, EventArgs.Empty);
        }

        private PackageLayoutBox HitTestBox(Point point)
        {
            if (_items == null) return null;
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var rect = BoxToScreen(_items[i]);
                var localPoint = UnrotatePoint(point, rect, _items[i].RotationDegrees);
                if (rect.Contains(localPoint) ||
                    (ReferenceEquals(_items[i], _selectedBox) && ResizeHandle(rect).Contains(localPoint)))
                    return _items[i];
            }
            return null;
        }

        private bool IsResizeHandleHit(PackageLayoutBox item, Point point)
        {
            var rect = BoxToScreen(item);
            return ResizeHandle(rect).Contains(UnrotatePoint(point, rect, item.RotationDegrees));
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

        private double RoundValue(double value)
        {
            var step = SnapToGrid ? 1.0 : 0.1;
            return Math.Round(value / step) * step;
        }

        private double UnitValue(double millimetres)
        {
            return MeasurementUnit == "in" ? millimetres / 25.4 : MeasurementUnit == "cm" ? millimetres / 10.0 : millimetres;
        }

        private string UnitSuffix()
        {
            return MeasurementUnit == "in" ? "in" : MeasurementUnit == "cm" ? "cm" : "mm";
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
