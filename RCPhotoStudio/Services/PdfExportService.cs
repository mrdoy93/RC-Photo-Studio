using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RCPhotoStudio.Models;
using RCPhotoStudio.Views;

namespace RCPhotoStudio.Services
{
    public static class PdfExportService
    {
        private const int ExportDpi = 300;
        private const int MaximumImageCopies = 9999;
        private const float TicketBorderWidthPixels = 0.10f;

        public static void Save(string path, PrintLayout layout, bool showCutGuides,
            bool showPictureBorders)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A PDF path is required.", "path");
            if (layout == null || layout.PageCount < 1) throw new ArgumentException("The preview has no pages.", "layout");

            var pages = new List<PdfPageImage>();
            for (var pageIndex = 0; pageIndex < layout.PageCount; pageIndex++)
                pages.Add(RenderPage(layout, pageIndex, showCutGuides, showPictureBorders));
            WritePdf(path, pages);
        }

        public static void SaveImagePreview(string path, string imagePath,
            double paperWidthMm, double paperHeightMm,
            double ticketWidthMm, double ticketHeightMm,
            IList<TicketNumberBoxSettings> numberingBoxes, int copyCount = 1,
            double sideMarginMm = 0, double imageGapMm = 0, double topMarginMm = 0,
            bool autoRotateToFill = false, bool printTicketBorders = false)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A PDF path is required.", "path");
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new ArgumentException("A preview image is required.", "imagePath");
            if (paperWidthMm <= 0 || paperHeightMm <= 0)
                throw new ArgumentException("The paper size is invalid.");
            if (ticketWidthMm <= 0 || ticketHeightMm <= 0)
                throw new ArgumentException("The ticket size is invalid.");
            if (sideMarginMm < 0 || imageGapMm < 0 || topMarginMm < 0)
                throw new ArgumentException("The image spacing is invalid.");

