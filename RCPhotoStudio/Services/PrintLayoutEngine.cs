using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using RCPhotoStudio.Models;

namespace RCPhotoStudio.Services
{
    public class PrintPlacement
    {
        public int PageIndex { get; set; }
        public Rect BoundsMm { get; set; }
        public string PhotoPath { get; set; }
        public string Label { get; set; }
        public double RotationDegrees { get; set; }
        public double PhotoOffsetX { get; set; }
        public double PhotoOffsetY { get; set; }
        public string PhotoFitMode { get; set; }
        public bool PrintPictureBorder { get; set; }
        public bool AutoRotatePhotoToBox { get; set; }
        public double CornerRadiusMm { get; set; }
    }

    public class PrintLayout
    {
        public PrintLayout()
        {
            Placements = new List<PrintPlacement>();
        }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public double MarginMm { get; set; }
        public int PageCount { get; set; }
        public List<PrintPlacement> Placements { get; private set; }
    }

    public static class PrintLayoutEngine
    {
        public static PrintLayout Create(PhotoPackage package, PaperDefinition paper, string orientation,
            string placementMode, IList<string> photoPaths, int amount, bool borderless)
        {
            return CreateInternal(package, paper, orientation, placementMode, photoPaths, amount,
                borderless, false);
        }

        private static PrintLayout CreateInternal(PhotoPackage package, PaperDefinition paper, string orientation,
            string placementMode, IList<string> photoPaths, int amount, bool borderless, bool rotateTemplate)
        {
            if (orientation == "Automatic")
            {
                var portrait = CreateInternal(package, paper, "Portrait", placementMode, photoPaths, amount,
                    borderless, false);
                var landscapeLayout = CreateInternal(package, paper, "Landscape", placementMode, photoPaths, amount,
                    borderless, false);
                return landscapeLayout.PageCount < portrait.PageCount ? landscapeLayout : portrait;
            }

            var landscape = orientation == "Landscape";
            var pageWidth = landscape ? Math.Max(paper.WidthMm, paper.HeightMm) : Math.Min(paper.WidthMm, paper.HeightMm);
            var pageHeight = landscape ? Math.Min(paper.WidthMm, paper.HeightMm) : Math.Max(paper.WidthMm, paper.HeightMm);
            var margin = borderless
                ? 0.0
                : package.AutoFlowNoGap && string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase)
                    ? 3.4
                    : 5.0;
            var gap = 2.0;
            var result = new PrintLayout
            {
                PaperWidthMm = pageWidth,
                PaperHeightMm = pageHeight,
                MarginMm = margin
            };

            if (package.AutoFlowNoGap &&
                string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase))
                return CreateAutomaticRushIdLayout(package, result, placementMode, photoPaths, amount,
                    pageWidth, pageHeight, margin);

            if (string.Equals(placementMode, "Photo Package Multiple", StringComparison.OrdinalIgnoreCase))
                return CreatePhotoPackageMultipleLayout(package, paper, orientation, result, photoPaths,
                    pageWidth, pageHeight, borderless);

            if (package.LayoutBoxes != null && package.LayoutBoxes.Count > 0)
            {
                var layoutBoxes = GetEffectiveLayoutBoxes(package, paper, orientation, rotateTemplate,
                    pageWidth, pageHeight, margin);
                if (string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(placementMode, "Multiple Photos", StringComparison.OrdinalIgnoreCase))
                    return CreateRushIdMultiplePhotoLayout(package, layoutBoxes, result, photoPaths, amount,
                        pageWidth, pageHeight, margin);

                var customPhotoIndex = 0;
                var pageCount = Math.Max(1, amount);
                for (var customPage = 0; customPage < pageCount; customPage++)
                {
                    foreach (var box in layoutBoxes)
                    {
                        var boxBounds = new Rect(box.Xmm, box.Ymm, box.WidthMm, box.HeightMm);
                        var width = Math.Max(1, Math.Min(boxBounds.Width, pageWidth));
                        var height = Math.Max(1, Math.Min(boxBounds.Height, pageHeight));
                        var customX = Math.Max(0, Math.Min(boxBounds.X, pageWidth - width));
                        var customY = Math.Max(0, Math.Min(boxBounds.Y, pageHeight - height));
                        var photoPath = photoPaths.Count == 0 ? null :
                            (placementMode == "Repeat One Photo" ? photoPaths[0] : photoPaths[customPhotoIndex++ % photoPaths.Count]);
                        result.Placements.Add(new PrintPlacement
                        {
                            PageIndex = customPage,
                            BoundsMm = new Rect(customX, customY, width, height),
                            PhotoPath = photoPath,
                            Label = box.Label,
                            RotationDegrees = box.RotationDegrees,
                            PhotoFitMode = package.PhotoFitMode,
                            PrintPictureBorder = package.PrintPictureBorder,
                            AutoRotatePhotoToBox = IsPhotoPackage(package)
                        });
                    }
                }
                result.PageCount = pageCount;
                return result;
            }

