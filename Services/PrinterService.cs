using System.Drawing.Printing;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using PrinterScanner.Models;

namespace PrinterScanner.Services;

public sealed class PrinterService
{
    private const string UnsupportedLargePaperMessage =
        "A3 or Legal is not available on this Printer Model";
    private const int DmOutBuffer = 0x00000002;
    private const int DmInBuffer = 0x00000008;
    private const int DmInPrompt = 0x00000004;
    private const int IdOk = 1;
    private const uint GmemMoveableZeroInit = 0x0042;
    private readonly Dictionary<string, byte[]> _printerModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageService _imageService;

    public PrinterService(ImageService imageService)
    {
        _imageService = imageService;
    }

    public IReadOnlyList<PrinterDevice> GetPrinters()
    {
        var printers = new List<PrinterDevice>();
        foreach (string printerName in PrinterSettings.InstalledPrinters)
        {
            printers.Add(new PrinterDevice(printerName));
        }

        return printers
            .OrderByDescending(printer => printer.IsPreferredBrand)
            .ThenBy(printer => printer.Name)
            .ToList();
    }

    public bool SupportsPaperSize(PrinterDevice printer, string paperSize)
    {
        if (paperSize is not ("A3" or "Legal"))
        {
            return true;
        }

        try
        {
            using var printServer = new LocalPrintServer();
            using var printQueue = printServer.GetPrintQueue(printer.Name);
            printQueue.Refresh();
            var capabilities = printQueue.GetPrintCapabilities();
            var expectedName = paperSize == "A3"
                ? PageMediaSizeName.ISOA3
                : PageMediaSizeName.NorthAmericaLegal;
            var expectedWidth = paperSize == "A3" ? 1122.52 : 816d;
            var expectedHeight = paperSize == "A3" ? 1587.4 : 1344d;

            return capabilities.PageMediaSizeCapability?.Any(size =>
                size.PageMediaSizeName == expectedName ||
                DimensionsMatch(size.Width, size.Height, expectedWidth, expectedHeight)) == true;
        }
        catch (PrintSystemException)
        {
            // If a legacy driver cannot report capabilities, allow it to validate the job itself.
            return true;
        }
    }

    private static bool DimensionsMatch(
        double? width,
        double? height,
        double expectedWidth,
        double expectedHeight)
    {
        if (width is null || height is null)
        {
            return false;
        }

        const double tolerance = 8d;
        return Math.Abs(width.Value - expectedWidth) <= tolerance &&
               Math.Abs(height.Value - expectedHeight) <= tolerance ||
               Math.Abs(width.Value - expectedHeight) <= tolerance &&
               Math.Abs(height.Value - expectedWidth) <= tolerance;
    }

    private static bool TicketUsesRequestedLargePaper(PrintTicket ticket, string paperSize)
    {
        var expectedName = paperSize == "A3"
            ? PageMediaSizeName.ISOA3
            : PageMediaSizeName.NorthAmericaLegal;
        var expectedWidth = paperSize == "A3" ? 1122.52 : 816d;
        var expectedHeight = paperSize == "A3" ? 1587.4 : 1344d;
        var mediaSize = ticket.PageMediaSize;

        return mediaSize?.PageMediaSizeName == expectedName ||
               DimensionsMatch(mediaSize?.Width, mediaSize?.Height, expectedWidth, expectedHeight);
    }

    public bool ShowProperties(IntPtr ownerHandle, PrinterDevice printer)
    {
        if (!OpenPrinter(printer.Name, out var printerHandle, IntPtr.Zero))
        {
            throw new InvalidOperationException("Windows could not open the selected printer.");
        }

        IntPtr devModeHandle = IntPtr.Zero;
        IntPtr devModePointer = IntPtr.Zero;

        try
        {
            var size = DocumentProperties(ownerHandle, printerHandle, printer.Name, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0)
            {
                throw new InvalidOperationException("The printer driver did not provide a properties page.");
            }

            devModeHandle = GlobalAlloc(GmemMoveableZeroInit, (UIntPtr)(uint)size);
            if (devModeHandle == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Windows could not allocate printer settings memory.");
            }

            devModePointer = GlobalLock(devModeHandle);
            if (devModePointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows could not access printer settings.");
            }

            var initialized = DocumentProperties(
                ownerHandle, printerHandle, printer.Name, devModePointer, IntPtr.Zero, DmOutBuffer);
            if (initialized < 0)
            {
                throw new InvalidOperationException("The printer driver could not load its default settings.");
            }

            var result = DocumentProperties(
                ownerHandle, printerHandle, printer.Name, devModePointer, devModePointer,
                DmInBuffer | DmOutBuffer | DmInPrompt);

            if (result != IdOk)
            {
                return false;
            }

            var bytes = new byte[size];
            Marshal.Copy(devModePointer, bytes, 0, size);
            _printerModes[printer.Name] = bytes;
            return true;
        }
        finally
        {
            if (devModePointer != IntPtr.Zero)
            {
                GlobalUnlock(devModeHandle);
            }

            if (devModeHandle != IntPtr.Zero)
            {
                GlobalFree(devModeHandle);
            }

            ClosePrinter(printerHandle);
        }
    }

    public Task PrintAsync(PrinterDevice printer, IReadOnlyList<ScanPage> pages, PrintOptions options)
    {
        if (pages.Count == 0)
        {
            return Task.FromException(new InvalidOperationException("Scan at least one page before printing."));
        }

        return RunSta(() => PrintWithWindowsQueue(printer, pages, options));
    }