            copyCount = Math.Max(1, Math.Min(copyCount, MaximumImageCopies));
            var slots = CreateImagePreviewSlots(paperWidthMm, paperHeightMm,
                ticketWidthMm, ticketHeightMm, sideMarginMm, topMarginMm,
                imageGapMm, autoRotateToFill);
            var copiesPerPage = Math.Max(1, slots.Count);
            var pageCount = Math.Max(1,
                (int)Math.Ceiling(copyCount / (double)copiesPerPage));
            var pages = new List<PdfPageImage>();
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
                pages.Add(RenderImagePreviewPage(imagePath, paperWidthMm, paperHeightMm,
                    ticketWidthMm, ticketHeightMm, numberingBoxes, copyCount, pageIndex,
                    sideMarginMm, imageGapMm, topMarginMm, autoRotateToFill,
                    printTicketBorders));
            WritePdf(path, pages);
        }

        private static PdfPageImage RenderImagePreviewPage(string imagePath,
            double paperWidthMm, double paperHeightMm,
            double ticketWidthMm, double ticketHeightMm,
            IList<TicketNumberBoxSettings> numberingBoxes, int copyCount, int pageIndex,
            double sideMarginMm, double imageGapMm, double topMarginMm,
            bool autoRotateToFill, bool printTicketBorders)
        {
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(paperWidthMm / 25.4 * ExportDpi));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(paperHeightMm / 25.4 * ExportDpi));
            using (var bitmap = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(ExportDpi, ExportDpi);
                using (var graphics = Graphics.FromImage(bitmap))
                using (var image = Image.FromFile(imagePath))
                {
                    ApplyExifOrientation(image);
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    var drawWidth = (float)(ticketWidthMm / 25.4 * ExportDpi);
                    var drawHeight = (float)(ticketHeightMm / 25.4 * ExportDpi);
                    var slots = CreateImagePreviewSlots(paperWidthMm, paperHeightMm,
                        ticketWidthMm, ticketHeightMm, sideMarginMm, topMarginMm,
                        imageGapMm, autoRotateToFill);
                    var copiesPerPage = Math.Max(1, slots.Count);
                    var firstCopyIndex = pageIndex * copiesPerPage;
                    var copiesOnPage = Math.Max(0,
                        Math.Min(copiesPerPage, copyCount - firstCopyIndex));
                    var baseNumber = numberingBoxes == null
                        ? ""
                        : numberingBoxes.Select(settings => settings.TextValue)
                            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";

                    for (var localIndex = 0; localIndex < copiesOnPage; localIndex++)
                    {
                        var copyIndex = firstCopyIndex + localIndex;
                        var slot = slots[localIndex];
                        var state = graphics.Save();
                        try
                        {
                            var left = (float)(slot.Xmm / 25.4 * ExportDpi);
                            var top = (float)(slot.Ymm / 25.4 * ExportDpi);
                            graphics.TranslateTransform(
                                left + (slot.IsRotated ? drawHeight : 0), top);
                            if (slot.IsRotated) graphics.RotateTransform(90);
                            var destination = new RectangleF(0, 0, drawWidth, drawHeight);
                            graphics.DrawImage(image, destination);
                            DrawNumberingBoxes(graphics, destination, numberingBoxes,
                                IncrementNumericText(baseNumber, copyIndex));
                            if (printTicketBorders)
                            {
                                using (var pen = new Pen(Color.Black, TicketBorderWidthPixels))
                                {
                                    pen.Alignment = PenAlignment.Inset;
                                    graphics.DrawRectangle(pen, destination.X, destination.Y,
                                        destination.Width, destination.Height);
                                }
                            }
                        }
                        finally
                        {
                            graphics.Restore(state);
                        }
                    }
                }

                return CreatePdfPageImage(bitmap, paperWidthMm, paperHeightMm);
            }
        }

        private sealed class ImagePreviewSlot
        {
            public double Xmm { get; set; }
            public double Ymm { get; set; }
            public bool IsRotated { get; set; }
        }

        private static IList<ImagePreviewSlot> CreateImagePreviewSlots(
            double paperWidthMm, double paperHeightMm,
            double ticketWidthMm, double ticketHeightMm,
            double sideMarginMm, double topMarginMm, double imageGapMm,
            bool autoRotateToFill)
        {
            var slots = new List<ImagePreviewSlot>();
            var availableWidth = Math.Max(0, paperWidthMm - sideMarginMm * 2);
            var availableHeight = Math.Max(0, paperHeightMm - topMarginMm);
            var columns = GetFitCount(availableWidth, ticketWidthMm, imageGapMm);
            var rows = GetFitCount(availableHeight, ticketHeightMm, imageGapMm);
            if (columns == 0 || rows == 0)
            {
                slots.Add(new ImagePreviewSlot { Xmm = sideMarginMm, Ymm = topMarginMm });
                return slots;
            }

            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                    slots.Add(new ImagePreviewSlot
                    {
                        Xmm = sideMarginMm + column * (ticketWidthMm + imageGapMm),
                        Ymm = topMarginMm + row * (ticketHeightMm + imageGapMm)
                    });

            if (!autoRotateToFill || Math.Abs(ticketWidthMm - ticketHeightMm) < 0.000001)
                return slots;

            var usedWidth = columns * ticketWidthMm + Math.Max(0, columns - 1) * imageGapMm;
            var usedHeight = rows * ticketHeightMm + Math.Max(0, rows - 1) * imageGapMm;
            var rotatedWidth = ticketHeightMm;
            var rotatedHeight = ticketWidthMm;

            var bottomY = topMarginMm + usedHeight + imageGapMm;
            var bottomHeight = availableHeight - usedHeight - imageGapMm;
            var bottomColumns = GetFitCount(availableWidth, rotatedWidth, imageGapMm);
            var bottomRows = GetFitCount(bottomHeight, rotatedHeight, imageGapMm);
            for (var row = 0; row < bottomRows; row++)
                for (var column = 0; column < bottomColumns; column++)
                    slots.Add(new ImagePreviewSlot
                    {
                        Xmm = sideMarginMm + column * (rotatedWidth + imageGapMm),
                        Ymm = bottomY + row * (rotatedHeight + imageGapMm),
                        IsRotated = true
                    });

            var rightX = sideMarginMm + usedWidth + imageGapMm;
            var rightWidth = availableWidth - usedWidth - imageGapMm;
            var rightColumns = GetFitCount(rightWidth, rotatedWidth, imageGapMm);
            var rightRows = GetFitCount(usedHeight, rotatedHeight, imageGapMm);
            for (var row = 0; row < rightRows; row++)
                for (var column = 0; column < rightColumns; column++)
                    slots.Add(new ImagePreviewSlot
                    {
                        Xmm = rightX + column * (rotatedWidth + imageGapMm),
                        Ymm = topMarginMm + row * (rotatedHeight + imageGapMm),
                        IsRotated = true
                    });

            return slots;
        }

        private static int GetFitCount(double availableSize, double itemSize, double gap)
        {
            if (availableSize + 0.000001 < itemSize || itemSize <= 0) return 0;
            return Math.Max(0, (int)Math.Floor(
                (availableSize + gap) / (itemSize + gap) + 0.000001));
        }

        private static void DrawNumberingBoxes(Graphics graphics, RectangleF ticketBounds,
            IList<TicketNumberBoxSettings> boxes, string numberText)
        {
            if (boxes == null || boxes.Count == 0) return;

            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            })
            {
                foreach (var settings in boxes)
                {
                    var width = (float)(settings.WidthInches * ExportDpi);
                    var height = (float)(settings.HeightInches * ExportDpi);
                    var centerX = ticketBounds.Left + (float)(settings.XInches * ExportDpi) + width / 2;
                    var centerY = ticketBounds.Top + (float)(settings.YInches * ExportDpi) + height / 2;
                    var state = graphics.Save();
                    try
                    {
                        graphics.TranslateTransform(centerX, centerY);
                        graphics.RotateTransform((float)settings.RotationDegrees);
                        var box = new RectangleF(-width / 2, -height / 2, width, height);
                        if (!settings.IsBackgroundTransparent)
                        {
                            using (var fill = new SolidBrush(ParseColor(settings.BackgroundColor, Color.White)))
                                graphics.FillRectangle(fill, box);
                        }
                        if (!settings.IsBorderTransparent)
                        {
                            var thicknessPixels = settings.BorderThicknessPixels > 0
                                ? settings.BorderThicknessPixels
                                : 1;
                            var exportThickness = Math.Max(0.1f,
                                (float)(thicknessPixels * ExportDpi / 96.0));
                            using (var pen = new Pen(ParseColor(settings.BorderColor, Color.Black), exportThickness))
                                graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                        }
                        if (!string.IsNullOrEmpty(numberText))
                            using (var font = CreateFont(settings.FontFamilyName, settings.FontSizePoints))
                            using (var textBrush = new SolidBrush(ParseColor(settings.TextColor, Color.Black)))
                                graphics.DrawString(numberText, font, textBrush, box, format);
                    }
                    finally
                    {
                        graphics.Restore(state);
                    }
                }
            }
        }

        private static string IncrementNumericText(string value, int increment)
        {
            if (string.IsNullOrEmpty(value) || increment <= 0) return value ?? "";
            var digits = value.ToCharArray();
            long carry = increment;
            for (var index = digits.Length - 1; index >= 0 && carry > 0; index--)
            {
                var sum = digits[index] - '0' + carry;
                digits[index] = (char)('0' + (int)(sum % 10));
                carry = sum / 10;
            }
            return carry > 0
                ? carry.ToString(CultureInfo.InvariantCulture) + new string(digits)
                : new string(digits);
        }

        private static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try
            {
                var text = value.TrimStart('#');
                if (text.Length == 8)
                {
                    return Color.FromArgb(
                        Convert.ToByte(text.Substring(0, 2), 16),
                        Convert.ToByte(text.Substring(2, 2), 16),
                        Convert.ToByte(text.Substring(4, 2), 16),
                        Convert.ToByte(text.Substring(6, 2), 16));
                }
                if (text.Length == 6)
                {
                    return Color.FromArgb(
                        Convert.ToByte(text.Substring(0, 2), 16),
                        Convert.ToByte(text.Substring(2, 2), 16),
                        Convert.ToByte(text.Substring(4, 2), 16));
                }
            }
            catch { }
            return fallback;
        }

        private static Font CreateFont(string familyName, double sizePoints)
        {
            try
            {
                return new Font(string.IsNullOrWhiteSpace(familyName) ? "Segoe UI" : familyName,
                    (float)Math.Max(4, Math.Min(200, sizePoints)), FontStyle.Bold, GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Arial", (float)Math.Max(4, Math.Min(200, sizePoints)),
                    FontStyle.Bold, GraphicsUnit.Point);
            }
        }

        private static PdfPageImage RenderPage(PrintLayout layout, int pageIndex,
            bool showCutGuides, bool showPictureBorders)
        {
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(layout.PaperWidthMm / 25.4 * ExportDpi));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(layout.PaperHeightMm / 25.4 * ExportDpi));
            using (var bitmap = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(ExportDpi, ExportDpi);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    PrintWorkspaceWindow.DrawPrintPage(graphics, pageIndex, layout,
                        showCutGuides, showPictureBorders);
                }

                return CreatePdfPageImage(bitmap, layout.PaperWidthMm, layout.PaperHeightMm);
            }
        }

        private static PdfPageImage CreatePdfPageImage(Bitmap bitmap,
            double paperWidthMm, double paperHeightMm)
        {
            using (var stream = new MemoryStream())
            {
                var codec = ImageCodecInfo.GetImageEncoders()
                    .First(item => item.FormatID == ImageFormat.Jpeg.Guid);
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                    bitmap.Save(stream, codec, parameters);
                }
                return new PdfPageImage
                {
                    Jpeg = stream.ToArray(),
                    PixelWidth = bitmap.Width,
                    PixelHeight = bitmap.Height,
                    WidthPoints = paperWidthMm / 25.4 * 72.0,
                    HeightPoints = paperHeightMm / 25.4 * 72.0
                };
            }
        }

        private static void ApplyExifOrientation(Image image)
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
                try { image.RemovePropertyItem(orientationId); }
                catch { }
            }
            catch
            {
                // Some image codecs expose incomplete EXIF data; keep the decoded image as-is.
            }
        }

        private static void WritePdf(string path, IList<PdfPageImage> pages)
        {
            var objectCount = 2 + pages.Count * 3;
            var offsets = new long[objectCount + 1];
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteAscii(stream, "%PDF-1.4\n%RCPS\n");

                WriteObjectStart(stream, offsets, 1);
                WriteAscii(stream, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                WriteObjectStart(stream, offsets, 2);
                var kids = string.Join(" ", Enumerable.Range(0, pages.Count)
                    .Select(index => (5 + index * 3).ToString(CultureInfo.InvariantCulture) + " 0 R"));
                WriteAscii(stream, string.Format(CultureInfo.InvariantCulture,
                    "<< /Type /Pages /Count {0} /Kids [ {1} ] >>\nendobj\n", pages.Count, kids));

                for (var index = 0; index < pages.Count; index++)
                {
                    var page = pages[index];
                    var imageId = 3 + index * 3;
                    var contentId = imageId + 1;
                    var pageId = imageId + 2;

                    WriteObjectStart(stream, offsets, imageId);
                    WriteAscii(stream, string.Format(CultureInfo.InvariantCulture,
                        "<< /Type /XObject /Subtype /Image /Width {0} /Height {1} " +
                        "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {2} >>\nstream\n",
                        page.PixelWidth, page.PixelHeight, page.Jpeg.Length));
                    stream.Write(page.Jpeg, 0, page.Jpeg.Length);
                    WriteAscii(stream, "\nendstream\nendobj\n");

                    var content = string.Format(CultureInfo.InvariantCulture,
                        "q\n{0:0.###} 0 0 {1:0.###} 0 0 cm\n/Im0 Do\nQ\n",
                        page.WidthPoints, page.HeightPoints);
                    var contentBytes = Encoding.ASCII.GetBytes(content);
                    WriteObjectStart(stream, offsets, contentId);
                    WriteAscii(stream, string.Format(CultureInfo.InvariantCulture,
                        "<< /Length {0} >>\nstream\n", contentBytes.Length));
                    stream.Write(contentBytes, 0, contentBytes.Length);
                    WriteAscii(stream, "endstream\nendobj\n");

                    WriteObjectStart(stream, offsets, pageId);
                    WriteAscii(stream, string.Format(CultureInfo.InvariantCulture,
                        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0:0.###} {1:0.###}] " +
                        "/Resources << /XObject << /Im0 {2} 0 R >> >> /Contents {3} 0 R >>\nendobj\n",
                        page.WidthPoints, page.HeightPoints, imageId, contentId));
                }

                var xrefOffset = stream.Position;
                WriteAscii(stream, string.Format(CultureInfo.InvariantCulture,
                    "xref\n0 {0}\n0000000000 65535 f \n", objectCount + 1));
                for (var id = 1; id <= objectCount; id++)
                    WriteAscii(stream, offsets[id].ToString("0000000000", CultureInfo.InvariantCulture) +
                        " 00000 n \n");
                WriteAscii(stream, string.Format(CultureInfo.InvariantCulture,
                    "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
                    objectCount + 1, xrefOffset));
            }
        }

        private static void WriteObjectStart(Stream stream, long[] offsets, int id)
        {
            offsets[id] = stream.Position;
            WriteAscii(stream, id.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
        }

        private static void WriteAscii(Stream stream, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private sealed class PdfPageImage
        {
            public byte[] Jpeg { get; set; }
            public int PixelWidth { get; set; }
            public int PixelHeight { get; set; }
            public double WidthPoints { get; set; }
            public double HeightPoints { get; set; }
        }
    }
}
