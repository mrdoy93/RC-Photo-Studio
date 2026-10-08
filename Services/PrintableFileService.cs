using System.IO;
using PrinterScanner.Models;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PrinterScanner.Services;

public sealed class PrintableFileService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"
    };

    private readonly ImageService _imageService;
    private readonly TempSession _tempSession;

    public PrintableFileService(ImageService imageService, TempSession tempSession)
    {
        _imageService = imageService;
        _tempSession = tempSession;
    }

    public async Task<IReadOnlyList<ScanPage>> PrepareAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The selected print file no longer exists.", filePath);
        }

        var extension = Path.GetExtension(filePath);
        if (ImageExtensions.Contains(extension))
        {
            return new[]
            {
                new ScanPage(filePath, _imageService.LoadThumbnail(filePath), ScanColorMode.Color)
                {
                    DisplayNumber = 1
                }
            };
        }

        if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a PDF, JPG, PNG, BMP, or TIFF file to print.");
        }

        return await RenderPdfAsync(filePath);
    }

    private async Task<IReadOnlyList<ScanPage>> RenderPdfAsync(string filePath)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));
        var document = await PdfDocument.LoadFromFileAsync(storageFile);
        if (document.PageCount == 0)
        {
            throw new InvalidOperationException("The selected PDF has no printable pages.");
        }

        var pages = new List<ScanPage>((int)document.PageCount);
        for (uint index = 0; index < document.PageCount; index++)
        {
            using var pdfPage = document.GetPage(index);
            using var renderedStream = new InMemoryRandomAccessStream();
            var scale = 300d / 96d;
            var width = pdfPage.Size.Width * scale;
            var height = pdfPage.Size.Height * scale;
            var longestEdge = Math.Max(width, height);
            if (longestEdge > 5000d)
            {
                scale *= 5000d / longestEdge;
                width = pdfPage.Size.Width * scale;
                height = pdfPage.Size.Height * scale;
            }

            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(height))
            };
            await pdfPage.RenderToStreamAsync(renderedStream, options);
            renderedStream.Seek(0);

            var outputPath = _tempSession.NewPath(".png");
            await using (var input = renderedStream.AsStreamForRead())
            await using (var output = File.Create(outputPath))
            {
                await input.CopyToAsync(output);
            }

            pages.Add(new ScanPage(outputPath, _imageService.LoadThumbnail(outputPath), ScanColorMode.Color)
            {
                DisplayNumber = (int)index + 1
            });
        }

        return pages;
    }
}