    private void PrintWithWindowsQueue(PrinterDevice printer, IReadOnlyList<ScanPage> pages, PrintOptions options)
    {
        using var printServer = new LocalPrintServer();
        using var printQueue = printServer.GetPrintQueue(printer.Name);
        printQueue.Refresh();

        var printTicket = CreatePrintTicket(printQueue, printer.Name, options);
        var validation = printQueue.MergeAndValidatePrintTicket(printQueue.DefaultPrintTicket, printTicket);
        printTicket = validation.ValidatedPrintTicket;
        if (options.PaperSize is "A3" or "Legal" &&
            !TicketUsesRequestedLargePaper(printTicket, options.PaperSize))
        {
            throw new InvalidOperationException(UnsupportedLargePaperMessage);
        }

        var capabilities = printQueue.GetPrintCapabilities(printTicket);
        var (pageWidth, pageHeight) = GetPageSize(printTicket, capabilities);
        var imageable = capabilities.PageImageableArea;
        var originX = imageable?.OriginWidth ?? 24d;
        var originY = imageable?.OriginHeight ?? 24d;
        var printableWidth = imageable?.ExtentWidth ?? Math.Max(1, pageWidth - 48d);
        var printableHeight = imageable?.ExtentHeight ?? Math.Max(1, pageHeight - 48d);

        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new System.Windows.Size(pageWidth, pageHeight);

        foreach (var page in pages)
        {
            var fixedPage = new FixedPage
            {
                Width = pageWidth,
                Height = pageHeight,
                Background = System.Windows.Media.Brushes.White
            };
            var image = new System.Windows.Controls.Image
            {
                Source = _imageService.LoadForPrint(page.FilePath, page.Rotation, options.ColorMode),
                Width = printableWidth,
                Height = printableHeight,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, options.Quality switch
            {
                PrintQualityMode.Draft => BitmapScalingMode.LowQuality,
                PrintQualityMode.High => BitmapScalingMode.HighQuality,
                _ => BitmapScalingMode.Fant
            });
            FixedPage.SetLeft(image, originX);
            FixedPage.SetTop(image, originY);
            fixedPage.Children.Add(image);
            fixedPage.Measure(new System.Windows.Size(pageWidth, pageHeight));
            fixedPage.Arrange(new Rect(0, 0, pageWidth, pageHeight));
            fixedPage.UpdateLayout();

            var pageContent = new PageContent();
            ((IAddChild)pageContent).AddChild(fixedPage);
            document.Pages.Add(pageContent);
        }

        var writer = PrintQueue.CreateXpsDocumentWriter(printQueue)
            ?? throw new InvalidOperationException("Windows could not create a print job for the selected printer.");
        writer.Write(document.DocumentPaginator, printTicket);
    }

    private PrintTicket CreatePrintTicket(PrintQueue queue, string printerName, PrintOptions options)
    {
        PrintTicket ticket;
        if (_printerModes.TryGetValue(printerName, out var devMode))
        {
            try
            {
                using var converter = new PrintTicketConverter(printerName, 1);
                ticket = converter.ConvertDevModeToPrintTicket(devMode, PrintTicketScope.JobScope);
                ApplyVisiblePrintOptions(ticket, options);
                return ticket;
            }
            catch (PrintSystemException)
            {
                // Some legacy vendor drivers cannot translate private DEVMODE data.
                // The queue's current user ticket remains a safe direct-print fallback.
            }
        }

        ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket ?? new PrintTicket();
        ApplyVisiblePrintOptions(ticket, options);
        return ticket;
    }

    private static void ApplyVisiblePrintOptions(PrintTicket ticket, PrintOptions options)
    {
        ticket.CopyCount = Math.Clamp(options.Copies, 1, 999);
        ticket.PageMediaSize = new PageMediaSize(options.PaperSize switch
        {
            "A3" => PageMediaSizeName.ISOA3,
            "Legal" => PageMediaSizeName.NorthAmericaLegal,
            "Letter" => PageMediaSizeName.NorthAmericaLetter,
            _ => PageMediaSizeName.ISOA4
        });
        ticket.PageOrientation = PageOrientation.Portrait;
        ticket.OutputColor = options.ColorMode switch
        {
            PrintColorMode.Grayscale => OutputColor.Grayscale,
            PrintColorMode.BlackAndWhite => OutputColor.Monochrome,
            _ => OutputColor.Color
        };
        ticket.OutputQuality = options.Quality switch
        {
            PrintQualityMode.Draft => OutputQuality.Draft,
            PrintQualityMode.High => OutputQuality.High,
            _ => OutputQuality.Normal
        };
    }

    private static (double Width, double Height) GetPageSize(
        PrintTicket ticket,
        PrintCapabilities capabilities)
    {
        var width = ticket.PageMediaSize?.Width ?? capabilities.OrientedPageMediaWidth ?? 793.7;
        var height = ticket.PageMediaSize?.Height ?? capabilities.OrientedPageMediaHeight ?? 1122.5;
        if (ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape && width < height)
        {
            (width, height) = (height, width);
        }

        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Direct Print Thread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printerHandle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode)]
    private static extern int DocumentProperties(
        IntPtr windowHandle,
        IntPtr printerHandle,
        string deviceName,
        IntPtr devModeOutput,
        IntPtr devModeInput,
        int mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memoryHandle);
}