            var requested = BuildRequestedSlots(package, placementMode, Math.Max(1, amount), pageWidth, pageHeight, margin, gap);
            if (rotateTemplate)
            {
                foreach (var slot in requested)
                {
                    var width = slot.WidthMm;
                    slot.WidthMm = slot.HeightMm;
                    slot.HeightMm = width;
                }
            }
            if (requested.Count == 0) return result;

            var page = 0;
            var x = margin;
            var y = margin;
            var rowHeight = 0.0;
            var photoIndex = 0;

            foreach (var slot in requested)
            {
                var width = Math.Min(slot.WidthMm, pageWidth - margin * 2);
                var height = Math.Min(slot.HeightMm, pageHeight - margin * 2);

                if (x + width > pageWidth - margin + 0.01)
                {
                    x = margin;
                    y += rowHeight + gap;
                    rowHeight = 0;
                }
                if (y + height > pageHeight - margin + 0.01)
                {
                    page++;
                    x = margin;
                    y = margin;
                    rowHeight = 0;
                }

                var photoPath = photoPaths.Count == 0 ? null :
                    (placementMode == "Repeat One Photo" ? photoPaths[0] : photoPaths[photoIndex++ % photoPaths.Count]);
                result.Placements.Add(new PrintPlacement
                {
                    PageIndex = page,
                    BoundsMm = new Rect(x, y, width, height),
                    PhotoPath = photoPath,
                    Label = slot.Label,
                    PhotoFitMode = package.PhotoFitMode,
                    PrintPictureBorder = package.PrintPictureBorder,
                    AutoRotatePhotoToBox = IsPhotoPackage(package)
                });
                x += width + gap;
                rowHeight = Math.Max(rowHeight, height);
            }

