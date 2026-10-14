using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using RCPhotoStudio.Models;

namespace RCPhotoStudio.Services
{
    public class PackageRepository
    {
        private readonly string _folder;
        private readonly string _file;

        public PackageRepository()
        {
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RC Photo Studio");
            _file = Path.Combine(_folder, "packages.json");
        }

        public ObservableCollection<PhotoPackage> Load()
        {
            try
            {
                if (File.Exists(_file))
                {
                    List<PhotoPackage> packages;
                    bool packagesChanged;
                    using (var stream = File.OpenRead(_file))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(List<PhotoPackage>));
                        packages = serializer.ReadObject(stream) as List<PhotoPackage>;
                        packagesChanged = false;
                        if (packages != null)
                        {
                            foreach (var package in packages)
                            {
                                if (package.Items == null) package.Items = new ObservableCollection<PhotoSizeItem>();
                                if (package.LayoutBoxes == null) package.LayoutBoxes = new ObservableCollection<PackageLayoutBox>();
                                if (string.IsNullOrWhiteSpace(package.MeasurementUnit)) package.MeasurementUnit = "in";
                                if (string.IsNullOrWhiteSpace(package.PhotoFitMode)) package.PhotoFitMode = "Crop";
                                if (string.IsNullOrWhiteSpace(package.RushIdPhotoArrangement))
                                {
                                    package.RushIdPhotoArrangement = "One photo per package";
                                    packagesChanged = true;
                                }
                                if (package.PrintAmount <= 0) package.PrintAmount = 1;
                                if (package.PrinterSettingsVersion <= 0)
                                {
                                    package.PrinterCopies = 1;
                                    package.PrinterPrintInColor = true;
                                    package.PrinterSettingsVersion = 1;
                                }
                                if (package.PrinterCopies <= 0) package.PrinterCopies = 1;
                            }
                            packagesChanged = AutomaticRushIdPackage.Normalize(packages) || packagesChanged;
                        }
                    }
                    if (packages != null)
                    {
                        if (packagesChanged)
                        {
                            try { Save(packages); }
                            catch
                            {
                                // Keep the successfully loaded in-memory library even if migration
                                // cannot be persisted until the next explicit save.
                            }
                        }
                        return new ObservableCollection<PhotoPackage>(packages);
                    }
                }
            }
            catch
            {
                // A damaged settings file should not prevent the studio from starting.
            }

            var defaults = DefaultPackageCatalog.Load();
            AutomaticRushIdPackage.Normalize(defaults);
            Save(defaults);
            return defaults;
        }

        public void Save(IEnumerable<PhotoPackage> packages)
        {
            Directory.CreateDirectory(_folder);
            var temp = _file + ".tmp";
            using (var stream = File.Create(temp))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<PhotoPackage>));
                serializer.WriteObject(stream, packages.ToList());
            }
            if (File.Exists(_file)) File.Delete(_file);
            File.Move(temp, _file);
        }

    }
}
