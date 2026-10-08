using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.IO;
using System.IO.Compression;
using PrinterScanner.Models;

namespace PrinterScanner.Services;

public sealed class PdfService
{
    private readonly ImageService _imageService;
    private readonly TempSession _tempSession;

    public PdfService(ImageService imageService, TempSession tempSession)
    {
        _imageService = imageService;
        _tempSession = tempSession;
    }

    public Task CreateAsync(
        IReadOnlyList<ScanPage> pages,
        string outputPath,
        PdfCompression compression,
        string paperSize) => Task.Run(() =>
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("Scan at least one page before creating a PDF.");
        }

        using var document = new PdfDocument();
        document.Info.Title = "Scanned document";
        document.Info.Creator = "RC Printer & Scanner";

        foreach (var scanPage in pages)
        {
            var imagePath = PrepareImage(scanPage, compression);
            var page = document.AddPage();
            using var image = XImage.FromFile(imagePath);
            SetPageSize(page, paperSize, image.PixelWidth > image.PixelHeight);

            using var graphics = XGraphics.FromPdfPage(page);
            var availableWidth = page.Width.Point;
            var availableHeight = page.Height.Point;
            var scale = Math.Min(availableWidth / image.PixelWidth, availableHeight / image.PixelHeight);
            var width = image.PixelWidth * scale;
            var height = image.PixelHeight * scale;
            var x = (availableWidth - width) / 2;
            var y = (availableHeight - height) / 2;
            graphics.DrawImage(image, x, y, width, height);
        }

        document.Save(outputPath);
    });

    public async Task CreateIndividualArchiveAsync(
        IReadOnlyList<ScanPage> pages,
        string outputPath,
        PdfCompression compression,
        string paperSize)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("Scan at least one page before creating a PDF archive.");
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        for (var index = 0; index < pages.Count; index++)
        {
            var temporaryPdf = _tempSession.NewPath(".pdf");
            await CreateAsync([pages[index]], temporaryPdf, compression, paperSize);
            archive.CreateEntryFromFile(
                temporaryPdf,
                $"Scan-{index + 1:000}.pdf",
                CompressionLevel.Optimal);
        }
    }

    private string PrepareImage(ScanPage page, PdfCompression compression)
    {
        if (page.ColorMode is ScanColorMode.GrayErrorDiffusion or ScanColorMode.BlackAndWhite)
        {
            var pngPath = _tempSession.NewPath(".png");
            _imageService.SavePng(page, pngPath);
            return pngPath;
        }

        var quality = compression switch
        {
            PdfCompression.Smallest => 45L,
            PdfCompression.HighQuality => 88L,
            _ => 68L
        };
        var jpegPath = _tempSession.NewPath(".jpg");
        _imageService.SaveJpeg(page, jpegPath, quality);
        return jpegPath;
    }

    private static void SetPageSize(PdfPage page, string paperSize, bool landscape)
    {
        var (widthMm, heightMm) = paperSize switch
        {
            "A3" => (297.0, 420.0),
            "Letter" => (215.9, 279.4),
            "Legal" => (215.9, 355.6),
            _ => (210.0, 297.0)
        };

        if (landscape)
        {
            (widthMm, heightMm) = (heightMm, widthMm);
        }

        page.Width = XUnit.FromMillimeter(widthMm);
        page.Height = XUnit.FromMillimeter(heightMm);
    }
}
