using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Views
{
    public class PhotoFile
    {
        public PhotoFile()
        {
            CopyCount = 1;
            PackageCount = 1;
        }

        public string Path { get; set; }
        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        public int CopyCount { get; set; }
        public int PackageCount { get; set; }
        public System.Windows.Media.ImageSource PreviewSource
        {
            get { return ImageLoadingService.Load(Path, 240); }
        }
    }

    public class PagePreviewItem
    {
        public PrintLayout Layout { get; set; }
        public int PageIndex { get; set; }
        public string Label { get; set; }
        public bool ShowGrid { get; set; }
        public bool ShowCutGuides { get; set; }
        public bool ShowPictureBorders { get; set; }
    }

    public partial class PrintWorkspaceWindow : Window, INotifyPropertyChanged
    {
        private const float OutputPictureBorderWidthPixels = 0.10f;
        private const double PvcCardWidthMm = 2.14 * 25.4;
        private const double PvcCardHeightMm = 3.39 * 25.4;
        private const double PvcCardCornerRadiusMm = 3.2;
        private const double PvcPrintableMarginMm = 5.0;
        private const double PvcCardGapMm = 2.0;
        private const string SelectedPicturesScope = "Selected picture(s)";
        private const string AllPicturesScope = "All pictures";
        private const string NoPictureBorderOption = "Do not print";
        private const string PrintPictureBorderOption = "Print border (0.10 px)";
        private readonly PhotoPackage _package;
        private PrintLayout _layout;
        private int _pageIndex;
        private PrinterSettings _printerSettings;
        private bool _ready;
        private IList<string> _slotPhotoPaths;
        private readonly Action _saveSettings;
        private readonly bool _settingsMode;
        private readonly bool _forceMultiplePhotos;
        private readonly bool _pvcPrintingMode;
        private bool _updatingPageSidebar;
        private bool _updatingPhotoCopyControls;
        private bool _updatingSelectedPhotoControls;
        private bool _useSidebarSelection;

        public ObservableCollection<PhotoFile> Photos { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public double ThumbnailImageSize { get; private set; }
        public double ThumbnailTileWidth { get { return ThumbnailImageSize + 8; } }
        public double ThumbnailTileHeight { get { return ThumbnailImageSize + 36; } }

        public PrintWorkspaceWindow(PhotoPackage package)
            : this(package, null, null, null, false, false, false)
        {
        }

        public PrintWorkspaceWindow(PhotoPackage package, IEnumerable<string> initialPhotos, IList<string> slotPhotoPaths)
            : this(package, initialPhotos, slotPhotoPaths, null, false, false, false)
        {
        }

        public PrintWorkspaceWindow(PhotoPackage package, IEnumerable<string> initialPhotos, IList<string> slotPhotoPaths,
            Action saveSettings, bool settingsMode = false, bool forceMultiplePhotos = false,
            bool pvcPrintingMode = false)
        {
            ThumbnailImageSize = 120;
            InitializeComponent();
            WindowSizingService.Maximize(this);
            _package = package;
            _slotPhotoPaths = slotPhotoPaths;
            _saveSettings = saveSettings;
            _settingsMode = settingsMode;
            _forceMultiplePhotos = forceMultiplePhotos;
            _pvcPrintingMode = pvcPrintingMode;
            PaperPreview.AllowPhotoReposition = _pvcPrintingMode || (!_settingsMode && !IsRushId(package));
            PaperPreview.ShowEmptyPictureBoxes = !_pvcPrintingMode;
            Photos = new ObservableCollection<PhotoFile>();
            if (initialPhotos != null)
            {
                var allowed = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }, StringComparer.OrdinalIgnoreCase);
                foreach (var path in initialPhotos)
                    if (File.Exists(path) && allowed.Contains(System.IO.Path.GetExtension(path)))
                        Photos.Add(new PhotoFile { Path = path });
            }
            if (_pvcPrintingMode) SortPhotosByName();
            DataContext = this;
            PackageTitle.Text = _settingsMode ? "Printer Settings" : "PVC Printing";
            if (_settingsMode)
            {
                Title = "Printer Settings — RC Photo Studio";
                WorkspaceSubtitle.Text = "Configure layout, paper, and printer options. Changes are saved automatically.";
                PrintNowButton.Visibility = Visibility.Collapsed;
                AutoSaveNotice.Visibility = Visibility.Visible;
            }
            if (_pvcPrintingMode)
            {
                RightSidebarColumn.Width = new GridLength(0);
                RightSidebarPanel.Visibility = Visibility.Collapsed;
                SavePdfButton.Visibility = Visibility.Visible;
                PhotoEditScopePanel.Visibility = Visibility.Visible;
                DropHintText.Text = "Import an image first";
            }
            ThemeToggle.IsChecked = ThemeService.IsDark;
            ThemeToggle.Content = ThemeService.IsDark ? "☀  Light mode" : "🌙  Night mode";
            SelectedPhotoApplyScopeBox.ItemsSource = new[] { SelectedPicturesScope, AllPicturesScope };
            SelectedPhotoApplyScopeBox.SelectedItem = SelectedPicturesScope;
            SelectedPhotoFitBox.ItemsSource = new[] { "Crop", "Stretch" };
            SelectedPictureBorderBox.ItemsSource = new[] { NoPictureBorderOption, PrintPictureBorderOption };
            PlacementBox.ItemsSource = new[] { "Repeat One Photo", "Multiple Photos", "Fill Page" };
            OrientationBox.ItemsSource = new[] { "Portrait", "Landscape", "Automatic" };
            var papers = PaperDefinition.StandardPapers().ToList();
            PaperDefinition pvcPaper = null;
            if (_pvcPrintingMode)
            {
                pvcPaper = new PaperDefinition
                {
                    Name = "PVC 200 × 300 mm",
                    WidthMm = 200,
                    HeightMm = 300
                };
                papers.Insert(0, pvcPaper);
            }
            PaperBox.ItemsSource = papers;
            PlacementBox.SelectedItem = _forceMultiplePhotos || (slotPhotoPaths != null && !IsRushId(package))
                ? "Multiple Photos"
                : package.PlacementMode;
            OrientationBox.SelectedItem = _pvcPrintingMode ? "Portrait" : package.Orientation;
            PaperBox.SelectedItem = pvcPaper ?? papers.FirstOrDefault(p => p.Name == package.PaperName) ?? papers[0];
            AmountBox.Text = Math.Max(1, package.PrintAmount).ToString();
            GridToggle.IsChecked = package.ShowGrid;
            CutGuidesToggle.IsChecked = package.PrintCutGuides;
            PictureBorderToggle.Visibility = SupportsPictureBorders(package) ? Visibility.Visible : Visibility.Collapsed;
            PictureBorderToggle.IsChecked = SupportsPictureBorders(package) && package.PrintPictureBorder;
            BorderlessToggle.IsChecked = package.Borderless;
            DropHint.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadPrinters(package.DefaultPrinter);
            _ready = true;
            SaveCurrentSettings();
            RefreshLayout();
            UpdatePhotoCopyControls();
            UpdateSelectedPhotoControls();
            UpdateFillSelectedPicturesButton();
        }

        private void ThumbnailZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ThumbnailImageSize = e.NewValue;
            RaisePropertyChanged("ThumbnailImageSize");
            RaisePropertyChanged("ThumbnailTileWidth");
            RaisePropertyChanged("ThumbnailTileHeight");
        }

        private void RaisePropertyChanged(string propertyName)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }

        private void LoadPrinters(string preferred)
        {
            var names = new List<string>();
            foreach (string printer in PrinterSettings.InstalledPrinters) names.Add(printer);
            PrinterBox.ItemsSource = names;
            var selected = !string.IsNullOrWhiteSpace(preferred) && names.Contains(preferred) ? preferred : null;
            if (selected == null)
            {
                try
                {
                    var defaults = new PrinterSettings();
                    if (defaults.IsValid && names.Contains(defaults.PrinterName)) selected = defaults.PrinterName;
                }
                catch { }
            }
            PrinterBox.SelectedItem = selected ?? names.FirstOrDefault();
            var selectedName = Convert.ToString(PrinterBox.SelectedItem);
            _printerSettings = CreatePrinterSettings(_package, selectedName);
            UpdatePrinterStatus();
        }

        private void AddPhotos(IEnumerable<string> files)
        {
            _slotPhotoPaths = null;
            _useSidebarSelection = false;
            var allowed = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }, StringComparer.OrdinalIgnoreCase);
            var addedPhotos = new List<PhotoFile>();
            foreach (var file in files)
                if (File.Exists(file) && allowed.Contains(System.IO.Path.GetExtension(file)))
                {
                    var photo = new PhotoFile { Path = file };
                    Photos.Add(photo);
                    addedPhotos.Add(photo);
                }
            if (addedPhotos.Count > 0)
            {
                if (_pvcPrintingMode) SortPhotosByName();
                PhotoList.UnselectAll();
                foreach (var photo in addedPhotos) PhotoList.SelectedItems.Add(photo);
            }
            DropHint.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshLayout();
        }

        private void SortPhotosByName()
        {
            var sortedPhotos = Photos
                .OrderBy(photo => photo.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(photo => photo.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            for (var targetIndex = 0; targetIndex < sortedPhotos.Count; targetIndex++)
            {
                var currentIndex = Photos.IndexOf(sortedPhotos[targetIndex]);
                if (currentIndex != targetIndex) Photos.Move(currentIndex, targetIndex);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddPhotos(files);
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_pvcPrintingMode || PhotoList == null || PhotoList.SelectedItems.Count == 0) return;

            var source = e.OriginalSource as DependencyObject;
            if (source == null) return;

            // Keep normal thumbnail selection behavior when a photo itself is clicked.
            if (ItemsControl.ContainerFromElement(PhotoList, source) is ListBoxItem) return;

            // These controls operate on the current selection, so do not clear it before
            // their own click handlers have a chance to run.
            if (IsWithinElement(source, PhotoCopyMinusButton) ||
                IsWithinElement(source, PhotoCopyCountBox) ||
                IsWithinElement(source, PhotoCopyPlusButton) ||
                IsWithinElement(source, FillSelectedPicturesButton) ||
                IsWithinElement(source, RemovePhotosButton))
                return;

            PhotoList.UnselectAll();
        }

        private static bool IsWithinElement(DependencyObject source, DependencyObject ancestor)
        {
            var current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor)) return true;

                if (current is System.Windows.Media.Visual)
                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                else if (current is FrameworkContentElement)
                    current = ((FrameworkContentElement)current).Parent;
                else
                    current = LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        private void BrowsePhotos_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Add photos",
                Filter = "Photo files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true) AddPhotos(dialog.FileNames);
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            _slotPhotoPaths = null;
            _useSidebarSelection = false;
            foreach (var selected in GetSelectedPhotos().ToList()) Photos.Remove(selected);
            DropHint.Visibility = Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshLayout();
            UpdatePhotoCopyControls();
            UpdateFillSelectedPicturesButton();
        }

        private void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePhotoCopyControls();
            UpdateFillSelectedPicturesButton();
        }

        private void PhotoList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_pvcPrintingMode || e.Key != Key.A ||
                (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            PhotoList.SelectAll();
            e.Handled = true;
        }

        private IList<PhotoFile> GetSelectedPhotos()
        {
            if (PhotoList == null || Photos == null) return new List<PhotoFile>();
            return Photos.Where(photo => PhotoList.SelectedItems.Contains(photo)).ToList();
        }

        private static List<string> ExpandPhotoCopies(IEnumerable<PhotoFile> photos)
        {
            return photos.SelectMany(photo => Enumerable.Repeat(photo.Path,
                Math.Max(1, Math.Min(999, photo.CopyCount)))).ToList();
        }

        private void UpdatePhotoCopyControls()
        {
            if (PhotoCopyCountBox == null) return;
            var selectedPhotos = GetSelectedPhotos();
            var hasSelection = selectedPhotos.Count > 0;
            PhotoCopyCountBox.IsEnabled = hasSelection;
            PhotoCopyMinusButton.IsEnabled = hasSelection;
            PhotoCopyPlusButton.IsEnabled = hasSelection;
            _updatingPhotoCopyControls = true;
            try
            {
                PhotoCopyCountBox.Text = !hasSelection
                    ? "1"
                    : selectedPhotos.All(photo => photo.CopyCount == selectedPhotos[0].CopyCount)
                        ? Math.Max(1, selectedPhotos[0].CopyCount).ToString()
                        : "";
            }
            finally
            {
                _updatingPhotoCopyControls = false;
            }
        }

        private void PhotoCopyMinus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var photo in GetSelectedPhotos()) photo.CopyCount = Math.Max(1, photo.CopyCount - 1);
            PhotoCopiesChanged();
        }

        private void PhotoCopyPlus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var photo in GetSelectedPhotos()) photo.CopyCount = Math.Min(999, photo.CopyCount + 1);
            PhotoCopiesChanged();
        }

        private void PhotoCopyCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingPhotoCopyControls || !_ready) return;
            int count;
            if (!int.TryParse(PhotoCopyCountBox.Text, out count) || count < 1 || count > 999) return;
            foreach (var photo in GetSelectedPhotos()) photo.CopyCount = count;
            RefreshSidebarPhotoSelection();
            RefreshLayout();
        }

        private void PhotoCopiesChanged()
        {
            UpdatePhotoCopyControls();
            RefreshSidebarPhotoSelection();
            RefreshLayout();
        }

        private void RefreshSidebarPhotoSelection()
        {
            if (!_useSidebarSelection) return;

            _slotPhotoPaths = ExpandPhotoCopies(GetSelectedPhotos());
            if (_pvcPrintingMode && _slotPhotoPaths.Count > 0)
            {
                var desiredOrientation = UsePortraitCardOrientation(
                    _slotPhotoPaths.Select(IsPortraitPhoto).ToList())
                    ? "Landscape"
                    : "Portrait";
                if (!string.Equals(Convert.ToString(OrientationBox.SelectedItem), desiredOrientation,
                    StringComparison.OrdinalIgnoreCase))
                    OrientationBox.SelectedItem = desiredOrientation;
            }
        }

        private void UpdateFillSelectedPicturesButton()
        {
            if (FillSelectedPicturesButton == null) return;
            FillSelectedPicturesButton.IsEnabled = GetSelectedPhotos().Count > 0;
        }

        private void FillSelectedPictures_Click(object sender, RoutedEventArgs e)
        {
            var selectedPhotos = GetSelectedPhotos();
            if (selectedPhotos.Count == 0) return;
            var selectedPhotoPaths = ExpandPhotoCopies(selectedPhotos);
            _slotPhotoPaths = selectedPhotoPaths;
            _useSidebarSelection = true;
            if (_pvcPrintingMode)
                OrientationBox.SelectedItem = UsePortraitCardOrientation(
                    selectedPhotoPaths.Select(IsPortraitPhoto).ToList())
                    ? "Landscape"
                    : "Portrait";
            if (!string.Equals(Convert.ToString(PlacementBox.SelectedItem), "Multiple Photos", StringComparison.OrdinalIgnoreCase))
                PlacementBox.SelectedItem = "Multiple Photos";
            else
                RefreshLayout();
        }

        private static bool IsPortraitPhoto(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                using (var image = System.Drawing.Image.FromFile(path))
                {
                    ApplyExifOrientation(image);
                    return image.Height > image.Width;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool UsePortraitCardOrientation(IList<bool> portraitFlags)
        {
            if (portraitFlags == null || portraitFlags.Count == 0) return false;
            var portraitCount = portraitFlags.Count(value => value);
            if (portraitCount * 2 == portraitFlags.Count) return portraitFlags[0];
            return portraitCount * 2 > portraitFlags.Count;
        }

        private void PaperPreview_SelectedPhotoChanged(object sender, EventArgs e)
        {
            UpdateSelectedPhotoControls();
        }

        private bool ApplyPhotoControlsToAll
        {
            get
            {
                return _pvcPrintingMode && string.Equals(
                    Convert.ToString(SelectedPhotoApplyScopeBox.SelectedItem), AllPicturesScope,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private void UpdateSelectedPhotoControls()
        {
            if (SelectedPhotoFitBox == null || SelectedPictureBorderBox == null || PaperPreview == null) return;
            var applyToAll = ApplyPhotoControlsToAll;
            var canApply = applyToAll ? PaperPreview.HasPhotos : PaperPreview.HasSelectedPhoto;
            _updatingSelectedPhotoControls = true;
            try
            {
                SelectedPhotoFitBox.IsEnabled = canApply;
                SelectedPictureBorderBox.IsEnabled = canApply;
                SelectedPhotoFitBox.SelectedItem = canApply
                    ? applyToAll ? PaperPreview.AllPhotoFitMode : PaperPreview.SelectedPhotoFitMode
                    : null;
                var printsBorder = canApply
                    ? applyToAll ? PaperPreview.AllPhotosPrintBorder : PaperPreview.SelectedPhotoPrintsBorder
                    : null;
                SelectedPictureBorderBox.SelectedItem = printsBorder.HasValue
                    ? printsBorder.Value ? PrintPictureBorderOption : NoPictureBorderOption
                    : null;
            }
            finally
            {
                _updatingSelectedPhotoControls = false;
            }
        }

        private void SelectedPhotoApplyScope_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            UpdateSelectedPhotoControls();
        }

        private void SelectedPhotoFit_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingSelectedPhotoControls || !_ready) return;
            var mode = Convert.ToString(SelectedPhotoFitBox.SelectedItem);
            var changed = !string.IsNullOrWhiteSpace(mode) && (ApplyPhotoControlsToAll
                ? PaperPreview.SetAllPhotoFitMode(mode)
                : PaperPreview.SetSelectedPhotoFitMode(mode));
            if (changed)
                UpdatePageSidebar();
        }

        private void SelectedPictureBorder_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingSelectedPhotoControls || !_ready) return;
            var value = Convert.ToString(SelectedPictureBorderBox.SelectedItem);
            if (string.IsNullOrWhiteSpace(value)) return;
            var printBorder = string.Equals(value, PrintPictureBorderOption, StringComparison.OrdinalIgnoreCase);
            var changed = ApplyPhotoControlsToAll
                ? PaperPreview.SetAllPhotoPictureBorder(printBorder)
                : PaperPreview.SetSelectedPhotoPictureBorder(printBorder);
            if (changed)
                UpdatePageSidebar();
        }

        private void LayoutSetting_Changed(object sender, EventArgs e)
        {
            if (!_ready) return;
            AmountLabel.Text = Convert.ToString(PlacementBox.SelectedItem) == "Fill Page" ? "Photo quantity (1 = auto-fill)" : "Package sets";
            SaveCurrentSettings();
            RefreshLayout();
        }

        private void SaveCurrentSettings()
        {
            if (!_ready && PaperBox.SelectedItem == null) return;
            if (_pvcPrintingMode) return;
            var paper = PaperBox.SelectedItem as PaperDefinition;
            if (paper != null) _package.PaperName = paper.Name;
            var orientation = Convert.ToString(OrientationBox.SelectedItem);
            if (!string.IsNullOrWhiteSpace(orientation)) _package.Orientation = orientation;
            var placement = Convert.ToString(PlacementBox.SelectedItem);
            if (!_forceMultiplePhotos && !string.IsNullOrWhiteSpace(placement)) _package.PlacementMode = placement;
            int amount;
            if (int.TryParse(AmountBox.Text, out amount))
                _package.PrintAmount = Math.Max(1, Math.Min(999, amount));
            _package.ShowGrid = GridToggle.IsChecked == true;
            _package.PrintCutGuides = CutGuidesToggle.IsChecked == true;
            if (SupportsPictureBorders(_package))
                _package.PrintPictureBorder = PictureBorderToggle.IsChecked == true;
            _package.Borderless = BorderlessToggle.IsChecked == true;
            if (_saveSettings != null) _saveSettings();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveCurrentSettings();
            base.OnClosing(e);
        }

        private void RefreshLayout()
        {
            if (!_ready) return;
            var paper = PaperBox.SelectedItem as PaperDefinition ?? PaperDefinition.Find(_package.PaperName);
            var orientation = Convert.ToString(OrientationBox.SelectedItem) ?? "Portrait";
            var placement = Convert.ToString(PlacementBox.SelectedItem) ?? "Repeat One Photo";
            int amount;
            if (!int.TryParse(AmountBox.Text, out amount)) amount = 1;
            amount = Math.Max(1, Math.Min(999, amount));
            var importedPhotos = (IList<string>)ExpandPhotoCopies(Photos);
            var layoutPhotos = _useSidebarSelection
                ? _slotPhotoPaths ?? importedPhotos
                : IsRushIdMultiple(_package, placement)
                    ? importedPhotos
                    : _slotPhotoPaths ?? importedPhotos;
            _layout = _pvcPrintingMode
                ? CreatePvcPrintingLayout(paper, orientation,
                    _useSidebarSelection ? layoutPhotos : new List<string>())
                : PrintLayoutEngine.Create(_package, paper, orientation, placement,
                    layoutPhotos, amount, BorderlessToggle.IsChecked == true);
            _pageIndex = Math.Max(0, Math.Min(_pageIndex, Math.Max(0, _layout.PageCount - 1)));
            PaperPreview.Layout = _layout;
            PaperPreview.PageIndex = _pageIndex;
            PaperPreview.ShowGrid = GridToggle.IsChecked == true;
            PaperPreview.ShowCutGuides = CutGuidesToggle.IsChecked == true;
            PaperPreview.ShowPictureBorders = SupportsPictureBorders(_package);
            PageText.Text = _layout.PageCount == 0 ? "No pages" : string.Format("Page {0} of {1}", _pageIndex + 1, _layout.PageCount);
            PreviousPageButton.IsEnabled = _pageIndex > 0;
            NextPageButton.IsEnabled = _pageIndex + 1 < _layout.PageCount;
            LayoutSummary.Text = string.Format("{0:0.#} × {1:0.#} mm • {2} photo slot(s) • {3} page(s)",
                _layout.PaperWidthMm, _layout.PaperHeightMm, _layout.Placements.Count, _layout.PageCount);
            var hasPreviewPhotos = _layout.Placements.Any(item =>
                !string.IsNullOrWhiteSpace(item.PhotoPath) && File.Exists(item.PhotoPath));
            if (_pvcPrintingMode)
            {
                DropHintText.Text = Photos.Count == 0 ? "Import an image first" : "Select an image";
                DropHint.Visibility = hasPreviewPhotos ? Visibility.Collapsed : Visibility.Visible;
            }
            SavePdfButton.IsEnabled = hasPreviewPhotos;
            UpdatePageSidebar();
        }

        private static PrintLayout CreatePvcPrintingLayout(PaperDefinition paper, string orientation,
            IList<string> photoPaths)
        {
            var landscape = string.Equals(orientation, "Landscape", StringComparison.OrdinalIgnoreCase);
            var pageWidth = landscape ? Math.Max(paper.WidthMm, paper.HeightMm) : Math.Min(paper.WidthMm, paper.HeightMm);
            var pageHeight = landscape ? Math.Min(paper.WidthMm, paper.HeightMm) : Math.Max(paper.WidthMm, paper.HeightMm);
            var layout = new PrintLayout
            {
                PaperWidthMm = pageWidth,
                PaperHeightMm = pageHeight,
                MarginMm = PvcPrintableMarginMm,
                PageCount = 1
            };

            var validPhotos = (photoPaths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).ToList();
            if (validPhotos.Count == 0) return layout;

            var portraitCards = UsePortraitCardOrientation(validPhotos.Select(IsPortraitPhoto).ToList());
            var cardWidth = portraitCards ? PvcCardWidthMm : PvcCardHeightMm;
            var cardHeight = portraitCards ? PvcCardHeightMm : PvcCardWidthMm;
            var usableWidth = Math.Max(0, pageWidth - PvcPrintableMarginMm * 2);
            var usableHeight = Math.Max(0, pageHeight - PvcPrintableMarginMm * 2);
            var columns = Math.Max(1, (int)Math.Floor(usableWidth / cardWidth));
            var rows = Math.Max(1, (int)Math.Floor(usableHeight / cardHeight));
            var columnGap = columns > 1
                ? Math.Max(0, (usableWidth - columns * cardWidth) / (columns - 1))
                : 0;
            var rowGap = rows > 1
                ? Math.Max(0, (usableHeight - rows * cardHeight) / (rows - 1))
                : 0;
            var cardsPerPage = Math.Max(1, columns * rows);
            layout.PageCount = (int)Math.Ceiling(validPhotos.Count / (double)cardsPerPage);

            for (var index = 0; index < validPhotos.Count; index++)
            {
                var page = index / cardsPerPage;
                var position = index % cardsPerPage;
                // Portrait paper fills each row from right to left. Landscape paper
                // fills each column from top to bottom, then moves to the right.
                var row = landscape ? position % rows : position / columns;
                var column = landscape ? position / rows : position % columns;
                var x = landscape
                    ? PvcPrintableMarginMm + column * (cardWidth + columnGap)
                    : pageWidth - PvcPrintableMarginMm - cardWidth - column * (cardWidth + columnGap);
                var y = PvcPrintableMarginMm + row * (cardHeight + rowGap);
                layout.Placements.Add(new PrintPlacement
                {
                    PageIndex = page,
                    BoundsMm = new Rect(x, y, cardWidth, cardHeight),
                    PhotoPath = validPhotos[index],
                    Label = "PVC ID",
                    PhotoFitMode = "Crop",
                    PrintPictureBorder = false,
                    AutoRotatePhotoToBox = true,
                    CornerRadiusMm = PvcCardCornerRadiusMm
                });
            }

            return layout;
        }

        private static bool IsRushIdMultiple(PhotoPackage package, string placement)
        {
            return IsRushId(package) &&
                   string.Equals(placement, "Multiple Photos", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRushId(PhotoPackage package)
        {
            return package != null && string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPhotoPackage(PhotoPackage package)
        {
            return package != null &&
                   (string.Equals(package.Category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(package.Category, "Custom", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(package.CustomKind, "Photo", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool SupportsPictureBorders(PhotoPackage package)
        {
            return package != null;
        }

        private void UpdatePageSidebar()
        {
            var show = _layout != null && _layout.PageCount >= 2 &&
                       (_pvcPrintingMode ||
                        string.Equals(_package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase));
            PageSidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _updatingPageSidebar = true;
            try
            {
                if (!show)
                {
                    PagePreviewList.ItemsSource = null;
                    return;
                }

                PagePreviewList.ItemsSource = Enumerable.Range(0, _layout.PageCount)
                    .Select(index => new PagePreviewItem
                    {
                        Layout = _layout,
                        PageIndex = index,
                        Label = string.Format("Page {0}", index + 1),
                        ShowGrid = GridToggle.IsChecked == true,
                        ShowCutGuides = CutGuidesToggle.IsChecked == true,
                        ShowPictureBorders = SupportsPictureBorders(_package)
                    }).ToList();
                PagePreviewList.SelectedIndex = _pageIndex;
                PagePreviewList.ScrollIntoView(PagePreviewList.SelectedItem);
            }
            finally
            {
                _updatingPageSidebar = false;
            }
        }

        private void PagePreviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingPageSidebar || PagePreviewList.SelectedIndex < 0 || _layout == null) return;
            _pageIndex = PagePreviewList.SelectedIndex;
            PaperPreview.PageIndex = _pageIndex;
            PageText.Text = string.Format("Page {0} of {1}", _pageIndex + 1, _layout.PageCount);
            PreviousPageButton.IsEnabled = _pageIndex > 0;
            NextPageButton.IsEnabled = _pageIndex + 1 < _layout.PageCount;
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex > 0) _pageIndex--;
            RefreshLayout();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_layout != null && _pageIndex + 1 < _layout.PageCount) _pageIndex++;
            RefreshLayout();
        }

        private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ThemeService.Apply(ThemeToggle.IsChecked == true);
            ThemeToggle.Content = ThemeService.IsDark ? "☀  Light mode" : "🌙  Night mode";
        }

        private void RefreshPrinters_Click(object sender, RoutedEventArgs e) { LoadPrinters(Convert.ToString(PrinterBox.SelectedItem)); }

        private void PrinterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready && PrinterBox.SelectedItem == null) return;
            var name = Convert.ToString(PrinterBox.SelectedItem);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!string.Equals(_package.DefaultPrinter, name, StringComparison.OrdinalIgnoreCase))
                    _package.PrinterDevModeBase64 = null;
                _package.DefaultPrinter = name;
                _printerSettings = CreatePrinterSettings(_package, name);
                SavePrinterSettings(_package, _printerSettings);
                if (_ready && _saveSettings != null) _saveSettings();
            }
            UpdatePrinterStatus();
        }

        private void UpdatePrinterStatus()
        {
            var name = Convert.ToString(PrinterBox.SelectedItem);
            if (string.IsNullOrWhiteSpace(name))
            {
                PrinterStatus.Text = "No installed printer was found.";
                PrinterStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                return;
            }
            try
            {
                if (_printerSettings == null ||
                    !string.Equals(_printerSettings.PrinterName, name, StringComparison.OrdinalIgnoreCase))
                    _printerSettings = CreatePrinterSettings(_package, name);
                PrinterStatus.Text = _printerSettings.IsValid ? "Installed and available to Windows" : "Printer is not currently available";
                PrinterStatus.Foreground = (System.Windows.Media.Brush)FindResource(_printerSettings.IsValid ? "SuccessBrush" : "DangerBrush");
            }
            catch
            {
                PrinterStatus.Text = "Printer status could not be read";
                PrinterStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            }
        }

        private void PrinterSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_printerSettings == null || !_printerSettings.IsValid)
            {
                MessageBox.Show("Select an installed printer first.", "Printer Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            using (var dialog = new System.Windows.Forms.PrintDialog())
            {
                dialog.UseEXDialog = true;
                dialog.AllowPrintToFile = false;
                dialog.PrinterSettings = (PrinterSettings)_printerSettings.Clone();
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _printerSettings = (PrinterSettings)dialog.PrinterSettings.Clone();
                    _package.DefaultPrinter = _printerSettings.PrinterName;
                    SavePrinterSettings(_package, _printerSettings);
                    PrinterBox.SelectedItem = _printerSettings.PrinterName;
                    if (_saveSettings != null) _saveSettings();
                    UpdatePrinterStatus();
                }
            }
        }

        private const uint GmemMoveable = 0x0002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr GlobalSize(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);

        internal static PrinterSettings CreatePrinterSettings(PhotoPackage package, string printerName)
        {
            var settings = new PrinterSettings();
            if (!string.IsNullOrWhiteSpace(printerName)) settings.PrinterName = printerName;
            if (!settings.IsValid || package == null ||
                !string.Equals(package.DefaultPrinter, settings.PrinterName, StringComparison.OrdinalIgnoreCase))
                return settings;

            if (!string.IsNullOrWhiteSpace(package.PrinterDevModeBase64))
            {
                IntPtr handle = IntPtr.Zero;
                IntPtr pointer = IntPtr.Zero;
                try
                {
                    var bytes = Convert.FromBase64String(package.PrinterDevModeBase64);
                    handle = GlobalAlloc(GmemMoveable, new UIntPtr((uint)bytes.Length));
                    if (handle != IntPtr.Zero)
                    {
                        pointer = GlobalLock(handle);
                        if (pointer != IntPtr.Zero)
                        {
                            Marshal.Copy(bytes, 0, pointer, bytes.Length);
                            GlobalUnlock(handle);
                            pointer = IntPtr.Zero;
                            settings.SetHdevmode(handle);
                        }
                    }
                }
                catch
                {
                    package.PrinterDevModeBase64 = null;
                }
                finally
                {
                    if (pointer != IntPtr.Zero) GlobalUnlock(handle);
                    if (handle != IntPtr.Zero) GlobalFree(handle);
                }
            }

            if (package.PrinterMediaTypeId > 0) ApplyMediaType(settings, package.PrinterMediaTypeId);
            try
            {
                settings.Copies = (short)Math.Max(1,
                    Math.Min(Math.Max(1, settings.MaximumCopies), package.PrinterCopies));
                var pageSettings = settings.DefaultPageSettings;
                var paperSize = settings.PaperSizes.Cast<PaperSize>().FirstOrDefault(item =>
                    string.Equals(item.PaperName, package.PrinterPaperSizeName, StringComparison.OrdinalIgnoreCase));
                if (paperSize != null) pageSettings.PaperSize = paperSize;
                var resolution = settings.PrinterResolutions.Cast<PrinterResolution>().FirstOrDefault(item =>
                    (int)item.Kind == package.PrinterResolutionKind &&
                    item.X == package.PrinterResolutionX && item.Y == package.PrinterResolutionY);
                if (resolution != null) pageSettings.PrinterResolution = resolution;
                pageSettings.Color = package.PrinterPrintInColor && settings.SupportsColor;
            }
            catch
            {
                // Keep the driver defaults if an optional saved capability is no longer available.
            }
            return settings;
        }

        internal static void SavePrinterSettings(PhotoPackage package, PrinterSettings settings)
        {
            if (package == null || settings == null || !settings.IsValid) return;
            package.DefaultPrinter = settings.PrinterName;
            IntPtr handle = IntPtr.Zero;
            IntPtr pointer = IntPtr.Zero;
            try
            {
                handle = settings.GetHdevmode(settings.DefaultPageSettings);
                if (handle == IntPtr.Zero) return;
                var size = checked((int)GlobalSize(handle).ToUInt64());
                if (size <= 0) return;
                pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return;
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                package.PrinterDevModeBase64 = Convert.ToBase64String(bytes);
            }
            catch
            {
                package.PrinterDevModeBase64 = null;
            }
            finally
            {
                if (pointer != IntPtr.Zero) GlobalUnlock(handle);
                if (handle != IntPtr.Zero) GlobalFree(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DeviceModeLayout
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion;
            public ushort DriverVersion;
            public ushort Size;
            public ushort DriverExtra;
            public int Fields;
            public short Orientation;
            public short PaperSize;
            public short PaperLength;
            public short PaperWidth;
            public short Scale;
            public short Copies;
            public short DefaultSource;
            public short PrintQuality;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TTOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int IcmMethod;
            public int IcmIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        internal static void ApplyMediaType(PrinterSettings settings, int mediaTypeId)
        {
            if (settings == null || !settings.IsValid || mediaTypeId <= 0) return;
            const int dmMediaType = 0x02000000;
            IntPtr handle = IntPtr.Zero;
            IntPtr pointer = IntPtr.Zero;
            try
            {
                handle = settings.GetHdevmode();
                if (handle == IntPtr.Zero) return;
                pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return;
                var fieldsOffset = (int)Marshal.OffsetOf(typeof(DeviceModeLayout), "Fields");
                var mediaTypeOffset = (int)Marshal.OffsetOf(typeof(DeviceModeLayout), "MediaType");
                Marshal.WriteInt32(pointer, fieldsOffset, Marshal.ReadInt32(pointer, fieldsOffset) | dmMediaType);
                Marshal.WriteInt32(pointer, mediaTypeOffset, mediaTypeId);
                GlobalUnlock(handle);
                pointer = IntPtr.Zero;
                settings.SetHdevmode(handle);
            }
            catch
            {
                // The driver properties dialog remains available for drivers that reject direct media changes.
            }
            finally
            {
                if (pointer != IntPtr.Zero) GlobalUnlock(handle);
                if (handle != IntPtr.Zero) GlobalFree(handle);
            }
        }

        public static bool ConfigurePrinterDriver(PhotoPackage package, Window owner, Action saveSettings)
        {
            if (package == null) return false;
            var settings = CreatePrinterSettings(package, package.DefaultPrinter);
            if (settings == null || !settings.IsValid)
            {
                MessageBox.Show(owner, "Windows did not report an installed printer.", "Printer Settings",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            using (var dialog = new System.Windows.Forms.PrintDialog())
            {
                dialog.UseEXDialog = true;
                dialog.AllowPrintToFile = false;
                dialog.AllowSelection = false;
                dialog.AllowSomePages = false;
                dialog.PrinterSettings = (PrinterSettings)settings.Clone();
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

                SavePrinterSettings(package, dialog.PrinterSettings);
                if (saveSettings != null) saveSettings();
                MessageBox.Show(owner,
                    "Printer driver settings were saved and will be used by the Print button.",
                    "Printer Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
        }

        private void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            if (_layout == null || _layout.PageCount < 1 || !_layout.Placements.Any(item =>
                !string.IsNullOrWhiteSpace(item.PhotoPath) && File.Exists(item.PhotoPath)))
            {
                MessageBox.Show(this, "Import and fill at least one picture before saving a PDF.",
                    "Save PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Save PVC Printing Preview as PDF",
                Filter = "PDF document (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = "PVC Printing.pdf"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                PdfExportService.Save(dialog.FileName, _layout,
                    CutGuidesToggle.IsChecked == true, SupportsPictureBorders(_package));
                MessageBox.Show(this, "PDF saved successfully.", "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the PDF.\n\n" + ex.Message,
                    "Save PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PrintNow_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSettings();
            if (Photos.Count == 0)
            {
                MessageBox.Show("Add at least one photo before printing.", "Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PrintLayoutWithConfirmation(_package, _layout, _printerSettings,
                CutGuidesToggle.IsChecked == true, this);
        }

        public static bool PrintUsingSavedSettings(PhotoPackage package, IEnumerable<string> photos,
            IList<string> slotPhotoPaths, Window owner, bool forceMultiplePhotos = false,
            int? amountOverride = null, string placementOverride = null,
            IList<System.Windows.Point> placementOffsets = null,
            IList<string> placementFitModes = null,
            IList<bool> placementPictureBorders = null)
        {
            var photoPaths = (photos ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).ToList();
            if (photoPaths.Count == 0)
            {
                MessageBox.Show(owner, "Import at least one photo before printing.", "Print",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var paper = PaperDefinition.Find(package.PaperName);
            var placement = !string.IsNullOrWhiteSpace(placementOverride)
                ? placementOverride
                : forceMultiplePhotos || (slotPhotoPaths != null && !IsRushId(package))
                ? "Multiple Photos"
                : package.PlacementMode;
            var layoutPhotos = IsRushIdMultiple(package, placement)
                ? (IList<string>)photoPaths
                : slotPhotoPaths ?? (IList<string>)photoPaths;
            var layout = PrintLayoutEngine.Create(package, paper, package.Orientation, placement,
                layoutPhotos, Math.Max(1, amountOverride ?? package.PrintAmount), package.Borderless);
            if (placementOffsets != null && placementOffsets.Count > 0)
            {
                for (var index = 0; index < layout.Placements.Count; index++)
                {
                    var offset = placementOffsets[index % placementOffsets.Count];
                    layout.Placements[index].PhotoOffsetX = Math.Max(-1, Math.Min(1, offset.X));
                    layout.Placements[index].PhotoOffsetY = Math.Max(-1, Math.Min(1, offset.Y));
                }
            }
            if (placementFitModes != null && placementFitModes.Count > 0)
            {
                for (var index = 0; index < layout.Placements.Count; index++)
                    layout.Placements[index].PhotoFitMode = string.Equals(
                        placementFitModes[index % placementFitModes.Count], "Stretch",
                        StringComparison.OrdinalIgnoreCase) ? "Stretch" : "Crop";
            }
            if (placementPictureBorders != null && placementPictureBorders.Count > 0)
            {
                for (var index = 0; index < layout.Placements.Count; index++)
                    layout.Placements[index].PrintPictureBorder =
                        placementPictureBorders[index % placementPictureBorders.Count];
            }
            var settings = CreatePrinterSettings(package, package.DefaultPrinter);
            return PrintLayoutWithConfirmation(package, layout, settings, package.PrintCutGuides, owner);
        }

        private static bool PrintLayoutWithConfirmation(PhotoPackage package, PrintLayout layout,
            PrinterSettings printerSettings, bool printCutGuides, Window owner)
        {
            if (printerSettings == null || !printerSettings.IsValid)
            {
                MessageBox.Show(owner, "Open Printer Settings and select an installed printer first.", "Print",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (layout == null || layout.PageCount == 0)
            {
                MessageBox.Show(owner, "The selected package did not produce any printable pages.", "Print",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (FindMatchingPaperSize(printerSettings, layout) == null)
            {
                var continueAnswer = MessageBox.Show(owner,
                    "The selected printer driver does not report an exact match for this paper size. " +
                    "Open Printer Settings and confirm the correct media before continuing.\n\nPrint using the driver's current paper setting?",
                    "Paper Size Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (continueAnswer != MessageBoxResult.Yes) return false;
            }
            var answer = MessageBox.Show(owner,
                string.Format("Print {0} page(s) to {1}?", layout.PageCount, printerSettings.PrinterName),
                "Confirm Print", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;

            try
            {
                PrintLayoutDirect(package, layout, printerSettings, printCutGuides);
                MessageBox.Show(owner, "The job was sent to the printer.", "RC Photo Studio",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Printing failed.\n\n" + ex.Message, "Print Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static void PrintLayoutDirect(PhotoPackage package, PrintLayout layout,
            PrinterSettings printerSettings, bool printCutGuides)
        {
            var pageIndex = 0;
            using (var document = new PrintDocument())
            {
                document.DocumentName = "RC Photo Studio - " + package.Name;
                document.PrintController = new StandardPrintController();
                document.PrinterSettings = (PrinterSettings)printerSettings.Clone();
                document.DefaultPageSettings.Landscape = layout.PaperWidthMm > layout.PaperHeightMm;
                document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
                var paper = FindMatchingPaperSize(document.PrinterSettings, layout);
                if (paper != null) document.DefaultPageSettings.PaperSize = paper;

                document.PrintPage += delegate(object sender, PrintPageEventArgs e)
                {
                    DrawPrintPage(e.Graphics, pageIndex, layout, printCutGuides,
                        SupportsPictureBorders(package));
                    pageIndex++;
                    e.HasMorePages = pageIndex < layout.PageCount;
                };
                document.Print();
            }
        }

        internal static void DrawPrintPage(Graphics graphics, int pageIndex, PrintLayout layout,
            bool printCutGuides, bool printPictureBorders)
        {
            graphics.PageUnit = GraphicsUnit.Millimeter;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            foreach (var placement in layout.Placements.Where(p => p.PageIndex == pageIndex))
            {
                var rect = new RectangleF((float)placement.BoundsMm.X, (float)placement.BoundsMm.Y,
                    (float)placement.BoundsMm.Width, (float)placement.BoundsMm.Height);
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                    graphics.RotateTransform((float)placement.RotationDegrees);
                    var rotatedRect = new RectangleF(-rect.Width / 2, -rect.Height / 2, rect.Width, rect.Height);
                    using (var roundedPath = placement.CornerRadiusMm > 0
                        ? CreateRoundedRectanglePath(rotatedRect, (float)placement.CornerRadiusMm)
                        : null)
                    {
                        if (!string.IsNullOrWhiteSpace(placement.PhotoPath) && File.Exists(placement.PhotoPath))
                        {
                            var imageState = graphics.Save();
                            try
                            {
                                if (roundedPath != null)
                                    graphics.SetClip(roundedPath, CombineMode.Intersect);
                                using (var image = System.Drawing.Image.FromFile(placement.PhotoPath))
                                {
                                    ApplyExifOrientation(image);
                                    if (placement.AutoRotatePhotoToBox && ImageLoadingService.ShouldRotateToMatch(
                                        image.Width, image.Height,
                                        placement.BoundsMm.Width, placement.BoundsMm.Height))
                                        image.RotateFlip(RotateFlipType.Rotate90FlipNone);
                                    if (string.Equals(placement.PhotoFitMode, "Stretch", StringComparison.OrdinalIgnoreCase))
                                        graphics.DrawImage(image, rotatedRect);
                                    else
                                        DrawImageCover(graphics, image, rotatedRect,
                                            placement.PhotoOffsetX, placement.PhotoOffsetY);
                                }
                            }
                            finally
                            {
                                graphics.Restore(imageState);
                            }
                        }
                        if (printCutGuides)
                        {
                            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(130, 30, 30, 30), 0.2f))
                            {
                                pen.DashStyle = DashStyle.Dash;
                                if (roundedPath != null) graphics.DrawPath(pen, roundedPath);
                                else graphics.DrawRectangle(pen, rotatedRect.X, rotatedRect.Y,
                                    rotatedRect.Width, rotatedRect.Height);
                            }
                        }
                        if (printPictureBorders && placement.PrintPictureBorder &&
                            !string.IsNullOrWhiteSpace(placement.PhotoPath) &&
                            File.Exists(placement.PhotoPath))
                        {
                            using (var pen = new System.Drawing.Pen(System.Drawing.Color.Black,
                                GetOutputPictureBorderWidthMillimeters(graphics)))
                            {
                                if (roundedPath != null) graphics.DrawPath(pen, roundedPath);
                                else graphics.DrawRectangle(pen, rotatedRect.X, rotatedRect.Y,
                                    rotatedRect.Width, rotatedRect.Height);
                            }
                        }
                    }
                }
                finally { graphics.Restore(state); }
            }
        }

        private static float GetOutputPictureBorderWidthMillimeters(Graphics graphics)
        {
            return OutputPictureBorderWidthPixels * 25.4f / Math.Max(1f, graphics.DpiX);
        }

        private static GraphicsPath CreateRoundedRectanglePath(RectangleF rectangle, float radius)
        {
            radius = Math.Max(0, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2));
            var diameter = radius * 2;
            var path = new GraphicsPath();
            if (diameter <= 0)
            {
                path.AddRectangle(rectangle);
                return path;
            }
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawImageCover(Graphics graphics, System.Drawing.Image image, RectangleF target,
            double offsetX = 0, double offsetY = 0)
        {
            var targetRatio = target.Width / target.Height;
            var imageRatio = (float)image.Width / image.Height;
            RectangleF source;
            if (imageRatio > targetRatio)
            {
                var width = image.Height * targetRatio;
                var available = image.Width - width;
                var x = available / 2f * (1f + (float)Math.Max(-1, Math.Min(1, offsetX)));
                source = new RectangleF(x, 0, width, image.Height);
            }
            else
            {
                var height = image.Width / targetRatio;
                var available = image.Height - height;
                var y = available / 2f * (1f + (float)Math.Max(-1, Math.Min(1, offsetY)));
                source = new RectangleF(0, y, image.Width, height);
            }
            graphics.DrawImage(image, target, source, GraphicsUnit.Pixel);
        }

        private static PaperSize FindMatchingPaperSize(PrinterSettings settings, PrintLayout layout)
        {
            var desiredWidth = (int)Math.Round(Math.Min(layout.PaperWidthMm, layout.PaperHeightMm) / 25.4 * 100);
            var desiredHeight = (int)Math.Round(Math.Max(layout.PaperWidthMm, layout.PaperHeightMm) / 25.4 * 100);
            return settings.PaperSizes.Cast<PaperSize>()
                .FirstOrDefault(p => Math.Abs(Math.Min(p.Width, p.Height) - desiredWidth) <= 6 &&
                                     Math.Abs(Math.Max(p.Width, p.Height) - desiredHeight) <= 6);
        }

        private static void ApplyExifOrientation(System.Drawing.Image image)
        {
            const int orientationId = 0x0112;
            try
            {
                if (!image.PropertyIdList.Contains(orientationId)) return;
                var value = image.GetPropertyItem(orientationId).Value[0];
                switch (value)
                {
                    case 2: image.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: image.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: image.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                    case 5: image.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: image.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: image.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                    case 8: image.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch { }
        }
    }
}
