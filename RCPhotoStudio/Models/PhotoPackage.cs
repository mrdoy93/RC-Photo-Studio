using System;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace RCPhotoStudio.Models
{
    [DataContract]
    public class PhotoPackage
    {
        public PhotoPackage()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "New Package";
            Category = "Rush ID";
            CustomKind = "Rush ID";
            PaperName = "A4";
            Orientation = "Portrait";
            PlacementMode = "Repeat One Photo";
            RushIdPhotoArrangement = "One photo per package";
            MixOneByOneWithTwoByTwo = true;
            PhotoFitMode = "Crop";
            MeasurementUnit = "in";
            PrintAmount = 1;
            PrinterCopies = 1;
            PrinterPrintInColor = true;
            PrinterSettingsVersion = 1;
            Items = new ObservableCollection<PhotoSizeItem>();
            LayoutBoxes = new ObservableCollection<PackageLayoutBox>();
            CreatedUtc = DateTime.UtcNow;
        }

        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Category { get; set; }
        [DataMember] public string CustomKind { get; set; }
        [DataMember] public decimal Price { get; set; }
        [DataMember] public string IconPath { get; set; }
        [DataMember] public string PaperName { get; set; }
        [DataMember] public string Orientation { get; set; }
        [DataMember] public string PlacementMode { get; set; }
        [DataMember] public string RushIdPhotoArrangement { get; set; }
        [DataMember] public bool MixOneByOneWithTwoByTwo { get; set; }
        [DataMember] public bool MixOneByOneWithOnePointFive { get; set; }
        [DataMember] public bool MixOneByOneWithPassport { get; set; }
        [DataMember] public string PhotoFitMode { get; set; }
        [DataMember] public string MeasurementUnit { get; set; }
        [DataMember] public string DefaultPrinter { get; set; }
        [DataMember] public string PrinterDevModeBase64 { get; set; }
        [DataMember] public string PrinterPaperSizeName { get; set; }
        [DataMember] public string PrinterPaperSourceName { get; set; }
        [DataMember] public int PrinterMediaTypeId { get; set; }
        [DataMember] public string PrinterMediaTypeName { get; set; }
        [DataMember] public int PrinterResolutionKind { get; set; }
        [DataMember] public int PrinterResolutionX { get; set; }
        [DataMember] public int PrinterResolutionY { get; set; }
        [DataMember] public bool PrinterPrintInColor { get; set; }
        [DataMember] public int PrinterCopies { get; set; }
        [DataMember] public int PrinterSettingsVersion { get; set; }
        [DataMember] public int PrintAmount { get; set; }
        [DataMember] public bool Borderless { get; set; }
        [DataMember] public bool ShowGrid { get; set; }
        [DataMember] public bool PrintCutGuides { get; set; }
        [DataMember] public bool PrintPictureBorder { get; set; }
        [DataMember] public bool AutoFlowNoGap { get; set; }
        [DataMember] public int AutomaticRushLayoutVersion { get; set; }
        [DataMember] public DateTime CreatedUtc { get; set; }
        [DataMember] public ObservableCollection<PhotoSizeItem> Items { get; set; }
        [DataMember] public ObservableCollection<PackageLayoutBox> LayoutBoxes { get; set; }

        public string CategoryDisplay
        {
            get
            {
                return Category == "Custom" && !string.IsNullOrWhiteSpace(CustomKind)
                    ? "Custom • " + CustomKind
                    : Category;
            }
        }

        public PhotoPackage CloneAsNew()
        {
            var copy = new PhotoPackage
            {
                Name = Name + " — Copy",
                Category = Category,
                CustomKind = CustomKind,
                Price = Price,
                IconPath = IconPath,
                PaperName = PaperName,
                Orientation = Orientation,
                PlacementMode = PlacementMode,
                RushIdPhotoArrangement = RushIdPhotoArrangement,
                MixOneByOneWithTwoByTwo = MixOneByOneWithTwoByTwo,
                MixOneByOneWithOnePointFive = MixOneByOneWithOnePointFive,
                MixOneByOneWithPassport = MixOneByOneWithPassport,
                PhotoFitMode = PhotoFitMode,
                MeasurementUnit = MeasurementUnit,
                DefaultPrinter = DefaultPrinter,
                PrinterDevModeBase64 = PrinterDevModeBase64,
                PrinterPaperSizeName = PrinterPaperSizeName,
                PrinterPaperSourceName = PrinterPaperSourceName,
                PrinterMediaTypeId = PrinterMediaTypeId,
                PrinterMediaTypeName = PrinterMediaTypeName,
                PrinterResolutionKind = PrinterResolutionKind,
                PrinterResolutionX = PrinterResolutionX,
                PrinterResolutionY = PrinterResolutionY,
                PrinterPrintInColor = PrinterPrintInColor,
                PrinterCopies = PrinterCopies,
                PrinterSettingsVersion = PrinterSettingsVersion,
                PrintAmount = PrintAmount,
                Borderless = Borderless,
                ShowGrid = ShowGrid,
                PrintCutGuides = PrintCutGuides,
                PrintPictureBorder = PrintPictureBorder,
                AutoFlowNoGap = AutoFlowNoGap,
                AutomaticRushLayoutVersion = AutomaticRushLayoutVersion
            };

            foreach (var item in Items)
                copy.Items.Add(item.Clone());
            if (LayoutBoxes != null)
                foreach (var box in LayoutBoxes)
                    copy.LayoutBoxes.Add(box.Clone());
            return copy;
        }

        public PhotoPackage EditableCopy()
        {
            var copy = CloneAsNew();
            copy.Id = Id;
            copy.Name = Name;
            copy.CreatedUtc = CreatedUtc;
            return copy;
        }
    }

    [DataContract]
    public class PackageLayoutBox
    {
        public PackageLayoutBox()
        {
            Id = Guid.NewGuid().ToString("N");
            Label = "Picture";
            Xmm = 5;
            Ymm = 5;
            WidthMm = 50.8;
            HeightMm = 50.8;
        }

        [DataMember] public string Id { get; set; }
        [DataMember] public string Label { get; set; }
        [DataMember] public double Xmm { get; set; }
        [DataMember] public double Ymm { get; set; }
        [DataMember] public double WidthMm { get; set; }
        [DataMember] public double HeightMm { get; set; }
        [DataMember] public double RotationDegrees { get; set; }

        public PackageLayoutBox Clone()
        {
            return new PackageLayoutBox
            {
                Label = Label,
                Xmm = Xmm,
                Ymm = Ymm,
                WidthMm = WidthMm,
                HeightMm = HeightMm,
                RotationDegrees = RotationDegrees
            };
        }
    }

    [DataContract]
    public class PhotoSizeItem
    {
        public PhotoSizeItem()
        {
            Label = "2 × 2 ID";
            WidthMm = 50.8;
            HeightMm = 50.8;
            Quantity = 4;
        }

        [DataMember] public string Label { get; set; }
        [DataMember] public double WidthMm { get; set; }
        [DataMember] public double HeightMm { get; set; }
        [DataMember] public int Quantity { get; set; }

        public PhotoSizeItem Clone()
        {
            return new PhotoSizeItem
            {
                Label = Label,
                WidthMm = WidthMm,
                HeightMm = HeightMm,
                Quantity = Quantity
            };
        }

        public override string ToString()
        {
            return string.Format("{0}: {1:0.#} × {2:0.#} mm × {3}", Label, WidthMm, HeightMm, Quantity);
        }
    }
}
