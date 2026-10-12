using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Views
{
    public partial class PackageEditorWindow : Window
    {
        private bool _designerReady;
        private bool _updatingInspector;
        private readonly bool _isNew;
        private double _previewZoom = 1.0;

        public PhotoPackage Package { get; private set; }

        public PackageEditorWindow(PhotoPackage package, bool isNew)
        {
            InitializeComponent();
            WindowSizingService.Maximize(this);
            _isNew = isNew;
            Package = package;
            if (isNew)
            {
                Package.Category = "Photo";
                Package.CustomKind = "Photo";
            }
            else if (Package.Category == "Custom")
                Package.Category = string.Equals(Package.CustomKind, "Photo", StringComparison.OrdinalIgnoreCase) ? "Photo" : "Rush ID";
            if (Package.Items == null) Package.Items = new ObservableCollection<PhotoSizeItem>();
            if (Package.LayoutBoxes == null) Package.LayoutBoxes = new ObservableCollection<PackageLayoutBox>();
            DataContext = Package;
            HeadingText.Text = isNew ? "Create Photo package" : "Edit package";
            OrientationBox.ItemsSource = new[] { "Portrait", "Landscape", "Automatic" };
            PaperBox.ItemsSource = PaperDefinition.StandardPapers();
            MeasurementUnitBox.ItemsSource = new[] { "Millimetres (mm)", "Centimetres (cm)", "Inches (in)" };
            if (string.IsNullOrWhiteSpace(Package.MeasurementUnit)) Package.MeasurementUnit = "in";
            MeasurementUnitBox.SelectedIndex = Package.MeasurementUnit == "in" ? 2 : Package.MeasurementUnit == "cm" ? 1 : 0;

            EnsureLayoutBoxes(isNew);
            LayoutDesigner.Items = Package.LayoutBoxes;
            _designerReady = true;
            UpdateDesignerPaper();
            SetPreviewZoom(1.0);
            LayoutDesigner.SelectedBox = Package.LayoutBoxes.FirstOrDefault();
            UpdateInspector();
        }

        private void EnsureLayoutBoxes(bool isNew)
        {
            if (Package.LayoutBoxes.Count > 0) return;
            var paper = PaperDefinition.Find(Package.PaperName);
            var landscape = Package.Orientation == "Landscape";
            var pageWidth = landscape ? Math.Max(paper.WidthMm, paper.HeightMm) : Math.Min(paper.WidthMm, paper.HeightMm);
            var pageHeight = landscape ? Math.Min(paper.WidthMm, paper.HeightMm) : Math.Max(paper.WidthMm, paper.HeightMm);

            if (!isNew && Package.Items.Count > 0)
            {
                var preview = PrintLayoutEngine.Create(Package, paper, Package.Orientation,
                    Package.PlacementMode, new List<string>(), 1, Package.Borderless);
                foreach (var placement in preview.Placements.Where(p => p.PageIndex == 0))
                {
                    Package.LayoutBoxes.Add(new PackageLayoutBox
                    {
                        Label = placement.Label,
                        Xmm = placement.BoundsMm.X,
                        Ymm = placement.BoundsMm.Y,
                        WidthMm = placement.BoundsMm.Width,
                        HeightMm = placement.BoundsMm.Height
                    });
                }
            }

            if (Package.LayoutBoxes.Count == 0)
            {
                Package.LayoutBoxes.Add(new PackageLayoutBox
                {
                    Label = "Picture 1",
                    Xmm = 5,
                    Ymm = 5,
                    WidthMm = Math.Min(50.8, pageWidth - 10),
                    HeightMm = Math.Min(50.8, pageHeight - 10)
                });
            }
        }

        private void AddPictureBox_Click(object sender, RoutedEventArgs e)
        {
            var width = Math.Min(50.8, Math.Max(5, LayoutDesigner.PaperWidthMm - 10));
            var height = Math.Min(50.8, Math.Max(5, LayoutDesigner.PaperHeightMm - 10));
            var offset = Math.Min(5 + Package.LayoutBoxes.Count * 4, 30);
            var box = new PackageLayoutBox
            {
                Label = "Picture " + (Package.LayoutBoxes.Count + 1),
                Xmm = Math.Min(offset, Math.Max(0, LayoutDesigner.PaperWidthMm - width)),
                Ymm = Math.Min(offset, Math.Max(0, LayoutDesigner.PaperHeightMm - height)),
                WidthMm = width,
                HeightMm = height
            };
            Package.LayoutBoxes.Add(box);
            LayoutDesigner.SelectedBox = box;
            UpdateDesignerSummary();
        }

        private void DuplicatePictureBox_Click(object sender, RoutedEventArgs e)
        {
            var source = LayoutDesigner.SelectedBox;
            if (source == null) return;
            var copy = source.Clone();
            copy.Label = source.Label + " Copy";
            copy.Xmm = Math.Min(source.Xmm + 5, Math.Max(0, LayoutDesigner.PaperWidthMm - source.WidthMm));
            copy.Ymm = Math.Min(source.Ymm + 5, Math.Max(0, LayoutDesigner.PaperHeightMm - source.HeightMm));
            Package.LayoutBoxes.Add(copy);
            LayoutDesigner.SelectedBox = copy;
            UpdateDesignerSummary();
        }

        private void RemovePictureBox_Click(object sender, RoutedEventArgs e)
        {
            var selected = LayoutDesigner.SelectedBox;
            if (selected == null) return;
            var index = Package.LayoutBoxes.IndexOf(selected);
            Package.LayoutBoxes.Remove(selected);
            LayoutDesigner.SelectedBox = Package.LayoutBoxes.Count == 0
                ? null
                : Package.LayoutBoxes[Math.Max(0, Math.Min(index, Package.LayoutBoxes.Count - 1))];
            UpdateDesignerSummary();
        }

        private void PaperSetting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_designerReady) return;
            UpdateDesignerPaper();
        }

        private void DesignerOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!_designerReady) return;
            LayoutDesigner.ShowRulers = RulerGuideToggle.IsChecked == true;
            LayoutDesigner.SnapToGrid = SnapGridToggle.IsChecked == true;
            LayoutDesigner.RefreshLayout();
        }

        private void MeasurementUnit_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_designerReady) return;
            Package.MeasurementUnit = GetMeasurementUnit();
            LayoutDesigner.MeasurementUnit = Package.MeasurementUnit;
            UpdateMeasurementLabels();
            UpdateInspector();
            UpdateDesignerSummary();
            LayoutDesigner.RefreshLayout();
        }

        private string GetMeasurementUnit()
        {
            return MeasurementUnitBox.SelectedIndex == 2 ? "in" : MeasurementUnitBox.SelectedIndex == 1 ? "cm" : "mm";
        }

        private double FromMillimetres(double value)
        {
            var unit = GetMeasurementUnit();
            return unit == "in" ? value / 25.4 : unit == "cm" ? value / 10.0 : value;
        }

        private double ToMillimetres(double value)
        {
            var unit = GetMeasurementUnit();
            return unit == "in" ? value * 25.4 : unit == "cm" ? value * 10.0 : value;
        }

        private string MeasurementSuffix()
        {
            return GetMeasurementUnit();
        }

        private void UpdateMeasurementLabels()
        {
            if (BoxXLabel == null) return;
            var suffix = MeasurementSuffix();
            BoxXLabel.Text = "X position (" + suffix + ")";
            BoxYLabel.Text = "Y position (" + suffix + ")";
            BoxWidthLabel.Text = "Width (" + suffix + ")";
            BoxHeightLabel.Text = "Height (" + suffix + ")";
            var unitName = suffix == "in" ? "inches" : suffix == "cm" ? "centimetres" : "millimetres";
            MeasurementHelpText.Text = "All values are exact " + unitName + ". Boxes must remain inside the selected paper.";
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            SetPreviewZoom(_previewZoom + 0.25);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            SetPreviewZoom(_previewZoom - 0.25);
        }

        private void DesignerViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            SetPreviewZoom(_previewZoom + (e.Delta > 0 ? 0.25 : -0.25));
            e.Handled = true;
        }

        private void DesignerViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateDesignerViewportSize();
        }

        private void SetPreviewZoom(double zoom)
        {
            _previewZoom = Math.Max(0.5, Math.Min(3.0, zoom));
            LayoutDesigner.LayoutTransform = new ScaleTransform(_previewZoom, _previewZoom);
            ZoomText.Text = Math.Round(_previewZoom * 100).ToString(CultureInfo.CurrentCulture) + "%";
            UpdateDesignerViewportSize();
            if (_designerReady) UpdateDesignerSummary();
        }

        private void UpdateDesignerViewportSize()
        {
            if (DesignerViewport == null || LayoutDesigner == null) return;
            LayoutDesigner.Width = Math.Max(260, DesignerViewport.ActualWidth - 24);
            LayoutDesigner.Height = Math.Max(200, DesignerViewport.ActualHeight - 24);
        }

        private void UpdateDesignerPaper()
        {
            if (!_designerReady) return;
            var paper = PaperBox.SelectedItem as PaperDefinition ?? PaperDefinition.Find(Package.PaperName);
            var orientation = Convert.ToString(OrientationBox.SelectedItem) ?? Package.Orientation ?? "Portrait";
            var landscape = orientation == "Landscape";
            LayoutDesigner.PaperWidthMm = landscape ? Math.Max(paper.WidthMm, paper.HeightMm) : Math.Min(paper.WidthMm, paper.HeightMm);
            LayoutDesigner.PaperHeightMm = landscape ? Math.Min(paper.WidthMm, paper.HeightMm) : Math.Max(paper.WidthMm, paper.HeightMm);
            LayoutDesigner.ShowRulers = RulerGuideToggle.IsChecked == true;
            LayoutDesigner.SnapToGrid = SnapGridToggle.IsChecked == true;
            LayoutDesigner.MeasurementUnit = GetMeasurementUnit();
            ClampBoxesToPaper();
            LayoutDesigner.RefreshLayout();
            UpdateMeasurementLabels();
            UpdateInspector();
            UpdateDesignerSummary();
        }

        private void ClampBoxesToPaper()
        {
            foreach (var box in Package.LayoutBoxes)
            {
                box.WidthMm = Math.Max(5, Math.Min(box.WidthMm, LayoutDesigner.PaperWidthMm));
                box.HeightMm = Math.Max(5, Math.Min(box.HeightMm, LayoutDesigner.PaperHeightMm));
                box.Xmm = Math.Max(0, Math.Min(box.Xmm, LayoutDesigner.PaperWidthMm - box.WidthMm));
                box.Ymm = Math.Max(0, Math.Min(box.Ymm, LayoutDesigner.PaperHeightMm - box.HeightMm));
            }
        }

        private void LayoutDesigner_SelectedBoxChanged(object sender, EventArgs e)
        {
            UpdateInspector();
        }

        private void LayoutDesigner_LayoutBoxChanged(object sender, EventArgs e)
        {
            UpdateInspector();
            UpdateDesignerSummary();
        }

        private void UpdateInspector()
        {
            if (BoxLabelBox == null) return;
            _updatingInspector = true;
            var box = LayoutDesigner.SelectedBox;
            var enabled = box != null;
            BoxLabelBox.IsEnabled = enabled;
            BoxXBox.IsEnabled = enabled;
            BoxYBox.IsEnabled = enabled;
            BoxWidthBox.IsEnabled = enabled;
            BoxHeightBox.IsEnabled = enabled;
            BoxRotationBox.IsEnabled = enabled;
            RotationSlider.IsEnabled = enabled;
            SelectionHint.Text = enabled ? "Edit its exact position and size" : "Select a box in the preview";
            BoxLabelBox.Text = enabled ? box.Label : "";
            BoxXBox.Text = enabled ? FromMillimetres(box.Xmm).ToString("0.###", CultureInfo.CurrentCulture) : "";
            BoxYBox.Text = enabled ? FromMillimetres(box.Ymm).ToString("0.###", CultureInfo.CurrentCulture) : "";
            BoxWidthBox.Text = enabled ? FromMillimetres(box.WidthMm).ToString("0.###", CultureInfo.CurrentCulture) : "";
            BoxHeightBox.Text = enabled ? FromMillimetres(box.HeightMm).ToString("0.###", CultureInfo.CurrentCulture) : "";
            BoxRotationBox.Text = enabled ? DisplayRotation(box.RotationDegrees).ToString("0.##", CultureInfo.CurrentCulture) : "";
            RotationSlider.Value = enabled ? DisplayRotation(box.RotationDegrees) : 0;
            _updatingInspector = false;
        }

        private void BoxLabel_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingInspector || LayoutDesigner.SelectedBox == null) return;
            LayoutDesigner.SelectedBox.Label = BoxLabelBox.Text;
            LayoutDesigner.RefreshLayout();
        }

        private void BoxValue_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyInspectorValues();
        }

        private void BoxValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyInspectorValues();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingInspector || LayoutDesigner == null || LayoutDesigner.SelectedBox == null) return;
            LayoutDesigner.SelectedBox.RotationDegrees = NormalizeRotation(e.NewValue);
            _updatingInspector = true;
            BoxRotationBox.Text = e.NewValue.ToString("0.##", CultureInfo.CurrentCulture);
            _updatingInspector = false;
            LayoutDesigner.RefreshLayout();
        }

        private void RotateClockwise_Click(object sender, RoutedEventArgs e)
        {
            RotateSelectedBox(90);
        }

        private void RotateCounterClockwise_Click(object sender, RoutedEventArgs e)
        {
            RotateSelectedBox(-90);
        }

        private void RotateSelectedBox(double degrees)
        {
            var box = LayoutDesigner.SelectedBox;
            if (box == null) return;
            box.RotationDegrees = NormalizeRotation(box.RotationDegrees + degrees);
            LayoutDesigner.RefreshLayout();
            UpdateInspector();
        }

        private void ApplyInspectorValues()
        {
            if (_updatingInspector || LayoutDesigner.SelectedBox == null) return;
            double x, y, width, height, rotation;
            if (!TryReadNumber(BoxXBox, out x) || !TryReadNumber(BoxYBox, out y) ||
                !TryReadNumber(BoxWidthBox, out width) || !TryReadNumber(BoxHeightBox, out height) ||
                !TryReadNumber(BoxRotationBox, out rotation))
            {
                UpdateInspector();
                return;
            }

            var box = LayoutDesigner.SelectedBox;
            x = ToMillimetres(x);
            y = ToMillimetres(y);
            width = ToMillimetres(width);
            height = ToMillimetres(height);
            box.WidthMm = Math.Max(5, Math.Min(width, LayoutDesigner.PaperWidthMm));
            box.HeightMm = Math.Max(5, Math.Min(height, LayoutDesigner.PaperHeightMm));
            box.Xmm = Math.Max(0, Math.Min(x, LayoutDesigner.PaperWidthMm - box.WidthMm));
            box.Ymm = Math.Max(0, Math.Min(y, LayoutDesigner.PaperHeightMm - box.HeightMm));
            box.RotationDegrees = NormalizeRotation(rotation);
            LayoutDesigner.RefreshLayout();
            UpdateInspector();
        }

        private static double NormalizeRotation(double degrees)
        {
            degrees %= 360;
            if (degrees < 0) degrees += 360;
            return degrees;
        }

        private static double DisplayRotation(double degrees)
        {
            var normalized = NormalizeRotation(degrees);
            return normalized > 180 ? normalized - 360 : normalized;
        }

        private static bool TryReadNumber(TextBox textBox, out double value)
        {
            return double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void UpdateDesignerSummary()
        {
            if (LayoutDesignerSummary == null) return;
            var paperWidth = FromMillimetres(LayoutDesigner.PaperWidthMm);
            var paperHeight = FromMillimetres(LayoutDesigner.PaperHeightMm);
            LayoutDesignerSummary.Text = string.Format(CultureInfo.CurrentCulture,
                "{0:0.###} x {1:0.###} {2} paper - {3} picture box(es) - Zoom {4:0}%",
                paperWidth, paperHeight, MeasurementSuffix(), Package.LayoutBoxes.Count, _previewZoom * 100);
        }

        private void SyncSizeItemsFromLayout()
        {
            Package.Items.Clear();
            foreach (var box in Package.LayoutBoxes)
            {
                Package.Items.Add(new PhotoSizeItem
                {
                    Label = string.IsNullOrWhiteSpace(box.Label) ? "Picture" : box.Label,
                    WidthMm = box.WidthMm,
                    HeightMm = box.HeightMm,
                    Quantity = 1
                });
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ApplyInspectorValues();
            if (_isNew)
            {
                Package.Category = "Photo";
                Package.CustomKind = "Photo";
            }
            if (string.IsNullOrWhiteSpace(Package.Name))
            {
                MessageBox.Show("Enter a package name.", "Package Designer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (Package.LayoutBoxes == null || Package.LayoutBoxes.Count == 0)
            {
                MessageBox.Show("Add at least one picture box to the layout.", "Package Designer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (Package.LayoutBoxes.Any(box => box.WidthMm <= 0 || box.HeightMm <= 0 || box.Xmm < 0 || box.Ymm < 0 ||
                box.Xmm + box.WidthMm > LayoutDesigner.PaperWidthMm + 0.01 ||
                box.Ymm + box.HeightMm > LayoutDesigner.PaperHeightMm + 0.01))
            {
                MessageBox.Show("Every picture box must have a valid size and remain inside the paper.",
                    "Package Designer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SyncSizeItemsFromLayout();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
