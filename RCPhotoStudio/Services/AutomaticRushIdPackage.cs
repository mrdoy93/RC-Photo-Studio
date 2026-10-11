using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RCPhotoStudio.Models;

namespace RCPhotoStudio.Services
{
    internal static class AutomaticRushIdPackage
    {
        public const string PackageId = "automatic-rush-id-package";

        public static PhotoPackage Create()
        {
            return new PhotoPackage
            {
                Id = PackageId,
                Name = "Automatic Rush ID",
                Category = "Rush ID",
                CustomKind = "Rush ID",
                PaperName = "A4",
                Orientation = "Portrait",
                PlacementMode = "Multiple Photos",
                PhotoFitMode = "Crop",
                PrintAmount = 1,
                AutoFlowNoGap = true,
                AutomaticRushLayoutVersion = 2,
                Items = new ObservableCollection<PhotoSizeItem>(),
                LayoutBoxes = new ObservableCollection<PackageLayoutBox>()
            };
        }

        public static bool Normalize(IList<PhotoPackage> packages)
        {
            if (packages == null) return false;

            var automatic = packages.FirstOrDefault(IsAutomatic);
            var changed = false;
            for (var index = packages.Count - 1; index >= 0; index--)
            {
                var package = packages[index];
                if (!IsRushId(package) || ReferenceEquals(package, automatic)) continue;
                packages.RemoveAt(index);
                changed = true;
            }

            if (automatic == null)
            {
                automatic = Create();
                packages.Add(automatic);
                changed = true;
            }
            else
            {
                if (!automatic.AutoFlowNoGap) changed = true;
                automatic.Id = PackageId;
                automatic.Name = "Automatic Rush ID";
                automatic.Category = "Rush ID";
                automatic.CustomKind = "Rush ID";
                automatic.PlacementMode = "Multiple Photos";
                automatic.AutoFlowNoGap = true;
                if (automatic.AutomaticRushLayoutVersion < 2)
                {
                    automatic.PaperName = "A4";
                    automatic.Orientation = "Portrait";
                    automatic.AutomaticRushLayoutVersion = 2;
                    changed = true;
                }
                if (automatic.Items == null)
                    automatic.Items = new ObservableCollection<PhotoSizeItem>();
                if (automatic.LayoutBoxes == null || automatic.LayoutBoxes.Count > 0)
                {
                    automatic.LayoutBoxes = new ObservableCollection<PackageLayoutBox>();
                    changed = true;
                }
            }

            return changed;
        }

        public static bool IsAutomatic(PhotoPackage package)
        {
            return package != null &&
                   (package.AutoFlowNoGap || string.Equals(package.Id, PackageId, StringComparison.Ordinal));
        }

        private static bool IsRushId(PhotoPackage package)
        {
            return package != null &&
                   (string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(package.Category, "Custom", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(package.CustomKind, "Rush ID", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