            result.PageCount = result.Placements.Count == 0 ? 0 : page + 1;
            return result;
        }

        private static PrintLayout CreateAutomaticRushIdLayout(PhotoPackage package, PrintLayout result,
            string placementMode, IList<string> photoPaths, int amount,
            double pageWidth, double pageHeight, double margin)
        {
            var sizes = package.Items == null
                ? new List<PhotoSizeItem>()
                : package.Items.Where(item => item != null && item.Quantity > 0 &&
                    item.WidthMm > 0 && item.HeightMm > 0)
                    // Keep 1 x 1 immediately after 2 x 2 so it fills the open space
                    // beside the 2 x 2 row before the remaining sizes are laid out.
                    .OrderBy(GetAutomaticRushIdSizeOrder)
                    .ToList();
            if (sizes.Count == 0)
            {
                result.PageCount = 0;
                return result;
            }
            var pictures = (photoPaths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            if (pictures.Count == 0) pictures.Add(null);

            var requested = new List<Tuple<PhotoSizeItem, string, int, double>>();
            var packageIndex = 0;
            var mixImagesByPackage = string.Equals(placementMode,
                "Rush ID Multiple Pictures", StringComparison.OrdinalIgnoreCase);
            for (var set = 0; set < Math.Max(1, amount); set++)
            {
                if (mixImagesByPackage)
                {
                    var pictureIndex = 0;
                    var oneByOne = sizes.FirstOrDefault(IsOneByOneSize);
                    var primarySizes = sizes.Where(size => !IsOneByOneSize(size)).ToList();
                    if (primarySizes.Count == 0 && oneByOne != null) primarySizes.Add(oneByOne);
                    foreach (var primarySize in primarySizes)
                    {
                        var groupSizes = new List<PhotoSizeItem> { primarySize };
                        if (oneByOne != null && !ReferenceEquals(primarySize, oneByOne) &&
                            ShouldMixOneByOneWithSize(package, primarySize))
                            groupSizes.Add(oneByOne);
                        var picture = pictures[pictureIndex++ % pictures.Count];
                        var groupPackageHeight = CalculateAutomaticRushIdPackageHeight(
                            groupSizes, Math.Min(pageWidth - margin, margin + 203.2) - margin);
                        foreach (var size in groupSizes)
                        {
                            for (var copy = 0; copy < size.Quantity; copy++)
                                requested.Add(Tuple.Create(size, picture, packageIndex, groupPackageHeight));
                        }
                        packageIndex++;
                    }
                    continue;
                }

                var sharedPackageHeight = CalculateAutomaticRushIdPackageHeight(sizes,
                    Math.Min(pageWidth - margin, margin + 203.2) - margin);
                foreach (var picture in pictures)
                {
                    foreach (var size in sizes)
                    {
                        for (var copy = 0; copy < size.Quantity; copy++)
                            requested.Add(Tuple.Create(size, picture, packageIndex, sharedPackageHeight));
                    }
                    packageIndex++;
                }
            }

            var page = 0;
            var x = margin;
            var y = margin;
            var rowHeight = 0.0;
            var rowItemCount = 0;
            PhotoSizeItem previousSize = null;
            var previousPackageIndex = -1;
            var mixedOneByOneInColumn = 0;
            var mixedOneByOneColumnX = 0.0;
            var mixedOneByOneColumnY = 0.0;
            var mixedOneByOneHorizontal = false;
            const double eightInchesMm = 203.2;
            var right = Math.Min(pageWidth - margin, margin + eightInchesMm);
            var bottom = pageHeight - margin;
            var usablePageHeight = bottom - margin;

            foreach (var request in requested)
            {
                var size = request.Item1;
                var width = Math.Max(1, Math.Min(size.WidthMm, pageWidth - margin * 2));
                var height = Math.Max(1, Math.Min(size.HeightMm, pageHeight - margin * 2));
                var beginsNewPackage = previousPackageIndex >= 0 && request.Item3 != previousPackageIndex;
                if (beginsNewPackage)
                {
                    if (rowHeight > 0 || x > margin + 0.01 || mixedOneByOneInColumn > 0)
                        y += rowHeight;
                    x = margin;
                    rowHeight = 0;
                    rowItemCount = 0;
                    mixedOneByOneInColumn = 0;
                    mixedOneByOneHorizontal = false;
                    previousSize = null;

                    if (request.Item4 <= usablePageHeight + 0.01 &&
                        y + request.Item4 > bottom + 0.01)
                    {
                        page++;
                        y = margin;
                    }
                }
                var sizeChanged = previousSize != null &&
                    (Math.Abs(previousSize.WidthMm - size.WidthMm) > 0.01 ||
                     Math.Abs(previousSize.HeightMm - size.HeightMm) > 0.01);
                var mixedOneByOne = sizes.Count > 1 && IsOneByOneSize(size);
                if (sizeChanged && (x > margin + 0.01 || mixedOneByOneInColumn > 0) && !mixedOneByOne)
                {
                    x = margin;
                    y += rowHeight;
                    rowHeight = 0;
                    rowItemCount = 0;
                    mixedOneByOneInColumn = 0;
                }

                if (mixedOneByOne)
                {
                    if (!mixedOneByOneHorizontal && mixedOneByOneInColumn == 0 &&
                        x + width > right + 0.01)
                    {
                        x = margin;
                        y += rowHeight;
                        rowHeight = 0;
                        mixedOneByOneHorizontal = true;
                    }
                    if (mixedOneByOneHorizontal)
                    {
                        if (x + width > right + 0.01)
                        {
                            x = margin;
                            y += rowHeight;
                            rowHeight = 0;
                        }
                        if (y + height > bottom + 0.01)
                        {
                            page++;
                            x = margin;
                            y = margin;
                            rowHeight = 0;
                        }
                        result.Placements.Add(new PrintPlacement
                        {
                            PageIndex = page,
                            BoundsMm = new Rect(x, y, width, height),
                            PhotoPath = request.Item2,
                            Label = size.Label,
                            PhotoFitMode = package.PhotoFitMode,
                            PrintPictureBorder = package.PrintPictureBorder
                        });
                        x += width;
                        rowHeight = Math.Max(rowHeight, height);
                        previousSize = size;
                        previousPackageIndex = request.Item3;
                        continue;
                    }

                    if (mixedOneByOneInColumn == 0)
                    {
                        if (y + height > bottom + 0.01)
                        {
                            page++;
                            x = margin;
                            y = margin;
                            rowHeight = 0;
                        }
                        mixedOneByOneColumnX = x;
                        mixedOneByOneColumnY = y;
                    }
                    else if (mixedOneByOneColumnY + height * 2 > bottom + 0.01)
                    {
                        page++;
                        x = margin;
                        y = margin;
                        rowHeight = 0;
                        mixedOneByOneColumnX = x;
                        mixedOneByOneColumnY = y;
                        mixedOneByOneInColumn = 0;
                    }

                    var oneByOneY = mixedOneByOneColumnY + mixedOneByOneInColumn * height;
                    result.Placements.Add(new PrintPlacement
                    {
                        PageIndex = page,
                        BoundsMm = new Rect(mixedOneByOneColumnX, oneByOneY, width, height),
                        PhotoPath = request.Item2,
                        Label = size.Label,
                        PhotoFitMode = package.PhotoFitMode,
                        PrintPictureBorder = package.PrintPictureBorder
                    });
                    mixedOneByOneInColumn++;
                    rowHeight = Math.Max(rowHeight,
                        oneByOneY + height - y);
                    if (mixedOneByOneInColumn == 2)
                    {
                        x = mixedOneByOneColumnX + width;
                        mixedOneByOneInColumn = 0;
                    }
                    previousSize = size;
                    previousPackageIndex = request.Item3;
                    continue;
                }

                var maximumColumns = GetAutomaticRushIdMaximumColumns(size, sizes.Count > 1);

                if (rowItemCount >= maximumColumns || x + width > right + 0.01)
                {
                    x = margin;
                    y += rowHeight;
                    rowHeight = 0;
                    rowItemCount = 0;
                }
                if (y + height > bottom + 0.01)
                {
                    page++;
                    x = margin;
                    y = margin;
                    rowHeight = 0;
                    rowItemCount = 0;
                }

                result.Placements.Add(new PrintPlacement
                {
                    PageIndex = page,
                    BoundsMm = new Rect(x, y, width, height),
                    PhotoPath = request.Item2,
                    Label = size.Label,
                    PhotoFitMode = package.PhotoFitMode,
                    PrintPictureBorder = package.PrintPictureBorder
                });
                x += width;
                rowHeight = Math.Max(rowHeight, height);
                rowItemCount++;
                previousSize = size;
                previousPackageIndex = request.Item3;
            }

            result.PageCount = result.Placements.Count == 0 ? 0 : page + 1;
            return result;
        }

        private static double CalculateAutomaticRushIdPackageHeight(IList<PhotoSizeItem> sizes,
            double printableWidth)
        {
            var x = 0.0;
            var y = 0.0;
            var rowHeight = 0.0;
            var rowItemCount = 0;
            var mixedOneByOneInColumn = 0;
            var mixedOneByOneColumnY = 0.0;
            var mixedOneByOneHorizontal = false;
            PhotoSizeItem previousSize = null;

            foreach (var size in sizes)
            {
                var width = Math.Min(size.WidthMm, printableWidth);
                var height = size.HeightMm;
                var sizeChanged = previousSize != null &&
                    (Math.Abs(previousSize.WidthMm - size.WidthMm) > 0.01 ||
                     Math.Abs(previousSize.HeightMm - size.HeightMm) > 0.01);
                var mixedOneByOne = sizes.Count > 1 && IsOneByOneSize(size);
                if (sizeChanged && (x > 0.01 || mixedOneByOneInColumn > 0) && !mixedOneByOne)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                    rowItemCount = 0;
                    mixedOneByOneInColumn = 0;
                }

                for (var copy = 0; copy < size.Quantity; copy++)
                {
                    if (mixedOneByOne)
                    {
                        if (!mixedOneByOneHorizontal && mixedOneByOneInColumn == 0 &&
                            x + width > printableWidth + 0.01)
                        {
                            x = 0;
                            y += rowHeight;
                            rowHeight = 0;
                            mixedOneByOneHorizontal = true;
                        }
                        if (mixedOneByOneHorizontal)
                        {
                            if (x + width > printableWidth + 0.01)
                            {
                                x = 0;
                                y += rowHeight;
                                rowHeight = 0;
                            }
                            x += width;
                            rowHeight = Math.Max(rowHeight, height);
                            continue;
                        }

                        if (mixedOneByOneInColumn == 0)
                        {
                            mixedOneByOneColumnY = y;
                        }
                        var oneByOneY = mixedOneByOneColumnY + mixedOneByOneInColumn * height;
                        rowHeight = Math.Max(rowHeight, oneByOneY + height - y);
                        mixedOneByOneInColumn++;
                        if (mixedOneByOneInColumn == 2)
                        {
                            x += width;
                            mixedOneByOneInColumn = 0;
                        }
                        continue;
                    }

                    var maximumColumns = GetAutomaticRushIdMaximumColumns(size, sizes.Count > 1);
                    if (rowItemCount >= maximumColumns || x + width > printableWidth + 0.01)
                    {
                        x = 0;
                        y += rowHeight;
                        rowHeight = 0;
                        rowItemCount = 0;
                    }
                    x += width;
                    rowHeight = Math.Max(rowHeight, height);
                    rowItemCount++;
                }
                previousSize = size;
            }

            return y + rowHeight;
        }

        private static int GetAutomaticRushIdMaximumColumns(PhotoSizeItem size, bool hasOtherSizes)
        {
            if (Math.Abs(size.WidthMm - 50.8) < 0.05 && Math.Abs(size.HeightMm - 50.8) < 0.05)
                return 4;
            if (Math.Abs(size.WidthMm - 25.4) < 0.05 && Math.Abs(size.HeightMm - 25.4) < 0.05)
                return hasOtherSizes ? 2 : int.MaxValue;
            return int.MaxValue;
        }

        private static bool IsOneByOneSize(PhotoSizeItem size)
        {
            return size != null && Math.Abs(size.WidthMm - 25.4) < 0.05 &&
                   Math.Abs(size.HeightMm - 25.4) < 0.05;
        }

        private static int GetAutomaticRushIdSizeOrder(PhotoSizeItem size)
        {
            if (size == null) return 4;
            if (Math.Abs(size.WidthMm - 50.8) < 0.05 && Math.Abs(size.HeightMm - 50.8) < 0.05)
                return 0;
            if (IsOneByOneSize(size)) return 1;
            if (Math.Abs(size.WidthMm - 38.1) < 0.05 && Math.Abs(size.HeightMm - 38.1) < 0.05)
                return 2;
            if (Math.Abs(size.WidthMm - 34.9758) < 0.05 && Math.Abs(size.HeightMm - 44.958) < 0.05)
                return 3;
            return 4;
        }

        private static bool ShouldMixOneByOneWithSize(PhotoPackage package, PhotoSizeItem size)
        {
            if (package == null || size == null) return false;
            if (Math.Abs(size.WidthMm - 50.8) < 0.05 && Math.Abs(size.HeightMm - 50.8) < 0.05)
                return package.MixOneByOneWithTwoByTwo;
            if (Math.Abs(size.WidthMm - 38.1) < 0.05 && Math.Abs(size.HeightMm - 38.1) < 0.05)
                return package.MixOneByOneWithOnePointFive;
            if (Math.Abs(size.WidthMm - 34.9758) < 0.05 && Math.Abs(size.HeightMm - 44.958) < 0.05)
                return package.MixOneByOneWithPassport;
            return false;
        }

        private static PrintLayout CreatePhotoPackageMultipleLayout(PhotoPackage package, PaperDefinition paper,
            string orientation, PrintLayout result, IList<string> photoPaths,
            double pageWidth, double pageHeight, bool borderless)
        {
            var boxes = new List<PackageLayoutBox>();
            if (package.LayoutBoxes != null && package.LayoutBoxes.Count > 0)
            {
                boxes.AddRange(package.LayoutBoxes);
            }
            else
            {
                var templateMode = string.Equals(package.PlacementMode, "Photo Package Multiple",
                    StringComparison.OrdinalIgnoreCase) ? "Repeat One Photo" : package.PlacementMode;
                var template = Create(package, paper, orientation, templateMode,
                    new List<string>(), 1, borderless);
                foreach (var placement in template.Placements.Where(item => item.PageIndex == 0))
                {
                    boxes.Add(new PackageLayoutBox
                    {
                        Label = placement.Label,
                        Xmm = placement.BoundsMm.X,
                        Ymm = placement.BoundsMm.Y,
                        WidthMm = placement.BoundsMm.Width,
                        HeightMm = placement.BoundsMm.Height,
                        RotationDegrees = placement.RotationDegrees
                    });
                }
            }

            if (boxes.Count == 0)
            {
                result.PageCount = 0;
                return result;
            }

            var pictures = photoPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            var pageCount = Math.Max(1, (int)Math.Ceiling(pictures.Count / (double)boxes.Count));
            var photoIndex = 0;
            for (var page = 0; page < pageCount; page++)
            {
                foreach (var box in boxes)
                {
                    var width = Math.Max(1, Math.Min(box.WidthMm, pageWidth));
                    var height = Math.Max(1, Math.Min(box.HeightMm, pageHeight));
                    result.Placements.Add(new PrintPlacement
                    {
                        PageIndex = page,
                        BoundsMm = new Rect(
                            Math.Max(0, Math.Min(box.Xmm, pageWidth - width)),
                            Math.Max(0, Math.Min(box.Ymm, pageHeight - height)),
                            width,
                            height),
                        PhotoPath = photoIndex < pictures.Count ? pictures[photoIndex++] : null,
                        Label = box.Label,
                        RotationDegrees = box.RotationDegrees,
                        PhotoFitMode = package.PhotoFitMode,
                        PrintPictureBorder = package.PrintPictureBorder,
                        AutoRotatePhotoToBox = IsPhotoPackage(package)
                    });
                }
            }

            result.PageCount = pageCount;
            return result;
        }

        private static PrintLayout CreateRushIdMultiplePhotoLayout(PhotoPackage package,
            IList<PackageLayoutBox> layoutBoxes, PrintLayout result,
            IList<string> photoPaths, int amount, double pageWidth, double pageHeight, double margin)
        {
            var boxes = layoutBoxes.ToList();
            var minY = boxes.Min(box => box.Ymm);
            var maxY = boxes.Max(box => box.Ymm + box.HeightMm);
            var groupHeight = Math.Max(1.0, maxY - minY);
            var pageBottom = pageHeight - margin;
            var pictures = photoPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            if (pictures.Count == 0) pictures.Add(null);

            var page = 0;
            var groupTop = minY;
            var firstGroupOnPage = true;
            for (var set = 0; set < Math.Max(1, amount); set++)
            {
                foreach (var picture in pictures)
                {
                    if (!firstGroupOnPage && groupTop + groupHeight > pageBottom + 0.01)
                    {
                        page++;
                        groupTop = minY;
                        firstGroupOnPage = true;
                    }

                    var offsetY = groupTop - minY;
                    foreach (var box in boxes)
                    {
                        var width = Math.Max(1, Math.Min(box.WidthMm, pageWidth));
                        var height = Math.Max(1, Math.Min(box.HeightMm, pageHeight));
                        result.Placements.Add(new PrintPlacement
                        {
                            PageIndex = page,
                            BoundsMm = new Rect(
                                Math.Max(0, Math.Min(box.Xmm, pageWidth - width)),
                                Math.Max(0, Math.Min(box.Ymm + offsetY, pageHeight - height)),
                                width,
                                height),
                            PhotoPath = picture,
                            Label = box.Label,
                            RotationDegrees = box.RotationDegrees,
                            PhotoFitMode = package.PhotoFitMode,
                            PrintPictureBorder = package.PrintPictureBorder,
                            AutoRotatePhotoToBox = IsPhotoPackage(package)
                        });
                    }

                    groupTop += groupHeight;
                    firstGroupOnPage = false;
                }
            }

            result.PageCount = result.Placements.Count == 0 ? 0 : page + 1;
            return result;
        }

        private static List<PackageLayoutBox> GetEffectiveLayoutBoxes(PhotoPackage package,
            PaperDefinition paper, string orientation, bool rotateTemplate,
            double pageWidth, double pageHeight, double margin)
        {
            var boxes = package.LayoutBoxes.Select(box =>
            {
                var bounds = rotateTemplate
                    ? RotateBoxToPaperOrientation(box, paper, orientation)
                    : new Rect(box.Xmm, box.Ymm, box.WidthMm, box.HeightMm);
                return new PackageLayoutBox
                {
                    Id = box.Id,
                    Label = box.Label,
                    Xmm = bounds.X,
                    Ymm = bounds.Y,
                    WidthMm = bounds.Width,
                    HeightMm = bounds.Height,
                    RotationDegrees = box.RotationDegrees
                };
            }).ToList();

            if (!string.Equals(package.Category, "Rush ID", StringComparison.OrdinalIgnoreCase) ||
                boxes.Count == 0)
                return boxes;

            var minX = boxes.Min(box => box.Xmm);
            var minY = boxes.Min(box => box.Ymm);
            var maxX = boxes.Max(box => box.Xmm + box.WidthMm);
            var groupWidth = Math.Max(1.0, maxX - minX);
            var printableWidth = Math.Max(0.0, pageWidth - margin * 2);
            var targetLeft = groupWidth <= printableWidth
                ? margin + (printableWidth - groupWidth) / 2
                : (pageWidth - groupWidth) / 2;
            var offsetX = targetLeft - minX;
            var offsetY = margin - minY;

            foreach (var box in boxes)
            {
                box.Xmm += offsetX;
                box.Ymm += offsetY;
            }
            return boxes;
        }

        private static bool IsPhotoPackage(PhotoPackage package)
        {
            return string.Equals(package.Category, "Photo", StringComparison.OrdinalIgnoreCase) ||
                   (string.Equals(package.Category, "Custom", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(package.CustomKind, "Photo", StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasSinglePictureBox(PhotoPackage package, string placementMode)
        {
            if (package == null) return false;
            if (string.Equals(placementMode, "Fill Page", StringComparison.OrdinalIgnoreCase)) return false;
            if (package.LayoutBoxes != null && package.LayoutBoxes.Count > 0)
                return package.LayoutBoxes.Count == 1;
            if (package.Items == null || package.Items.Count == 0) return true;
            return package.Items.Sum(item => Math.Max(1, item.Quantity)) == 1;
        }

        private static bool TryGetFirstPhotoOrientation(IList<string> photoPaths, out bool landscape)
        {
            landscape = false;
            var path = photoPaths == null
                ? null
                : photoPaths.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item) && File.Exists(item));
            if (path == null) return false;

            try
            {
                using (var image = System.Drawing.Image.FromFile(path))
                {
                    var width = image.Width;
                    var height = image.Height;
                    const int orientationId = 0x0112;
                    if (image.PropertyIdList.Contains(orientationId))
                    {
                        var orientation = image.GetPropertyItem(orientationId).Value[0];
                        if (orientation >= 5 && orientation <= 8)
                        {
                            var swap = width;
                            width = height;
                            height = swap;
                        }
                    }
                    if (width == height) return false;
                    landscape = width > height;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static Rect RotateBoxToPaperOrientation(PackageLayoutBox box, PaperDefinition paper,
            string targetOrientation)
        {
            var shortSide = Math.Min(paper.WidthMm, paper.HeightMm);
            if (string.Equals(targetOrientation, "Landscape", StringComparison.OrdinalIgnoreCase))
            {
                return new Rect(box.Ymm, shortSide - box.Xmm - box.WidthMm,
                    box.HeightMm, box.WidthMm);
            }

            return new Rect(shortSide - box.Ymm - box.HeightMm, box.Xmm,
                box.HeightMm, box.WidthMm);
        }

        private static List<PhotoSizeItem> BuildRequestedSlots(PhotoPackage package, string mode, int amount,
            double pageWidth, double pageHeight, double margin, double gap)
        {
            var slots = new List<PhotoSizeItem>();
            var source = package.Items != null && package.Items.Count > 0
                ? package.Items.ToList()
                : new List<PhotoSizeItem> { new PhotoSizeItem() };

            if (mode == "Fill Page")
            {
                var item = source[0];
                var columns = Math.Max(1, (int)Math.Floor((pageWidth - margin * 2 + gap) / (item.WidthMm + gap)));
                var rows = Math.Max(1, (int)Math.Floor((pageHeight - margin * 2 + gap) / (item.HeightMm + gap)));
                var count = amount <= 1 ? columns * rows : amount;
                for (var i = 0; i < count; i++) slots.Add(item.Clone());
                return slots;
            }

            for (var set = 0; set < amount; set++)
                foreach (var item in source)
                    for (var i = 0; i < Math.Max(1, item.Quantity); i++)
                        slots.Add(item.Clone());
            return slots;
        }
    }
}
