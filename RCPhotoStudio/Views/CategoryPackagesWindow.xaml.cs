using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using RCPhotoStudio.Controls;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;

namespace RCPhotoStudio.Views
{
    public partial class CategoryPackagesWindow : Window, INotifyPropertyChanged
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" },
            StringComparer.OrdinalIgnoreCase);

        private readonly ObservableCollection<PhotoPackage> _allPackages;
        private readonly ObservableCollection<PhotoPackage> _categoryPackages;
        private readonly string _category;
        private readonly Action _savePackages;
        private readonly Stack<RemovedPackageState> _removedPackages = new Stack<RemovedPackageState>();
        private IList<string> _multiPhotoPaths;
        private PrintLayout _multiPhotoLayout;
        private int _multiPhotoPageIndex;
        private int _multiPhotoAmount = 1;
        private bool _photoPackageMultipleLayout;
        private bool _updatingPackagePageSidebar;
        private bool _updatingRushIdPackageCount;
        private bool _updatingPhotoCopyCount;
        private bool _updatingPhotoFit;
        private bool _updatingPictureBorder;
        private bool _updatingRushIdPictureBorder;
        private bool _updatingRushIdPhotoFit;
        private bool _updatingAutomaticRushControls;

        private sealed class RemovedPackageState
        {
            public PhotoPackage Package { get; set; }
            public int AllPackagesIndex { get; set; }
            public int CategoryIndex { get; set; }
        }

        public ObservableCollection<PhotoFile> Photos { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public double ThumbnailImageSize { get; private set; }
        public double ThumbnailTileWidth { get { return ThumbnailImageSize + 8; } }
        public double ThumbnailTileHeight { get { return ThumbnailImageSize + 36; } }

        public CategoryPackagesWindow(ObservableCollection<PhotoPackage> packages, string category, Action savePackages = null)
        {
            InitializeComponent();
            WindowSizingService.Maximize(this);
            _allPackages = packages;
            _category = category;
            _savePackages = savePackages;
            var isAutomaticRushId = string.Equals(category, "Rush ID", StringComparison.OrdinalIgnoreCase);
            if (isAutomaticRushId && AutomaticRushIdPackage.Normalize(_allPackages) && _savePackages != null)
                _savePackages();
            ThumbnailImageSize = ThumbnailZoomSlider.Value;
            DataContext = this;
            CategoryTitle.Text = category + " Packages";
            var allowPhotoReposition = string.Equals(category, "Photo", StringComparison.OrdinalIgnoreCase);
            PhotoList.SelectionMode = allowPhotoReposition ? SelectionMode.Extended : SelectionMode.Multiple;
            AssignmentPreview.AllowPhotoReposition = allowPhotoReposition;
            MultiPhotoPreview.AllowPhotoReposition = allowPhotoReposition;
            PhotoFitBox.ItemsSource = new[] { "Crop", "Stretch" };
            RushIdPhotoFitBox.ItemsSource = new[] { "Crop", "Stretch" };
            PictureBorderBox.ItemsSource = new[] { "Do not print", "Print border" };
            RushIdPictureBorderBox.ItemsSource = new[] { "Do not print", "Print border" };
            PhotoFitPanel.Visibility = allowPhotoReposition ? Visibility.Visible : Visibility.Collapsed;
            PhotoCopyPanel.Visibility = allowPhotoReposition ? Visibility.Visible : Visibility.Collapsed;
            CategorySubtitle.Text = isAutomaticRushId
                ? "Choose ID sizes and quantities; the printable layout is created automatically"
                : allowPhotoReposition
                ? "Ctrl+click selects several preview pictures; Ctrl+A selects all; drag or use arrow keys to reposition"
                : "Choose a package, import photos, then fill or drag thumbnails into its picture boxes";
            Photos = new ObservableCollection<PhotoFile>();
            PhotoList.ItemsSource = Photos;

            _categoryPackages = new ObservableCollection<PhotoPackage>(packages.Where(MatchesCategory));
            var packageView = CollectionViewSource.GetDefaultView(_categoryPackages);
            packageView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            PackageList.ItemsSource = packageView;
            SavedPackagePanel.Visibility = isAutomaticRushId ? Visibility.Collapsed : Visibility.Visible;
            AutomaticRushIdPanel.Visibility = isAutomaticRushId ? Visibility.Visible : Visibility.Collapsed;
            PackageColumn.Width = new GridLength(isAutomaticRushId ? 250 : 150);
            var usesSavedPrintSettings =
                string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase);
            OpenWorkspaceButton.Visibility = usesSavedPrintSettings ? Visibility.Collapsed : Visibility.Visible;
            SavePdfButton.Visibility = usesSavedPrintSettings ? Visibility.Visible : Visibility.Collapsed;
            PrinterSettingsButton.Visibility = usesSavedPrintSettings ? Visibility.Visible : Visibility.Collapsed;
            PrintButton.Visibility = usesSavedPrintSettings ? Visibility.Visible : Visibility.Collapsed;
            if (_categoryPackages.Count > 0) PackageList.SelectedIndex = 0;
            if (isAutomaticRushId) ConfigureAutomaticRushIdControls();
            UpdatePhotoCopyControls();
            UpdateRushIdPackageCountControls();
            UpdatePackageState();
            UpdateAssignmentStatus();
        }

        private bool MatchesCategory(PhotoPackage package)
        {
            return string.Equals(package.Category, _category, StringComparison.OrdinalIgnoreCase) ||
                   (string.Equals(package.Category, "Custom", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(package.CustomKind, _category, StringComparison.OrdinalIgnoreCase));
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

        private PhotoPackage SelectedPackage
        {
            get { return PackageList.SelectedItem as PhotoPackage; }
        }

        private void ConfigureAutomaticRushIdControls()
        {
            var package = SelectedPackage;
            if (package == null || !package.AutoFlowNoGap) return;

            _updatingAutomaticRushControls = true;
            try
            {
                AutomaticRushPaperBox.ItemsSource = PaperDefinition.StandardPapers();
                AutomaticRushPaperBox.SelectedItem = ((IEnumerable<PaperDefinition>)AutomaticRushPaperBox.ItemsSource)
                    .FirstOrDefault(item => item.Name == package.PaperName) ??
                    (PaperDefinition)AutomaticRushPaperBox.Items[0];
                AutomaticRushOrientationBox.ItemsSource = new[] { "Portrait", "Landscape", "Automatic" };
                AutomaticRushOrientationBox.SelectedItem = string.IsNullOrWhiteSpace(package.Orientation)
                    ? "Portrait"
                    : package.Orientation;

                SetAutomaticSizeControl(TwoByTwoCheckBox, TwoByTwoQuantityBox,
                    FindAutomaticSize(package, 50.8, 50.8));
                SetAutomaticSizeControl(OneByOneCheckBox, OneByOneQuantityBox,
                    FindAutomaticSize(package, 25.4, 25.4));
                SetAutomaticSizeControl(OnePointFiveCheckBox, OnePointFiveQuantityBox,
                    FindAutomaticSize(package, 38.1, 38.1));
                SetAutomaticSizeControl(PassportCheckBox, PassportQuantityBox,
                    FindAutomaticSize(package, 34.9758, 44.958));
            }
            finally
            {
                _updatingAutomaticRushControls = false;
            }

            RefreshAutomaticRushIdPreview();
        }

        private static PhotoSizeItem FindAutomaticSize(PhotoPackage package, double widthMm, double heightMm)
        {
            return package.Items == null ? null : package.Items.FirstOrDefault(item =>
                Math.Abs(item.WidthMm - widthMm) < 0.05 && Math.Abs(item.HeightMm - heightMm) < 0.05);
        }

        private static void SetAutomaticSizeControl(CheckBox checkBox, TextBox quantityBox, PhotoSizeItem item)
        {
            checkBox.IsChecked = item != null;
            quantityBox.Text = (item == null ? 1 : Math.Max(1, item.Quantity)).ToString();
            quantityBox.IsEnabled = item != null;
        }

        private void AutomaticRushSize_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingAutomaticRushControls) return;
            UpdateAutomaticRushPackage();
        }

        private void AutomaticRushQuantity_Changed(object sender, TextChangedEventArgs e)
        {
            if (_updatingAutomaticRushControls) return;
            int ignored;
            if (!int.TryParse(((TextBox)sender).Text, out ignored) || ignored < 1 || ignored > 999) return;
            UpdateAutomaticRushPackage();
        }

        private void ResetAutomaticRushSizes_Click(object sender, RoutedEventArgs e)
        {
            _updatingAutomaticRushControls = true;
            try
            {
                TwoByTwoCheckBox.IsChecked = false;
                OneByOneCheckBox.IsChecked = false;
                OnePointFiveCheckBox.IsChecked = false;
                PassportCheckBox.IsChecked = false;
                TwoByTwoQuantityBox.Text = "1";
                OneByOneQuantityBox.Text = "1";
                OnePointFiveQuantityBox.Text = "1";
                PassportQuantityBox.Text = "1";
            }
            finally
            {
                _updatingAutomaticRushControls = false;
            }
            UpdateAutomaticRushPackage();
        }

        private void AutomaticRushPaper_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingAutomaticRushControls || SelectedPackage == null) return;
            var paper = AutomaticRushPaperBox.SelectedItem as PaperDefinition;
            if (paper == null) return;
            SelectedPackage.PaperName = paper.Name;
            EnsureFourTwoByTwoFit();
            SaveAndRefreshAutomaticRushId();
        }

        private void AutomaticRushOrientation_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingAutomaticRushControls || SelectedPackage == null) return;
            SelectedPackage.Orientation = Convert.ToString(AutomaticRushOrientationBox.SelectedItem) ?? "Portrait";
            EnsureFourTwoByTwoFit();
            SaveAndRefreshAutomaticRushId();
        }

        private void UpdateAutomaticRushPackage()
        {
            var package = SelectedPackage;
            if (package == null || !package.AutoFlowNoGap) return;

            TwoByTwoQuantityBox.IsEnabled = TwoByTwoCheckBox.IsChecked == true;
            OneByOneQuantityBox.IsEnabled = OneByOneCheckBox.IsChecked == true;
            OnePointFiveQuantityBox.IsEnabled = OnePointFiveCheckBox.IsChecked == true;
            PassportQuantityBox.IsEnabled = PassportCheckBox.IsChecked == true;

            var items = new ObservableCollection<PhotoSizeItem>();
            AddAutomaticSize(items, TwoByTwoCheckBox, TwoByTwoQuantityBox,
                "2 × 2 inches", 50.8, 50.8);
            AddAutomaticSize(items, OnePointFiveCheckBox, OnePointFiveQuantityBox,
                "1.5 × 1.5 inches", 38.1, 38.1);
            AddAutomaticSize(items, OneByOneCheckBox, OneByOneQuantityBox,
                "1 × 1 inch", 25.4, 25.4);
            AddAutomaticSize(items, PassportCheckBox, PassportQuantityBox,
                "Passport 1.377 × 1.77 inches", 34.9758, 44.958);
            package.Items = items;
            package.LayoutBoxes.Clear();
            package.PlacementMode = "Multiple Photos";
            EnsureFourTwoByTwoFit();
            SaveAndRefreshAutomaticRushId();
        }

        private void EnsureFourTwoByTwoFit()
        {
            var package = SelectedPackage;
            if (package == null || !package.AutoFlowNoGap ||
                FindAutomaticSize(package, 50.8, 50.8) == null)
                return;

            const double fourPicturesWideMm = 50.8 * 4;
            var margin = package.Borderless ? 0.0 : 3.4;
            var paper = PaperDefinition.Find(package.PaperName);
            var orientation = package.Orientation ?? "Portrait";
            var currentWidth = string.Equals(orientation, "Landscape", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(paper.WidthMm, paper.HeightMm)
                : Math.Min(paper.WidthMm, paper.HeightMm);
            if (!string.Equals(orientation, "Automatic", StringComparison.OrdinalIgnoreCase) &&
                currentWidth - margin * 2 >= fourPicturesWideMm - 0.01)
                return;

            if (Math.Max(paper.WidthMm, paper.HeightMm) - margin * 2 < fourPicturesWideMm - 0.01)
            {
                package.PaperName = "A4";
                paper = PaperDefinition.Find(package.PaperName);
            }
            package.Orientation = "Landscape";

            _updatingAutomaticRushControls = true;
            try
            {
                AutomaticRushPaperBox.SelectedItem = AutomaticRushPaperBox.Items
                    .Cast<PaperDefinition>().FirstOrDefault(item => item.Name == package.PaperName);
                AutomaticRushOrientationBox.SelectedItem = package.Orientation;
            }
            finally
            {
                _updatingAutomaticRushControls = false;
            }
        }

        private static void AddAutomaticSize(ICollection<PhotoSizeItem> items, CheckBox checkBox,
            TextBox quantityBox, string label, double widthMm, double heightMm)
        {
            if (checkBox.IsChecked != true) return;
            int quantity;
            if (!int.TryParse(quantityBox.Text, out quantity)) quantity = 1;
            items.Add(new PhotoSizeItem
            {
                Label = label,
                WidthMm = widthMm,
                HeightMm = heightMm,
                Quantity = Math.Max(1, Math.Min(999, quantity))
            });
        }

        private void SaveAndRefreshAutomaticRushId()
        {
            if (_savePackages != null) _savePackages();
            RefreshAutomaticRushIdPreview();
        }

        private void RefreshAutomaticRushIdPreview()
        {
            var package = SelectedPackage;
            if (package == null || !package.AutoFlowNoGap) return;

            var paths = _multiPhotoPaths == null
                ? new List<string>()
                : _multiPhotoPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            var paper = PaperDefinition.Find(package.PaperName);
            _multiPhotoLayout = PrintLayoutEngine.Create(package, paper, package.Orientation,
                "Multiple Photos", paths, 1, package.Borderless);
            _multiPhotoPageIndex = 0;
            _multiPhotoAmount = 1;
            _photoPackageMultipleLayout = false;
            if (paths.Count == 0) _multiPhotoPaths = null;

            AssignmentPreview.Package = null;
            AssignmentPreview.Package = package;
            AssignmentPreview.Visibility = Visibility.Collapsed;
            MultiPhotoPreview.Visibility = Visibility.Visible;
            MultiPhotoPreview.Layout = _multiPhotoLayout;
            MultiPhotoPreview.PageIndex = 0;
            MultiPhotoPreview.ShowGrid = package.ShowGrid;
            MultiPhotoPreview.ShowCutGuides = package.PrintCutGuides;
            MultiPhotoPreview.ShowPictureBorders = true;
            UpdateMultiPhotoPageDisplay();
            UpdateFillAllState();
            UpdateAssignmentStatus();
        }

        private void ImportImages_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import photos",
                Filter = "Photo files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true) AddPhotos(dialog.FileNames);
        }

        private void AddPhotos(IEnumerable<string> paths)
        {
            var addedPhotos = new List<PhotoFile>();
            foreach (var path in paths)
            {
                if (!File.Exists(path) || !SupportedExtensions.Contains(Path.GetExtension(path))) continue;
                if (Photos.Any(photo => string.Equals(photo.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var importedPhoto = new PhotoFile { Path = path };
                Photos.Add(importedPhoto);
                addedPhotos.Add(importedPhoto);
            }
            if (addedPhotos.Count > 0)
            {
                PhotoList.UnselectAll();
                foreach (var importedPhoto in addedPhotos) PhotoList.SelectedItems.Add(importedPhoto);
            }
            else if (PhotoList.SelectedItem == null && Photos.Count > 0)
            {
                PhotoList.SelectedIndex = 0;
            }
            UpdateFillAllState();
            UpdateAssignmentStatus();
        }

        private void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            var selected = PhotoList.SelectedItem as PhotoFile;
            if (selected != null) Photos.Remove(selected);
            ClearMultiPhotoPreview();
            if (PhotoList.SelectedItem == null && Photos.Count > 0) PhotoList.SelectedIndex = 0;
            UpdateFillAllState();
            UpdateAssignmentStatus();
        }

        private void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePhotoCopyControls();
            UpdateRushIdPackageCountControls();
            UpdateFillAllState();
        }

        private IList<PhotoFile> GetSelectedPhotos()
        {
            if (PhotoList == null || Photos == null) return new List<PhotoFile>();
            return Photos.Where(photo => PhotoList.SelectedItems.Contains(photo)).ToList();
        }

        private void UpdatePhotoCopyControls()
        {
            if (PhotoCopyCountBox == null ||
                !string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase)) return;

            var selectedPhotos = GetSelectedPhotos();
            var hasSelection = selectedPhotos.Count > 0;
            PhotoCopyCountBox.IsEnabled = hasSelection;
            PhotoCopyMinusButton.IsEnabled = hasSelection;
            PhotoCopyPlusButton.IsEnabled = hasSelection;

            _updatingPhotoCopyCount = true;
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
                _updatingPhotoCopyCount = false;
            }
        }

        private void PhotoCopyMinus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var selectedPhoto in GetSelectedPhotos())
                selectedPhoto.CopyCount = Math.Max(1, selectedPhoto.CopyCount - 1);
            UpdatePhotoCopyControls();
            UpdateFillAllState();
        }

        private void PhotoCopyPlus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var selectedPhoto in GetSelectedPhotos())
                selectedPhoto.CopyCount = Math.Min(999, Math.Max(1, selectedPhoto.CopyCount) + 1);
            UpdatePhotoCopyControls();
            UpdateFillAllState();
        }

        private void PhotoCopyCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingPhotoCopyCount ||
                !string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase)) return;

            int count;
            if (!int.TryParse(PhotoCopyCountBox.Text, out count) || count < 1 || count > 999) return;
            foreach (var selectedPhoto in GetSelectedPhotos()) selectedPhoto.CopyCount = count;
            UpdateFillAllState();
        }

        private void PhotoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var clickedItem = source == null
                ? null
                : ItemsControl.ContainerFromElement(PhotoList, source) as ListBoxItem;
            if (clickedItem != null) return;

            PhotoList.UnselectAll();
            UpdateFillAllState();
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                PhotoList == null || PhotoList.SelectedItems.Count == 0)
                return;

            var source = e.OriginalSource as DependencyObject;
            if (source == null) return;

            // A regular thumbnail click is handled by Extended selection mode:
            // click selects one, while Ctrl+click keeps multi-selection available.
            if (ItemsControl.ContainerFromElement(PhotoList, source) is ListBoxItem) return;

            // Keep the selection long enough for controls that operate on it.
            if (IsWithinElement(source, PhotoCopyPanel) ||
                IsWithinElement(source, FillAllBoxesButton) ||
                IsWithinElement(source, RemoveImageButton))
                return;

            PhotoList.UnselectAll();
            UpdateFillAllState();
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

        private void PackageCountMinus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var selectedPhoto in GetSelectedPhotos())
                selectedPhoto.PackageCount = Math.Max(1, selectedPhoto.PackageCount - 1);
            UpdateRushIdPackageCountControls();
            RefreshAutomaticRushIdSelectedPackageCounts();
            UpdateFillAllState();
        }

        private void PackageCountPlus_Click(object sender, RoutedEventArgs e)
        {
            foreach (var selectedPhoto in GetSelectedPhotos())
                selectedPhoto.PackageCount = Math.Min(999, Math.Max(1, selectedPhoto.PackageCount) + 1);
            UpdateRushIdPackageCountControls();
            RefreshAutomaticRushIdSelectedPackageCounts();
            UpdateFillAllState();
        }

        private void PhotoFit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingPhotoFit || !string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                SelectedPackage == null || PhotoFitBox.SelectedItem == null)
                return;

            ApplyPhotoFitToAllButton.IsEnabled = true;
            var mode = string.Equals(Convert.ToString(PhotoFitBox.SelectedItem), "Stretch",
                StringComparison.OrdinalIgnoreCase) ? "Stretch" : "Crop";
            var applied = _multiPhotoLayout != null && MultiPhotoPreview.Visibility == Visibility.Visible
                ? MultiPhotoPreview.SetSelectedPhotoFitMode(mode)
                : AssignmentPreview.SetSelectedPhotoFitMode(mode);
            if (applied && _multiPhotoLayout != null) UpdateMultiPhotoPageDisplay();
        }

        private void Preview_SelectedPhotoChanged(object sender, EventArgs e)
        {
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
        }

        private void ApplyPhotoFitToAll_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPackage == null || PhotoFitBox.SelectedItem == null) return;
            var mode = NormalizePhotoFitMode(Convert.ToString(PhotoFitBox.SelectedItem));
            SelectedPackage.PhotoFitMode = mode;
            AssignmentPreview.SetAllPhotoFitModes(mode);
            if (_multiPhotoLayout != null)
            {
                foreach (var placement in _multiPhotoLayout.Placements)
                    placement.PhotoFitMode = mode;
                MultiPhotoPreview.InvalidateVisual();
                UpdateMultiPhotoPageDisplay();
            }
            UpdatePhotoFitControl();
            if (_savePackages != null) _savePackages();
        }

        private void ApplyPictureBorderToAll_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPackage == null || PictureBorderBox.SelectedItem == null) return;
            var printBorder = string.Equals(Convert.ToString(PictureBorderBox.SelectedItem),
                "Print border", StringComparison.OrdinalIgnoreCase);
            SelectedPackage.PrintPictureBorder = printBorder;
            AssignmentPreview.SetAllPhotoPictureBorders(printBorder);
            AssignmentPreview.ShowPictureBorders = true;
            if (_multiPhotoLayout != null)
            {
                foreach (var placement in _multiPhotoLayout.Placements)
                    placement.PrintPictureBorder = printBorder;
                MultiPhotoPreview.ShowPictureBorders = true;
                MultiPhotoPreview.InvalidateVisual();
                UpdateMultiPhotoPageDisplay();
            }
            UpdatePictureBorderControl();
            if (_savePackages != null) _savePackages();
        }

        private void UpdatePhotoFitControl()
        {
            if (PhotoFitBox == null ||
                !string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase)) return;

            var usesMultiPreview = _multiPhotoLayout != null &&
                                   MultiPhotoPreview.Visibility == Visibility.Visible;
            var hasSelectedPhoto = usesMultiPreview
                ? MultiPhotoPreview.HasSelectedPhoto
                : AssignmentPreview.HasSelectedPhoto;
            var mode = usesMultiPreview
                ? MultiPhotoPreview.SelectedPhotoFitMode
                : AssignmentPreview.SelectedPhotoFitMode;
            if (!hasSelectedPhoto && SelectedPackage != null)
                mode = SelectedPackage.PhotoFitMode;
            if (!string.IsNullOrWhiteSpace(mode)) mode = NormalizePhotoFitMode(mode);

            _updatingPhotoFit = true;
            try
            {
                PhotoFitBox.SelectedItem = string.IsNullOrWhiteSpace(mode) ? null : mode;
                PhotoFitBox.IsEnabled = SelectedPackage != null;
                ApplyPhotoFitToAllButton.IsEnabled = SelectedPackage != null && PhotoFitBox.SelectedItem != null;
            }
            finally { _updatingPhotoFit = false; }
        }

        private void PictureBorder_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingPictureBorder || !string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                SelectedPackage == null || PictureBorderBox.SelectedItem == null)
                return;

            ApplyPictureBorderToAllButton.IsEnabled = true;
            var printBorder = string.Equals(
                Convert.ToString(PictureBorderBox.SelectedItem), "Print border",
                StringComparison.OrdinalIgnoreCase);
            var applied = _multiPhotoLayout != null && MultiPhotoPreview.Visibility == Visibility.Visible
                ? MultiPhotoPreview.SetSelectedPhotoPictureBorder(printBorder)
                : AssignmentPreview.SetSelectedPhotoPictureBorder(printBorder);
            if (applied && _multiPhotoLayout != null) UpdateMultiPhotoPageDisplay();
        }

        private void UpdatePictureBorderControl()
        {
            if (PictureBorderBox == null ||
                !string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase)) return;

            var usesMultiPreview = _multiPhotoLayout != null &&
                                   MultiPhotoPreview.Visibility == Visibility.Visible;
            var hasSelectedPhoto = usesMultiPreview
                ? MultiPhotoPreview.HasSelectedPhoto
                : AssignmentPreview.HasSelectedPhoto;
            var printBorder = usesMultiPreview
                ? MultiPhotoPreview.SelectedPhotoPrintsBorder
                : AssignmentPreview.SelectedPhotoPrintsBorder;
            if (!hasSelectedPhoto && SelectedPackage != null)
                printBorder = SelectedPackage.PrintPictureBorder;

            _updatingPictureBorder = true;
            try
            {
                PictureBorderBox.SelectedItem = printBorder.HasValue
                    ? printBorder.Value ? "Print border" : "Do not print"
                    : null;
                PictureBorderBox.IsEnabled = SelectedPackage != null;
                ApplyPictureBorderToAllButton.IsEnabled = SelectedPackage != null && PictureBorderBox.SelectedItem != null;
            }
            finally { _updatingPictureBorder = false; }
        }

        private void RushIdPictureBorder_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingRushIdPictureBorder ||
                !string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase) ||
                SelectedPackage == null)
                return;

            SelectedPackage.PrintPictureBorder = string.Equals(
                Convert.ToString(RushIdPictureBorderBox.SelectedItem), "Print border",
                StringComparison.OrdinalIgnoreCase);
            AssignmentPreview.SetAllPhotoPictureBorders(SelectedPackage.PrintPictureBorder);
            AssignmentPreview.ShowPictureBorders = true;
            if (_multiPhotoLayout != null)
                foreach (var placement in _multiPhotoLayout.Placements)
                    placement.PrintPictureBorder = SelectedPackage.PrintPictureBorder;
            MultiPhotoPreview.ShowPictureBorders = true;
            UpdateMultiPhotoPageDisplay();
            if (_savePackages != null) _savePackages();
        }

        private void RushIdPhotoFit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingRushIdPhotoFit ||
                !string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase) ||
                SelectedPackage == null)
                return;

            var mode = NormalizePhotoFitMode(Convert.ToString(RushIdPhotoFitBox.SelectedItem));
            SelectedPackage.PhotoFitMode = mode;
            AssignmentPreview.SetAllPhotoFitModes(mode);
            if (_multiPhotoLayout != null)
            {
                foreach (var placement in _multiPhotoLayout.Placements)
                    placement.PhotoFitMode = mode;
                MultiPhotoPreview.InvalidateVisual();
            }
            UpdateMultiPhotoPageDisplay();
            if (_savePackages != null) _savePackages();
        }

        private void RushIdPackageCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingRushIdPackageCount ||
                !string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase)) return;
            int count;
            if (!int.TryParse(RushIdPackageCountBox.Text, out count) || count < 1 || count > 999) return;
            foreach (var selectedPhoto in GetSelectedPhotos()) selectedPhoto.PackageCount = count;
            RefreshAutomaticRushIdSelectedPackageCounts();
            UpdateFillAllState();
        }

        private void RefreshAutomaticRushIdSelectedPackageCounts()
        {
            if (SelectedPackage == null || !SelectedPackage.AutoFlowNoGap ||
                SelectedPackage.Items == null || SelectedPackage.Items.Count == 0 ||
                GetSelectedPhotos().Count == 0)
                return;
            ApplyRushIdSelectionToPreview();
        }

        private void UpdateRushIdPackageCountControls()
        {
            if (RushIdPackageCountBox == null ||
                !string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase)) return;

            var selectedPhotos = GetSelectedPhotos();
            var hasSelection = selectedPhotos.Count > 0;
            RushIdPackageCountBox.IsEnabled = hasSelection;
            RushIdPackageMinusButton.IsEnabled = hasSelection;
            RushIdPackagePlusButton.IsEnabled = hasSelection;
            _updatingRushIdPackageCount = true;
            try
            {
                RushIdPackageCountBox.Text = !hasSelection
                    ? "1"
                    : selectedPhotos.All(photo => photo.PackageCount == selectedPhotos[0].PackageCount)
                        ? Math.Max(1, selectedPhotos[0].PackageCount).ToString()
                        : "";
            }
            finally
            {
                _updatingRushIdPackageCount = false;
            }
        }

        private void PhotoList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.A || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
            PhotoList.SelectAll();
            UpdateFillAllState();
            e.Handled = true;
        }

        private void FillAllBoxes_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPackage == null || PhotoList.SelectedItems.Count == 0) return;
            if (string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRushIdSelectionToPreview();
                return;
            }

            var selectedPhotos = GetSelectedPhotos();
            var selectedPaths = selectedPhotos.Select(photo => photo.Path).ToList();
            if (string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase))
            {
                ApplyPhotoPackageSelectionToPreview(selectedPhotos);
                UpdateFillAllState();
                UpdateAssignmentStatus();
                return;
            }

            ClearMultiPhotoPreview();
            AssignmentPreview.AssignPhotosToBoxes(selectedPaths);
            UpdateAssignmentStatus();
        }

        private void ApplyPhotoPackageSelectionToPreview(IList<PhotoFile> selectedPhotos)
        {
            if (selectedPhotos == null || selectedPhotos.Count == 0) return;

            var newPaths = selectedPhotos
                .SelectMany(photo => Enumerable.Repeat(photo.Path,
                    Math.Max(1, Math.Min(999, photo.CopyCount))))
                .ToList();
            var existingPaths = GetExistingPhotoPackagePaths();

            if (existingPaths.Count > 0)
            {
                var existingFitModes = GetExistingPhotoPackageFitModes();
                var existingOffsets = GetExistingPhotoPackageOffsets();
                var existingPictureBorders = GetExistingPhotoPackageBorders();
                existingPaths.AddRange(newPaths);
                ShowPhotoPackageMultiplePreview(existingPaths, existingFitModes, existingOffsets,
                    existingPictureBorders);
                return;
            }

            if (newPaths.Count == 1)
            {
                ClearMultiPhotoPreview();
                AssignmentPreview.AssignPhotoToAll(newPaths[0]);
                return;
            }

            ShowPhotoPackageMultiplePreview(newPaths);
        }

        private List<string> GetExistingPhotoPackagePaths()
        {
            if (_photoPackageMultipleLayout && _multiPhotoPaths != null)
                return _multiPhotoPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

            if (AssignmentPreview == null || AssignmentPreview.AssignedCount == 0)
                return new List<string>();

            return AssignmentPreview.GetSlotPhotoPaths()
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        }

        private List<string> GetExistingPhotoPackageFitModes()
        {
            if (_photoPackageMultipleLayout && _multiPhotoLayout != null)
                return _multiPhotoLayout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => NormalizePhotoFitMode(item.PhotoFitMode)).ToList();

            if (AssignmentPreview == null || AssignmentPreview.AssignedCount == 0)
                return new List<string>();

            var paths = AssignmentPreview.GetSlotPhotoPaths();
            var modes = AssignmentPreview.GetSlotPhotoFitModes();
            return paths.Select((path, index) => new { path, index })
                .Where(item => !string.IsNullOrWhiteSpace(item.path))
                .Select(item => NormalizePhotoFitMode(modes[item.index])).ToList();
        }

        private List<Point> GetExistingPhotoPackageOffsets()
        {
            if (_photoPackageMultipleLayout && _multiPhotoLayout != null)
                return _multiPhotoLayout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => new Point(item.PhotoOffsetX, item.PhotoOffsetY)).ToList();

            if (AssignmentPreview == null || AssignmentPreview.AssignedCount == 0)
                return new List<Point>();

            var paths = AssignmentPreview.GetSlotPhotoPaths();
            var offsets = AssignmentPreview.GetSlotPhotoOffsets();
            return paths.Select((path, index) => new { path, index })
                .Where(item => !string.IsNullOrWhiteSpace(item.path))
                .Select(item => offsets[item.index]).ToList();
        }

        private List<bool> GetExistingPhotoPackageBorders()
        {
            if (_photoPackageMultipleLayout && _multiPhotoLayout != null)
                return _multiPhotoLayout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => item.PrintPictureBorder).ToList();

            if (AssignmentPreview == null || AssignmentPreview.AssignedCount == 0)
                return new List<bool>();

            var paths = AssignmentPreview.GetSlotPhotoPaths();
            var borders = AssignmentPreview.GetSlotPictureBorders();
            return paths.Select((path, index) => new { path, index })
                .Where(item => !string.IsNullOrWhiteSpace(item.path))
                .Select(item => borders[item.index]).ToList();
        }

        private void ApplyRushIdSelectionToPreview()
        {
            if (!string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase) ||
                SelectedPackage == null || PhotoList == null || PhotoList.SelectedItems.Count == 0)
                return;

            var selectedPhotos = GetSelectedPhotos()
                .Where(photo => photo != null && !string.IsNullOrWhiteSpace(photo.Path))
                .ToList();
            var selectedPaths = selectedPhotos
                .SelectMany(photo => Enumerable.Repeat(photo.Path,
                    Math.Max(1, Math.Min(999, photo.PackageCount))))
                .ToList();
            if (selectedPaths.Count == 0) return;

            var existingPaths = GetExistingRushIdPackagePaths();
            if (SelectedPackage.AutoFlowNoGap && existingPaths.Count > 0)
            {
                foreach (var photo in selectedPhotos)
                    existingPaths.RemoveAll(path => string.Equals(path, photo.Path,
                        StringComparison.OrdinalIgnoreCase));
            }
            if (existingPaths.Count > 0)
            {
                existingPaths.AddRange(selectedPaths);
                selectedPaths = existingPaths;
            }

            SelectedPackage.PrintAmount = 1;
            if (_savePackages != null) _savePackages();

            if (SelectedPackage.AutoFlowNoGap || selectedPaths.Count > 1)
                ShowMultiPhotoPreview(selectedPaths, 1);
            else
            {
                ClearMultiPhotoPreview();
                AssignmentPreview.AssignPhotosToBoxes(selectedPaths);
            }
            UpdateFillAllState();
            UpdateAssignmentStatus();
        }

        private List<string> GetExistingRushIdPackagePaths()
        {
            if (!string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase))
                return new List<string>();

            if (!_photoPackageMultipleLayout && _multiPhotoPaths != null && _multiPhotoPaths.Count > 0)
                return _multiPhotoPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

            if (AssignmentPreview == null || AssignmentPreview.AssignedCount == 0)
                return new List<string>();

            var assignedPath = AssignmentPreview.GetSlotPhotoPaths()
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            return string.IsNullOrWhiteSpace(assignedPath)
                ? new List<string>()
                : new List<string> { assignedPath };
        }

        private void ShowMultiPhotoPreview(IList<string> selectedPaths, int amount)
        {
            _multiPhotoPaths = selectedPaths.ToList();
            _multiPhotoAmount = Math.Max(1, amount);
            _photoPackageMultipleLayout = false;
            _multiPhotoPageIndex = 0;
            var package = SelectedPackage;
            var paper = PaperDefinition.Find(package.PaperName);
            _multiPhotoLayout = PrintLayoutEngine.Create(package, paper, package.Orientation,
                "Multiple Photos", _multiPhotoPaths, _multiPhotoAmount, package.Borderless);

            AssignmentPreview.Visibility = Visibility.Collapsed;
            MultiPhotoPreview.Visibility = Visibility.Visible;
            MultiPhotoPreview.Layout = _multiPhotoLayout;
            MultiPhotoPreview.PageIndex = 0;
            MultiPhotoPreview.ShowGrid = package.ShowGrid;
            MultiPhotoPreview.ShowCutGuides = package.PrintCutGuides;
            MultiPhotoPreview.ShowPictureBorders = true;
            UpdateMultiPhotoPageDisplay();
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
        }

        private void ShowPhotoPackageMultiplePreview(IList<string> selectedPaths,
            IList<string> existingFitModes = null, IList<Point> existingOffsets = null,
            IList<bool> existingPictureBorders = null)
        {
            _multiPhotoPaths = selectedPaths.ToList();
            _multiPhotoAmount = 1;
            _photoPackageMultipleLayout = true;
            _multiPhotoPageIndex = 0;
            var package = SelectedPackage;
            var paper = PaperDefinition.Find(package.PaperName);
            _multiPhotoLayout = PrintLayoutEngine.Create(package, paper, package.Orientation,
                "Photo Package Multiple", _multiPhotoPaths, 1, package.Borderless);
            ApplyPhotoPlacementSettings(_multiPhotoLayout, existingFitModes, existingOffsets,
                existingPictureBorders);

            AssignmentPreview.Visibility = Visibility.Collapsed;
            MultiPhotoPreview.Visibility = Visibility.Visible;
            MultiPhotoPreview.Layout = _multiPhotoLayout;
            MultiPhotoPreview.PageIndex = 0;
            MultiPhotoPreview.ShowGrid = package.ShowGrid;
            MultiPhotoPreview.ShowCutGuides = package.PrintCutGuides;
            MultiPhotoPreview.ShowPictureBorders = true;
            UpdateMultiPhotoPageDisplay();
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
        }

        private static void ApplyPhotoPlacementSettings(PrintLayout layout, IList<string> fitModes,
            IList<Point> offsets, IList<bool> pictureBorders)
        {
            if (layout == null) return;
            var assignedPlacements = layout.Placements
                .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath)).ToList();
            for (var index = 0; index < assignedPlacements.Count; index++)
            {
                if (fitModes != null && index < fitModes.Count)
                    assignedPlacements[index].PhotoFitMode = NormalizePhotoFitMode(fitModes[index]);
                if (offsets != null && index < offsets.Count)
                {
                    assignedPlacements[index].PhotoOffsetX = Math.Max(-1, Math.Min(1, offsets[index].X));
                    assignedPlacements[index].PhotoOffsetY = Math.Max(-1, Math.Min(1, offsets[index].Y));
                }
                if (pictureBorders != null && index < pictureBorders.Count)
                    assignedPlacements[index].PrintPictureBorder = pictureBorders[index];
            }
        }

        private static string NormalizePhotoFitMode(string mode)
        {
            return string.Equals(mode, "Stretch", StringComparison.OrdinalIgnoreCase)
                ? "Stretch"
                : "Crop";
        }

        private void ClearMultiPhotoPreview()
        {
            _multiPhotoPaths = null;
            _multiPhotoLayout = null;
            _multiPhotoAmount = 1;
            _photoPackageMultipleLayout = false;
            _multiPhotoPageIndex = 0;
            if (AssignmentPreview == null) return;
            AssignmentPreview.Visibility = Visibility.Visible;
            MultiPhotoPreview.Visibility = Visibility.Collapsed;
            MultiPageStatus.Visibility = Visibility.Collapsed;
            PackagePageSidebar.Visibility = Visibility.Collapsed;
            PackagePagePreviewList.ItemsSource = null;
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
        }

        private void UpdateMultiPhotoPageDisplay()
        {
            if (_multiPhotoLayout == null) return;
            if (_multiPhotoLayout.PageCount < 1)
            {
                _multiPhotoPageIndex = 0;
                MultiPhotoPreview.PageIndex = 0;
                MultiPageStatus.Text = "Select at least one ID size";
                MultiPageStatus.Visibility = Visibility.Visible;
                PackagePageSidebar.Visibility = Visibility.Collapsed;
                PackagePagePreviewList.ItemsSource = null;
                return;
            }
            MultiPhotoPreview.PageIndex = _multiPhotoPageIndex;
            MultiPageStatus.Text = string.Format("Page {0} of {1}",
                _multiPhotoPageIndex + 1, _multiPhotoLayout.PageCount);
            MultiPageStatus.Visibility = Visibility.Visible;

            var showSidebar = _multiPhotoLayout.PageCount >= 2;
            PackagePageSidebar.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
            _updatingPackagePageSidebar = true;
            try
            {
                if (!showSidebar)
                {
                    PackagePagePreviewList.ItemsSource = null;
                    return;
                }

                PackagePagePreviewList.ItemsSource = Enumerable.Range(0, _multiPhotoLayout.PageCount)
                    .Select(index => new PagePreviewItem
                    {
                        Layout = _multiPhotoLayout,
                        PageIndex = index,
                        Label = string.Format("Page {0}", index + 1),
                        ShowGrid = SelectedPackage.ShowGrid,
                        ShowCutGuides = SelectedPackage.PrintCutGuides,
                        ShowPictureBorders = string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                                             SelectedPackage.PrintPictureBorder
                    }).ToList();
                PackagePagePreviewList.SelectedIndex = _multiPhotoPageIndex;
                PackagePagePreviewList.ScrollIntoView(PackagePagePreviewList.SelectedItem);
            }
            finally
            {
                _updatingPackagePageSidebar = false;
            }
        }

        private void PackagePagePreviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingPackagePageSidebar || PackagePagePreviewList.SelectedIndex < 0 || _multiPhotoLayout == null) return;
            _multiPhotoPageIndex = PackagePagePreviewList.SelectedIndex;
            MultiPhotoPreview.PageIndex = _multiPhotoPageIndex;
            MultiPageStatus.Text = string.Format("Page {0} of {1}",
                _multiPhotoPageIndex + 1, _multiPhotoLayout.PageCount);
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
        }

        private void PhotoList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var photo = PhotoList.SelectedItem as PhotoFile;
            if (photo == null) return;
            var data = new DataObject(PackageAssignmentPreviewControl.PhotoPathFormat, photo.Path);
            DragDrop.DoDragDrop(PhotoList, data, DragDropEffects.Copy);
        }

        private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ClearMultiPhotoPreview();
            AssignmentPreview.Package = SelectedPackage;
            SelectedPackageTitle.Text = SelectedPackage == null ? "No package selected" : SelectedPackage.Name;
            if (string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase) && SelectedPackage != null)
            {
                var rushFitMode = NormalizePhotoFitMode(SelectedPackage.PhotoFitMode);
                SelectedPackage.PhotoFitMode = rushFitMode;
                _updatingRushIdPhotoFit = true;
                try { RushIdPhotoFitBox.SelectedItem = rushFitMode; }
                finally { _updatingRushIdPhotoFit = false; }
                AssignmentPreview.PhotoFitMode = rushFitMode;
                _updatingRushIdPictureBorder = true;
                try
                {
                    RushIdPictureBorderBox.SelectedItem = SelectedPackage.PrintPictureBorder
                        ? "Print border"
                        : "Do not print";
                }
                finally { _updatingRushIdPictureBorder = false; }
                AssignmentPreview.ShowPictureBorders = SelectedPackage.PrintPictureBorder;
            }
            if (string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase) && SelectedPackage != null)
            {
                var mode = string.Equals(SelectedPackage.PhotoFitMode, "Stretch",
                    StringComparison.OrdinalIgnoreCase) ? "Stretch" : "Crop";
                SelectedPackage.PhotoFitMode = mode;
                _updatingPhotoFit = true;
                try { PhotoFitBox.SelectedItem = mode; }
                finally { _updatingPhotoFit = false; }
                AssignmentPreview.PhotoFitMode = mode;
                _updatingPictureBorder = true;
                try
                {
                    PictureBorderBox.SelectedItem = SelectedPackage.PrintPictureBorder
                        ? "Print border"
                        : "Do not print";
                }
                finally { _updatingPictureBorder = false; }
                AssignmentPreview.ShowPictureBorders = true;
            }
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
            UpdateRushIdPackageCountControls();
            UpdatePackageState();
            UpdateAssignmentStatus();
        }

        private void EditPackage_Click(object sender, RoutedEventArgs e)
        {
            var original = SelectedPackage;
            if (original == null) return;

            var originalName = original.Name ?? "";
            var editor = new PackageEditorWindow(original.EditableCopy(), false) { Owner = this };
            if (editor.ShowDialog() != true) return;

            var edited = editor.Package;
            var nameChanged = !string.Equals(originalName.Trim(), (edited.Name ?? "").Trim(),
                StringComparison.Ordinal);

            if (nameChanged)
            {
                edited.Id = Guid.NewGuid().ToString("N");
                edited.CreatedUtc = DateTime.UtcNow;
                _allPackages.Add(edited);
                if (MatchesCategory(edited))
                {
                    _categoryPackages.Add(edited);
                    PackageList.SelectedItem = edited;
                }
            }
            else
            {
                var allIndex = _allPackages.IndexOf(original);
                var categoryIndex = _categoryPackages.IndexOf(original);
                if (allIndex >= 0) _allPackages[allIndex] = edited;

                if (MatchesCategory(edited))
                {
                    if (categoryIndex >= 0) _categoryPackages[categoryIndex] = edited;
                    else _categoryPackages.Add(edited);
                    PackageList.SelectedItem = edited;
                }
                else if (categoryIndex >= 0)
                {
                    _categoryPackages.RemoveAt(categoryIndex);
                    if (_categoryPackages.Count > 0)
                        PackageList.SelectedIndex = Math.Min(categoryIndex, _categoryPackages.Count - 1);
                }
            }

            UpdatePackageState();
        }

        private void UndoRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_removedPackages.Count == 0) return;
            var removed = _removedPackages.Pop();
            var allIndex = Math.Max(0, Math.Min(removed.AllPackagesIndex, _allPackages.Count));
            var categoryIndex = Math.Max(0, Math.Min(removed.CategoryIndex, _categoryPackages.Count));

            _allPackages.Insert(allIndex, removed.Package);
            _categoryPackages.Insert(categoryIndex, removed.Package);
            PackageList.SelectedItem = removed.Package;
            UpdatePackageState();
        }

        private void RemovePackage_Click(object sender, RoutedEventArgs e)
        {
            var package = SelectedPackage;
            if (package == null) return;

            var answer = MessageBox.Show("Remove ‘" + package.Name + "’ from saved packages?",
                "Remove package", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            var categoryIndex = _categoryPackages.IndexOf(package);
            var allPackagesIndex = _allPackages.IndexOf(package);
            _removedPackages.Push(new RemovedPackageState
            {
                Package = package,
                AllPackagesIndex = allPackagesIndex,
                CategoryIndex = categoryIndex
            });
            _allPackages.Remove(package);
            _categoryPackages.Remove(package);

            if (_categoryPackages.Count > 0)
                PackageList.SelectedIndex = Math.Min(categoryIndex, _categoryPackages.Count - 1);
            UpdatePackageState();
        }

        private void UpdatePackageState()
        {
            if (PackageList == null) return;
            var hasPackages = _categoryPackages != null && _categoryPackages.Count > 0;
            var hasSelection = SelectedPackage != null;
            NoPackagesText.Visibility = hasPackages ? Visibility.Collapsed : Visibility.Visible;
            OpenWorkspaceButton.IsEnabled = hasSelection;
            SavePdfButton.IsEnabled = hasSelection && HasPreviewAssignments();
            PrinterSettingsButton.IsEnabled = hasSelection;
            PrintButton.IsEnabled = hasSelection && Photos != null && Photos.Count > 0 &&
                (!SelectedPackage.AutoFlowNoGap || HasPreviewAssignments());
            EditPackageButton.IsEnabled = hasSelection;
            UndoRemoveButton.IsEnabled = _removedPackages.Count > 0;
            RemovePackageButton.IsEnabled = hasSelection;
            UpdateFillAllState();
        }

        private void UpdateFillAllState()
        {
            if (FillAllBoxesButton == null || PhotoList == null) return;
            var selectedPhotos = GetSelectedPhotos();
            var selectedCount = selectedPhotos.Count;
            var totalCopies = selectedPhotos.Sum(photo => Math.Max(1, photo.CopyCount));
            var isPhotoCategory = string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase);
            var isRushIdCategory = string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase);
            var totalRushIdPackages = selectedPhotos.Sum(photo => Math.Max(1, photo.PackageCount));
            var hasExistingRushIdPackages = isRushIdCategory &&
                ((_multiPhotoPaths != null && _multiPhotoPaths.Count > 0) ||
                 (AssignmentPreview != null && AssignmentPreview.AssignedCount > 0));
            var hasExistingPhotoAssignments = isPhotoCategory &&
                ((_photoPackageMultipleLayout && _multiPhotoPaths != null && _multiPhotoPaths.Count > 0) ||
                 (AssignmentPreview != null && AssignmentPreview.AssignedCount > 0));
            var hasAutomaticSizes = SelectedPackage == null || !SelectedPackage.AutoFlowNoGap ||
                                    (SelectedPackage.Items != null && SelectedPackage.Items.Count > 0);
            FillAllBoxesButton.IsEnabled = SelectedPackage != null && selectedCount > 0 && hasAutomaticSizes;
            FillAllBoxesButton.Content = selectedCount == 0
                ? "Select Pictures to Fill"
                : !hasAutomaticSizes
                    ? "Select at Least One ID Size"
                : SelectedPackage != null && SelectedPackage.AutoFlowNoGap
                    ? string.Format("{0} {1} Rush ID Package{2}",
                        hasExistingRushIdPackages ? "Update" : "Fill", totalRushIdPackages,
                        totalRushIdPackages == 1 ? "" : "s")
                : isRushIdCategory
                    ? string.Format("{0} {1} Rush ID Package{2}",
                        hasExistingRushIdPackages ? "Add" : "Fill", totalRushIdPackages,
                        totalRushIdPackages == 1 ? "" : "s")
                : hasExistingPhotoAssignments
                    ? string.Format("Add {0} Picture{1} to Next Box", totalCopies,
                        totalCopies == 1 ? "" : "s")
                : isPhotoCategory && (selectedCount > 1 || totalCopies > 1)
                    ? string.Format("Fill {0} Picture Copies One per Box", totalCopies)
                : selectedCount == 1
                    ? "Fill 1 Picture into All Boxes"
                    : string.Format("Fill {0} Pictures One per Box", selectedCount);
            FillAllBoxesButton.ToolTip = hasExistingPhotoAssignments
                ? "Keep existing pictures and continue into the next empty box or page"
                : isRushIdCategory
                    ? hasExistingRushIdPackages
                        ? "Keep existing Rush ID packages and append duplicates for the selected image"
                        : "Create the chosen number of complete packages for each selected image"
                : selectedCount <= 1 && totalCopies <= 1
                ? "One selected picture fills every package picture box"
                : "Picture copies fill one box each and continue onto new pages";
            if (RushIdPackageCountPanel != null)
                RushIdPackageCountPanel.Visibility =
                    string.Equals(_category, "Rush ID", StringComparison.OrdinalIgnoreCase) &&
                    SelectedPackage != null
                        ? Visibility.Visible
                        : Visibility.Collapsed;
        }

        private void AssignmentPreview_PhotoAssigned(object sender, EventArgs e)
        {
            UpdatePhotoFitControl();
            UpdatePictureBorderControl();
            UpdateAssignmentStatus();
        }

        private void UpdateAssignmentStatus()
        {
            if (AssignmentStatus == null) return;
            if (_multiPhotoLayout != null && _multiPhotoPaths != null)
                AssignmentStatus.Text = _photoPackageMultipleLayout
                    ? string.Format("{0} picture copy/copies • one per picture box across {1} page(s)",
                        _multiPhotoPaths.Count, _multiPhotoLayout.PageCount)
                    : string.Format("{0} Rush ID package(s) across {1} page(s)",
                        _multiPhotoPaths.Count * _multiPhotoAmount, _multiPhotoLayout.PageCount);
            else if (SelectedPackage != null && SelectedPackage.AutoFlowNoGap && _multiPhotoLayout != null)
                AssignmentStatus.Text = string.Format(
                    "{0} picture box(es) across {1} page(s) - select an image and click Fill",
                    _multiPhotoLayout.Placements.Count, _multiPhotoLayout.PageCount);
            else
                AssignmentStatus.Text = string.Format("{0} imported image(s) • {1} picture box(es) assigned",
                    Photos == null ? 0 : Photos.Count, AssignmentPreview == null ? 0 : AssignmentPreview.AssignedCount);
            if (PrintButton != null)
                PrintButton.IsEnabled = SelectedPackage != null && Photos != null && Photos.Count > 0 &&
                    (!SelectedPackage.AutoFlowNoGap || HasPreviewAssignments());
            if (SavePdfButton != null)
                SavePdfButton.IsEnabled = SelectedPackage != null && HasPreviewAssignments();
        }

        private bool HasPreviewAssignments()
        {
            return (_multiPhotoLayout != null && _multiPhotoLayout.PageCount > 0 &&
                    _multiPhotoLayout.Placements.Any(item => !string.IsNullOrWhiteSpace(item.PhotoPath))) ||
                   (AssignmentPreview != null && AssignmentPreview.AssignedCount > 0);
        }

        private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var package = SelectedPackage;
            if (package == null) return;
            var useMultiplePhotos = _multiPhotoPaths != null;
            var photoPaths = useMultiplePhotos ? _multiPhotoPaths : Photos.Select(photo => photo.Path).ToList();
            var slotPhotos = useMultiplePhotos || AssignmentPreview.AssignedCount == 0
                ? null
                : AssignmentPreview.GetSlotPhotoPaths();
            var workspace = new PrintWorkspaceWindow(package,
                photoPaths, slotPhotos, _savePackages, false, useMultiplePhotos) { Owner = this };
            workspace.ShowDialog();
        }

        private void PrinterSettings_Click(object sender, RoutedEventArgs e)
        {
            var package = SelectedPackage;
            if (package == null) return;
            var settings = new PrinterSettingsWindow(package, _savePackages) { Owner = this };
            settings.ShowDialog();
        }

        private void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            var package = SelectedPackage;
            var layout = BuildCurrentPreviewLayout();
            if (package == null || layout == null || layout.PageCount < 1)
            {
                MessageBox.Show(this, "Fill the package preview before saving a PDF.", "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fileName = string.IsNullOrWhiteSpace(package.Name) ? "Photo Package" : package.Name.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
            var dialog = new SaveFileDialog
            {
                Title = "Save Package Preview as PDF",
                Filter = "PDF document (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = fileName + ".pdf"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                PdfExportService.Save(dialog.FileName, layout, package.PrintCutGuides, true);
                MessageBox.Show(this, "PDF saved successfully.", "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the PDF.\n\n" + ex.Message, "Save PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private PrintLayout BuildCurrentPreviewLayout()
        {
            if (_multiPhotoLayout != null) return _multiPhotoLayout;
            var package = SelectedPackage;
            if (package == null || AssignmentPreview == null || AssignmentPreview.AssignedCount == 0)
                return null;

            var paths = AssignmentPreview.GetSlotPhotoPaths();
            var paper = PaperDefinition.Find(package.PaperName);
            var placement = string.Equals(_category, "Photo", StringComparison.OrdinalIgnoreCase)
                ? "Multiple Photos"
                : package.PlacementMode;
            var layout = PrintLayoutEngine.Create(package, paper, package.Orientation, placement,
                paths, 1, package.Borderless);
            var offsets = AssignmentPreview.GetSlotPhotoOffsets();
            var fitModes = AssignmentPreview.GetSlotPhotoFitModes();
            var pictureBorders = AssignmentPreview.GetSlotPictureBorders();
            for (var index = 0; index < layout.Placements.Count; index++)
            {
                if (index < offsets.Count)
                {
                    layout.Placements[index].PhotoOffsetX = Math.Max(-1, Math.Min(1, offsets[index].X));
                    layout.Placements[index].PhotoOffsetY = Math.Max(-1, Math.Min(1, offsets[index].Y));
                }
                if (index < fitModes.Count)
                    layout.Placements[index].PhotoFitMode = NormalizePhotoFitMode(fitModes[index]);
                if (index < pictureBorders.Count)
                    layout.Placements[index].PrintPictureBorder = pictureBorders[index];
            }
            return layout;
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            var package = SelectedPackage;
            if (package == null) return;
            if (package.AutoFlowNoGap && !HasPreviewAssignments())
            {
                MessageBox.Show(this, "Select at least one ID size and fill the package with a photo before printing.",
                    "Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var useMultiplePhotos = _multiPhotoPaths != null;
            var photoPaths = useMultiplePhotos ? _multiPhotoPaths : Photos.Select(photo => photo.Path).ToList();
            var slotPhotos = useMultiplePhotos || AssignmentPreview.AssignedCount == 0
                ? null
                : AssignmentPreview.GetSlotPhotoPaths();
            var placementOffsets = useMultiplePhotos && _multiPhotoLayout != null
                ? _multiPhotoLayout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => new Point(item.PhotoOffsetX, item.PhotoOffsetY)).ToList()
                : AssignmentPreview.GetSlotPhotoOffsets();
            var placementFitModes = useMultiplePhotos && _multiPhotoLayout != null
                ? _multiPhotoLayout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => NormalizePhotoFitMode(item.PhotoFitMode)).ToList()
                : AssignmentPreview.GetSlotPhotoFitModes();
            var placementPictureBorders = useMultiplePhotos && _multiPhotoLayout != null
                ? _multiPhotoLayout.Placements
                    .Where(item => !string.IsNullOrWhiteSpace(item.PhotoPath))
                    .Select(item => item.PrintPictureBorder).ToList()
                : AssignmentPreview.GetSlotPictureBorders();
            if (_savePackages != null) _savePackages();
            PrintWorkspaceWindow.PrintUsingSavedSettings(package,
                photoPaths, slotPhotos, this, useMultiplePhotos,
                useMultiplePhotos ? (int?)_multiPhotoAmount : null,
                _photoPackageMultipleLayout ? "Photo Package Multiple" : null,
                placementOffsets, placementFitModes, placementPictureBorders);
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

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
    }
}
