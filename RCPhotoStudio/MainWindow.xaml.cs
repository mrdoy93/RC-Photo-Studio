using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RCPhotoStudio.Models;
using RCPhotoStudio.Services;
using RCPhotoStudio.Views;

namespace RCPhotoStudio
{
    public partial class MainWindow : Window
    {
        private readonly PackageRepository _repository = new PackageRepository();
        private readonly ObservableCollection<PhotoPackage> _packages;
        private readonly ICollectionView _view;

        public MainWindow()
        {
            InitializeComponent();
            WindowSizingService.Maximize(this);
            _packages = _repository.Load();
            _view = CollectionViewSource.GetDefaultView(_packages);
            _view.Filter = FilterPackage;
            _view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            PackageList.ItemsSource = _view;
            ThemeToggle.IsChecked = ThemeService.IsDark;
            ThemeToggle.Content = ThemeService.IsDark ? "☀  Light mode" : "🌙  Night mode";
            UpdateCount();
        }

        private bool FilterPackage(object value)
        {
            var package = value as PhotoPackage;
            if (package == null) return false;
            var term = SearchBox == null ? "" : (SearchBox.Text ?? "").Trim();
            var selected = CategoryFilter == null ? null : CategoryFilter.SelectedItem as ComboBoxItem;
            var category = selected == null ? "All packages" : Convert.ToString(selected.Content);
            var categoryMatches = category == "All packages" || package.Category == category;
            var searchMatches = term.Length == 0 || package.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0;
            return categoryMatches && searchMatches;
        }

        private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ThemeService.Apply(ThemeToggle.IsChecked == true);
            ThemeToggle.Content = ThemeService.IsDark ? "☀  Light mode" : "🌙  Night mode";
        }

        private void RushId_Click(object sender, RoutedEventArgs e) { OpenCategoryWindow("Rush ID"); }
        private void PhotoPackages_Click(object sender, RoutedEventArgs e) { OpenCategoryWindow("Photo"); }

        private void TicketPrinting_Click(object sender, RoutedEventArgs e)
        {
            var window = new TicketPrintingWindow { Owner = this };
            Hide();
            try
            {
                window.ShowDialog();
            }
            finally
            {
                Show();
                Activate();
            }
        }

        private void OpenCategoryWindow(string category)
        {
            var window = new CategoryPackagesWindow(_packages, category, () => _repository.Save(_packages)) { Owner = this };
            Hide();
            try
            {
                window.ShowDialog();
                SaveAndRefresh();
            }
            finally
            {
                Show();
                Activate();
            }
        }

        private void SelectCategory(string category)
        {
            foreach (ComboBoxItem item in CategoryFilter.Items)
                if (Convert.ToString(item.Content) == category) { CategoryFilter.SelectedItem = item; break; }
        }

        private void CreatePackage_Click(object sender, RoutedEventArgs e)
        {
            var package = new PhotoPackage
            {
                Category = "Photo",
                CustomKind = "Photo"
            };
            package.Items.Add(new PhotoSizeItem());
            var editor = new PackageEditorWindow(package, true) { Owner = this };
            Hide();
            try
            {
                if (editor.ShowDialog() == true)
                {
                    _packages.Add(editor.Package);
                    SearchBox.Text = "";
                    SelectCategory("Photo");
                    SaveAndRefresh();
                }
            }
            finally
            {
                Show();
                Activate();
            }
        }

        private string GetSelectedCategory()
        {
            var selected = CategoryFilter.SelectedItem as ComboBoxItem;
            return selected == null ? "All packages" : Convert.ToString(selected.Content);
        }

        private void EditPackage_Click(object sender, RoutedEventArgs e)
        {
            var original = (sender as FrameworkElement).Tag as PhotoPackage;
            if (original == null) return;
            var editor = new PackageEditorWindow(original.EditableCopy(), false) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                var index = _packages.IndexOf(original);
                _packages[index] = editor.Package;
                SaveAndRefresh();
            }
        }

        private void DuplicatePackage_Click(object sender, RoutedEventArgs e)
        {
            var package = (sender as FrameworkElement).Tag as PhotoPackage;
            if (package == null) return;
            _packages.Add(package.CloneAsNew());
            SaveAndRefresh();
        }

        private void DeletePackage_Click(object sender, RoutedEventArgs e)
        {
            var package = (sender as FrameworkElement).Tag as PhotoPackage;
            if (package == null) return;
            var answer = MessageBox.Show("Delete ‘" + package.Name + "’?\n\nPrevious exported or printed files will not be affected.",
                "Delete package", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                _packages.Remove(package);
                SaveAndRefresh();
            }
        }

        private void OpenPackage_Click(object sender, RoutedEventArgs e)
        {
            var package = (sender as FrameworkElement).Tag as PhotoPackage;
            if (package != null) OpenWorkspace(package);
        }

        private void OpenPrinterWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var package = _view.Cast<PhotoPackage>().FirstOrDefault() ?? _packages.FirstOrDefault();
            if (package == null)
            {
                MessageBox.Show("Create a package first.", "RC Photo Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenWorkspace(package, true);
        }

        private void OpenWorkspace(PhotoPackage package, bool hideMainMenu = false)
        {
            var workspace = new PrintWorkspaceWindow(package, null, null,
                () => _repository.Save(_packages), false, false, true) { Owner = this };
            if (hideMainMenu) Hide();

            try
            {
                workspace.ShowDialog();
            }
            finally
            {
                _repository.Save(_packages);
                if (hideMainMenu)
                {
                    Show();
                    Activate();
                }
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { RefreshView(); }
        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { RefreshView(); }

        private void RefreshView()
        {
            if (_view == null) return;
            _view.Refresh();
            UpdateCount();
        }

        private void SaveAndRefresh()
        {
            _repository.Save(_packages);
            RefreshView();
        }

        private void UpdateCount()
        {
            if (PackageCountText == null || _view == null) return;
            PackageCountText.Text = _view.Cast<object>().Count() + " package(s) shown";
        }
    }
}
