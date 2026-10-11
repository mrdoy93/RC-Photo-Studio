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
    private static async Task<int> Main()
    {
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

            for (var index = 0; index < pages.Count; index++)
            {
                pages[index].DisplayNumber = index + 1;
            }

            pages[1].Rotation = 90;
            var printImage = imageService.LoadForPrint(pages[0].FilePath, 90);
            Require(printImage.PixelWidth == 1100 && printImage.PixelHeight == 850,
                "Full-resolution print image rotation was incorrect.");
            var grayscalePrintImage = imageService.LoadForPrint(pages[0].FilePath, 0, PrintColorMode.Grayscale);
            Require(grayscalePrintImage.Format == WpfPixelFormats.Gray8,
                "Grayscale print setting was not applied to the rendered page.");
            var blackAndWhitePrintImage = imageService.LoadForPrint(pages[0].FilePath, 0, PrintColorMode.BlackAndWhite);
            Require(blackAndWhitePrintImage.Format == WpfPixelFormats.BlackWhite,
                "Black-and-white print setting was not applied to the rendered page.");
            var croppedPath = Path.Combine(testFolder, "cropped-rotated-page.png");
            imageService.CropPage(pages[1], croppedPath, 0.25, 0.25, 0.5, 0.5);
            using (var croppedImage = new Bitmap(croppedPath))
            {
                Require(croppedImage.Width == 550 && croppedImage.Height is >= 425 and <= 426,
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
                Require(combinedDocument.Pages[0].Height.Point > combinedDocument.Pages[0].Width.Point,
                    "Portrait scan did not use a portrait PDF page.");
                Require(combinedDocument.Pages[1].Width.Point > combinedDocument.Pages[1].Height.Point,
                    "Rotated scan did not maximize onto a landscape PDF page.");
            }

            var individualPdfPath = Path.Combine(testFolder, "individual-001.pdf");
            await pdfService.CreateAsync([pages[1]], individualPdfPath, PdfCompression.Smallest, "A4");
            Require(new FileInfo(individualPdfPath).Length > 1_000, "Individual compressed PDF was empty.");
            Require(File.ReadAllBytes(individualPdfPath).AsSpan(0, 4).SequenceEqual("%PDF"u8),
                "Individual output was not a PDF file.");
            using (var individualDocument = PdfReader.Open(individualPdfPath, PdfDocumentOpenMode.Import))
            {
                Require(individualDocument.Pages[0].Width.Point > individualDocument.Pages[0].Height.Point,
                    "Rotated individual PDF did not use a landscape page.");
            }

            var a3PdfPath = Path.Combine(testFolder, "a3.pdf");
            await pdfService.CreateAsync([pages[0]], a3PdfPath, PdfCompression.Balanced, "A3");
            using (var a3Document = PdfReader.Open(a3PdfPath, PdfDocumentOpenMode.Import))
            {
                Require(a3Document.Pages[0].Width.Point is > 840 and < 843 &&
                        a3Document.Pages[0].Height.Point is > 1189 and < 1192,
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
