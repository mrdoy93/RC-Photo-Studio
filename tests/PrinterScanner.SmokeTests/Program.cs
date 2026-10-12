using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using PdfSharp.Pdf.IO;
using PrinterScanner.Models;
using PrinterScanner.Services;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace PrinterScanner.SmokeTests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--process-image", var cliSourcePath, var cliOutputPath, var modeName])
        {
            var mode = Enum.Parse<ScanColorMode>(modeName, ignoreCase: true);
            new ImageService().ProcessScan(cliSourcePath, cliOutputPath, mode);
            Console.WriteLine($"Processed {cliSourcePath} as {mode} into {cliOutputPath}.");
            return 0;
        }

        var testFolder = Path.Combine(Path.GetTempPath(), "PrinterScanner-SmokeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testFolder);

        try
        {
            Require(new PrinterDevice("EPSON L110 Series").ToString() == "EPSON L110 Series",
                "Printer dropdown name was not concise.");
            Require(new ScannerDevice("scanner-id", "Brother Scanner", "Brother").ToString() == "Brother Scanner",
                "Scanner dropdown name was not concise.");
            var customPaper = PrinterPaperSize.FromHundredthsOfAnInch("Custom Photo", 394, 591, 256);
            Require(customPaper.DisplayName.StartsWith("Custom Photo (", StringComparison.Ordinal) &&
                    customPaper.DisplayName.EndsWith(" mm)", StringComparison.Ordinal),
                "Custom printer paper did not include its detected dimensions.");
            Require(customPaper.HasSameDimensions(customPaper.Height, customPaper.Width),
                "Printer paper dimensions did not match in landscape orientation.");
            var presetJson = JsonSerializer.Serialize(new AppPreferences
            {
                PrintPresets =
                [
                    new PrintPresetPreferences
                    {
                        PrinterName = "Test Printer",
                        PaperName = customPaper.Name,
                        PaperWidth = customPaper.Width,
                        PaperHeight = customPaper.Height,
                        ColorMode = PrintColorMode.Grayscale.ToString(),
                        Quality = PrintQualityMode.High.ToString(),
                        Copies = 3
                    }
                ]
            });
            var restoredPreferences = JsonSerializer.Deserialize<AppPreferences>(presetJson);
            Require(restoredPreferences?.PrintPresets.Single()?.Copies == 3,
                "Saved Copy preset settings did not survive serialization.");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var cancelled = false;
                try
                {
                    await new WiaScannerService().ScanAsync(
                        new ScannerDevice("cancel-test", "Test Scanner", "Test"),
                        new ScanOptions(ScanSource.Flatbed, ScanColorMode.Color, 300, "A4"),
                        Path.Combine(testFolder, "cancelled.png"),
                        cancellationToken: cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Require(cancelled, "A cancelled scan attempted to continue into WIA acquisition.");
            }

            var sourcePath = Path.Combine(testFolder, "source.png");
            CreateTestPage(sourcePath);
            var imageService = new ImageService();
            using var session = new TempSession();
            var pdfService = new PdfService(imageService, session);
            var pages = new List<ScanPage>();

            foreach (var mode in Enum.GetValues<ScanColorMode>())
            {
                var outputPath = Path.Combine(testFolder, $"{mode}.png");
                imageService.ProcessScan(sourcePath, outputPath, mode);
                Require(new FileInfo(outputPath).Length > 100, $"{mode} output was empty.");
                pages.Add(new ScanPage(outputPath, imageService.LoadThumbnail(outputPath), mode));
            }

            VerifyDocumentBackgroundEnhancement(testFolder, imageService);

            for (var index = 0; index < pages.Count; index++)
            {
                pages[index].DisplayNumber = index + 1;
            }

            pages[1].Rotation = 90;
            using var colorPageBitmap = new Bitmap(pages[0].FilePath);
            var printImage = imageService.LoadForPrint(pages[0].FilePath, 90);
            Require(printImage.PixelWidth == colorPageBitmap.Height &&
                    printImage.PixelHeight == colorPageBitmap.Width,
                "Full-resolution print image rotation was incorrect.");
            var grayscalePrintImage = imageService.LoadForPrint(pages[0].FilePath, 0, PrintColorMode.Grayscale);
            Require(grayscalePrintImage.Format == WpfPixelFormats.Gray8,
                "Grayscale print setting was not applied to the rendered page.");
            var blackAndWhitePrintImage = imageService.LoadForPrint(pages[0].FilePath, 0, PrintColorMode.BlackAndWhite);
            Require(blackAndWhitePrintImage.Format == WpfPixelFormats.BlackWhite,
                "Black-and-white print setting was not applied to the rendered page.");
            var croppedPath = Path.Combine(testFolder, "cropped-rotated-page.png");
            using var grayscalePageBitmap = new Bitmap(pages[1].FilePath);
            var rotatedWidth = grayscalePageBitmap.Height;
            var rotatedHeight = grayscalePageBitmap.Width;
            var expectedCropWidth = (int)Math.Ceiling(rotatedWidth * 0.75) -
                                    (int)Math.Floor(rotatedWidth * 0.25);
            var expectedCropHeight = (int)Math.Ceiling(rotatedHeight * 0.75) -
                                     (int)Math.Floor(rotatedHeight * 0.25);
            imageService.CropPage(pages[1], croppedPath, 0.25, 0.25, 0.5, 0.5);
            using (var croppedImage = new Bitmap(croppedPath))
            {
                Require(croppedImage.Width == expectedCropWidth &&
                        croppedImage.Height == expectedCropHeight,
                    "Full-resolution crop did not follow the rotated page coordinates.");
            }
            var printerService = new PrinterService(imageService);
            var installedPrinters = printerService.GetPrinters();
            foreach (var printer in installedPrinters)
            {
                var detectedPaperSizes = printerService.GetPaperSizes(printer);
                Require(detectedPaperSizes.Count > 0,
                    $"No paper sizes were available for {printer.Name}.");

                var driverSettings = new System.Drawing.Printing.PrinterSettings
                {
                    PrinterName = printer.Name
                };
                foreach (System.Drawing.Printing.PaperSize driverPaper in driverSettings.PaperSizes)
                {
                    if (driverPaper.Kind != System.Drawing.Printing.PaperKind.Custom ||
                        driverPaper.Width <= 0 || driverPaper.Height <= 0)
                    {
                        continue;
                    }

                    var expected = PrinterPaperSize.FromHundredthsOfAnInch(
                        driverPaper.PaperName,
                        driverPaper.Width,
                        driverPaper.Height,
                        driverPaper.RawKind);
                    Require(detectedPaperSizes.Any(size =>
                            string.Equals(size.Name, expected.Name, StringComparison.OrdinalIgnoreCase) &&
                            size.HasSameDimensions(expected.Width, expected.Height)),
                        $"Custom paper size {expected.DisplayName} was not detected for {printer.Name}.");
                }
            }

            var pdfPath = Path.Combine(testFolder, "combined.pdf");
            await pdfService.CreateAsync(pages, pdfPath, PdfCompression.Balanced, "A4");
            Require(new FileInfo(pdfPath).Length > 1_000, "Combined PDF was empty.");
            Require(File.ReadAllBytes(pdfPath).AsSpan(0, 4).SequenceEqual("%PDF"u8), "Output was not a PDF file.");
            using (var combinedDocument = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
            {
                var firstPageIsLandscape = colorPageBitmap.Width > colorPageBitmap.Height;
                var firstPdfIsLandscape =
                    combinedDocument.Pages[0].Width.Point > combinedDocument.Pages[0].Height.Point;
                Require(firstPdfIsLandscape == firstPageIsLandscape,
                    "Detected-subject scan did not use the matching PDF orientation.");
                var rotatedPageIsLandscape = grayscalePageBitmap.Height > grayscalePageBitmap.Width;
                var secondPdfIsLandscape =
                    combinedDocument.Pages[1].Width.Point > combinedDocument.Pages[1].Height.Point;
                Require(secondPdfIsLandscape == rotatedPageIsLandscape,
                    "Rotated scan did not use the matching PDF orientation.");
            }

            var individualPdfPath = Path.Combine(testFolder, "individual-001.pdf");
            await pdfService.CreateAsync([pages[1]], individualPdfPath, PdfCompression.Smallest, "A4");
            Require(new FileInfo(individualPdfPath).Length > 1_000, "Individual compressed PDF was empty.");
            Require(File.ReadAllBytes(individualPdfPath).AsSpan(0, 4).SequenceEqual("%PDF"u8),
                "Individual output was not a PDF file.");
            using (var individualDocument = PdfReader.Open(individualPdfPath, PdfDocumentOpenMode.Import))
            {
                var expectedLandscape = grayscalePageBitmap.Height > grayscalePageBitmap.Width;
                var actualLandscape =
                    individualDocument.Pages[0].Width.Point > individualDocument.Pages[0].Height.Point;
                Require(actualLandscape == expectedLandscape,
                    "Rotated individual PDF did not use the matching orientation.");
            }

            var a3PdfPath = Path.Combine(testFolder, "a3.pdf");
            await pdfService.CreateAsync([pages[0]], a3PdfPath, PdfCompression.Balanced, "A3");
            using (var a3Document = PdfReader.Open(a3PdfPath, PdfDocumentOpenMode.Import))
            {
                var shortSide = Math.Min(
                    a3Document.Pages[0].Width.Point,
                    a3Document.Pages[0].Height.Point);
                var longSide = Math.Max(
                    a3Document.Pages[0].Width.Point,
                    a3Document.Pages[0].Height.Point);
                Require(shortSide is > 840 and < 843 && longSide is > 1189 and < 1192,
                    "A3 PDF page dimensions were incorrect.");
            }

            var archivePath = Path.Combine(testFolder, "individual-pdfs.zip");
            await pdfService.CreateIndividualArchiveAsync(pages, archivePath, PdfCompression.Smallest, "A4");
            Require(new FileInfo(archivePath).Length > 1_000, "Individual PDF ZIP was empty.");
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                Require(archive.Entries.Count == pages.Count, "Individual PDF ZIP lost pages.");
                Require(archive.Entries.All(entry => entry.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)),
                    "Individual PDF ZIP contained a non-PDF entry.");
            }

            var printableFiles = new PrintableFileService(imageService, session);
            var renderedPdfPages = await printableFiles.PrepareAsync(pdfPath);
            Require(renderedPdfPages.Count == pages.Count, "Direct-print PDF rendering lost pages.");
            Require(renderedPdfPages.All(page => new FileInfo(page.FilePath).Length > 1_000),
                "A direct-print PDF page was empty.");
            var printableImage = await printableFiles.PrepareAsync(sourcePath);
            Require(printableImage.Count == 1, "Direct-print image preparation failed.");

            Console.WriteLine($"PASS: processed {pages.Count} scan modes, created a {new FileInfo(pdfPath).Length:N0}-byte PDF, and rendered it for direct printing.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(testFolder, recursive: true);
            }
            catch
            {
                // The operating system can finish removing codec handles after the test exits.
            }
        }
    }

    private static void CreateTestPage(string outputPath)
    {
        using var bitmap = new Bitmap(850, 1100, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(200, 200);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var titleFont = new Font("Segoe UI", 28, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 16);
        using var pen = new Pen(Color.SteelBlue, 4);
        graphics.DrawString("Printer & Scanner", titleFont, Brushes.Black, 55, 50);
        graphics.DrawRectangle(pen, 55, 130, 740, 820);
        graphics.DrawString("Grayscale, color, and PDF smoke test", bodyFont, Brushes.DimGray, 80, 175);
        graphics.FillRectangle(Brushes.OrangeRed, 80, 245, 210, 110);
        graphics.FillRectangle(Brushes.ForestGreen, 320, 245, 210, 110);
        graphics.FillRectangle(Brushes.RoyalBlue, 560, 245, 210, 110);
        for (var line = 0; line < 12; line++)
        {
            graphics.DrawLine(Pens.Gray, 80, 430 + (line * 35), 760, 430 + (line * 35));
        }

        bitmap.Save(outputPath, ImageFormat.Png);
    }

    private static void VerifyDocumentBackgroundEnhancement(string testFolder, ImageService imageService)
    {
        var sourcePath = Path.Combine(testFolder, "shaded-document.png");
        using (var source = new Bitmap(320, 180, PixelFormat.Format24bppRgb))
        {
            source.SetResolution(200, 200);
            using var textGraphics = Graphics.FromImage(source);
            for (var x = 0; x < source.Width; x++)
            {
                var shade = 190 + (x * 50 / (source.Width - 1));
                using var pen = new Pen(Color.FromArgb(shade, shade, shade));
                textGraphics.DrawLine(pen, x, 0, x, source.Height - 1);
            }

            using var darkBrush = new SolidBrush(Color.FromArgb(105, 105, 105));
            using var faintBrush = new SolidBrush(Color.FromArgb(155, 155, 155));
            textGraphics.FillRectangle(darkBrush, 45, 65, 230, 12);
            textGraphics.FillRectangle(faintBrush, 45, 100, 230, 10);
            source.Save(sourcePath, ImageFormat.Png);
        }

        foreach (var mode in new[]
                 {
                     ScanColorMode.TrueGray,
                     ScanColorMode.GrayErrorDiffusion,
                     ScanColorMode.BlackAndWhite
                 })
        {
            var outputPath = Path.Combine(testFolder, $"enhanced-{mode}.png");
            imageService.ProcessScan(sourcePath, outputPath, mode);
            using var output = new Bitmap(outputPath);

            Require(output.GetPixel(20, 25).R >= 240 && output.GetPixel(295, 25).R >= 240,
                $"{mode} did not clean the shaded page background.");
            Require(output.GetPixel(160, 70).R <= 80,
                $"{mode} did not preserve strong document text.");
            Require(output.GetPixel(160, 104).R <= 210,
                $"{mode} did not improve faint document text contrast.");
        }

        var handwritingPath = Path.Combine(testFolder, "scattered-handwriting.png");
        using (var source = new Bitmap(600, 800, PixelFormat.Format24bppRgb))
        {
            source.SetResolution(200, 200);
            using var graphics = Graphics.FromImage(source);
            graphics.Clear(Color.FromArgb(235, 235, 235));
            using var handwritingPen = new Pen(Color.FromArgb(35, 35, 35), 4);
            foreach (var top in new[] { 100, 350, 650 })
            {
                graphics.DrawLine(handwritingPen, 100, top, 470, top);
                graphics.DrawLine(handwritingPen, 120, top + 18, 440, top + 18);
                graphics.DrawLine(handwritingPen, 90, top + 36, 455, top + 36);
            }

            source.Save(handwritingPath, ImageFormat.Png);
        }

        var handwritingOutputPath = Path.Combine(testFolder, "scattered-handwriting-output.png");
        imageService.ProcessScan(
            handwritingPath,
            handwritingOutputPath,
            ScanColorMode.BlackAndWhite);
        using (var handwritingOutput = new Bitmap(handwritingOutputPath))
        {
            foreach (var y in new[] { 100, 350, 650 })
            {
                Require(handwritingOutput.GetPixel(250, y).R <= 30,
                    "A handwriting cluster was mistaken for the whole document object.");
            }
        }

        var darkRegionPath = Path.Combine(testFolder, "dark-card-document.png");
        using (var source = new Bitmap(360, 260, PixelFormat.Format24bppRgb))
        {
            source.SetResolution(200, 200);
            using var graphics = Graphics.FromImage(source);
            graphics.Clear(Color.FromArgb(235, 235, 235));
            using var cardBrush = new SolidBrush(Color.FromArgb(65, 65, 65));
            using var cardTextBrush = new SolidBrush(Color.FromArgb(25, 25, 25));
            using var faintCardTextBrush = new SolidBrush(Color.FromArgb(52, 52, 52));
            using var artifactBrush = new SolidBrush(Color.FromArgb(45, 45, 45));
            graphics.FillRectangle(cardBrush, 50, 55, 260, 160);
            graphics.FillRectangle(cardTextBrush, 90, 105, 180, 10);
            graphics.FillRectangle(cardTextBrush, 90, 135, 140, 8);
            graphics.FillRectangle(faintCardTextBrush, 90, 165, 140, 7);
            graphics.FillRectangle(artifactBrush, 325, 25, 8, 30);
            graphics.FillEllipse(artifactBrush, 15, 225, 12, 12);
            source.Save(darkRegionPath, ImageFormat.Png);
        }

        foreach (var mode in new[]
                 {
                     ScanColorMode.TrueGray,
                     ScanColorMode.GrayErrorDiffusion,
                     ScanColorMode.BlackAndWhite
                 })
        {
            var outputPath = Path.Combine(testFolder, $"dark-card-{mode}.png");
            imageService.ProcessScan(darkRegionPath, outputPath, mode);
            using var output = new Bitmap(outputPath);
            var cardBackground = AverageBrightness(output, new Rectangle(255, 165, 30, 25));
            var cardText = AverageBrightness(output, new Rectangle(110, 106, 100, 8));
            var faintCardText = AverageBrightness(output, new Rectangle(110, 165, 100, 7));
            var outsideArtifact = AverageBrightness(output, new Rectangle(325, 25, 8, 30));

            var minimumCardBrightness = mode == ScanColorMode.BlackAndWhite ? 225 : 185;
            var requiredTextContrast = mode == ScanColorMode.BlackAndWhite ? 180 : 50;
            Require(cardBackground >= minimumCardBrightness,
                $"{mode} left a dark document region too dark to read ({cardBackground:0.0}).");
            Require(cardBackground - cardText >= requiredTextContrast,
                $"{mode} lost text detail inside a dark document region " +
                $"(background {cardBackground:0.0}, text {cardText:0.0}).");
            Require(cardBackground - faintCardText >= 55,
                $"{mode} did not make low-contrast card text readable " +
                $"(background {cardBackground:0.0}, faint text {faintCardText:0.0}).");
            Require(outsideArtifact >= 250,
                $"{mode} did not whiten artifacts outside the detected card " +
                $"({outsideArtifact:0.0}).");
            Require(output.Width == 360 && output.Height == 260,
                $"{mode} changed the original paper dimensions.");
        }

        var tiltedCardPath = Path.Combine(testFolder, "tilted-card-document.png");
        using (var source = new Bitmap(380, 280, PixelFormat.Format24bppRgb))
        {
            source.SetResolution(200, 200);
            using var graphics = Graphics.FromImage(source);
            graphics.Clear(Color.FromArgb(230, 230, 230));
            using var cardBrush = new SolidBrush(Color.FromArgb(125, 125, 125));
            using var textBrush = new SolidBrush(Color.FromArgb(25, 25, 25));
            var cardOutline = new[]
            {
                new Point(110, 60),
                new Point(300, 95),
                new Point(265, 220),
                new Point(65, 185)
            };
            graphics.FillPolygon(cardBrush, cardOutline);
            graphics.FillRectangle(textBrush, 125, 130, 130, 12);

            // This dark spot is inside the card's axis-aligned bounds, but outside
            // its actual tilted outline. A rectangular crop would leave it behind.
            graphics.FillRectangle(textBrush, 68, 62, 12, 12);
            source.Save(tiltedCardPath, ImageFormat.Png);
        }

        foreach (var mode in new[]
                 {
                     ScanColorMode.Color,
                     ScanColorMode.TrueGray,
                     ScanColorMode.GrayErrorDiffusion,
                     ScanColorMode.BlackAndWhite
                 })
        {
            var outputPath = Path.Combine(testFolder, $"tilted-card-{mode}.png");
            imageService.ProcessScan(tiltedCardPath, outputPath, mode);
            using var output = new Bitmap(outputPath);
            var outsideCorner = AverageBrightness(output, new Rectangle(68, 62, 12, 12));
            var retainedText = AverageBrightness(output, new Rectangle(145, 132, 80, 8));

            Require(outsideCorner >= 250,
                $"{mode} did not keep the cropped corner outside a tilted object white " +
                $"({outsideCorner:0.0}).");
            var maximumRetainedDetail = mode == ScanColorMode.Color ? 40 : 90;
            Require(retainedText <= maximumRetainedDetail,
                $"{mode} removed detail inside a tilted detected object " +
                $"({retainedText:0.0}).");
            Require(output.Width == 380 && output.Height == 280,
                $"{mode} changed the tilted scan's paper dimensions.");
        }
    }

    private static double AverageBrightness(Bitmap bitmap, Rectangle area)
    {
        long total = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                total += bitmap.GetPixel(x, y).R;
            }
        }

        return total / (double)(area.Width * area.Height);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
