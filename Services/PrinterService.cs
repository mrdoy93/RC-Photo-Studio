using System.Drawing.Printing;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using PrinterScanner.Models;

namespace PrinterScanner.Services;

public sealed class PrinterService
{
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

    public IReadOnlyList<PrinterPaperSize> GetPaperSizes(PrinterDevice printer)
    {
        var sizes = new List<PrinterPaperSize>();
        PrinterPaperSize? defaultSize = null;

        try
        {
            var settings = new PrinterSettings { PrinterName = printer.Name };
            if (settings.IsValid)
            {
                foreach (System.Drawing.Printing.PaperSize driverSize in settings.PaperSizes)
                {
                    if (driverSize.Width <= 0 || driverSize.Height <= 0)
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(driverSize.PaperName)
                        ? driverSize.Kind.ToString()
                        : driverSize.PaperName.Trim();
                    var detected = PrinterPaperSize.FromHundredthsOfAnInch(
                        name,
                        driverSize.Width,
                        driverSize.Height,
                        driverSize.RawKind);
                    AddUniqueSize(sizes, detected);
                }

                var driverDefault = settings.DefaultPageSettings.PaperSize;
                if (driverDefault.Width > 0 && driverDefault.Height > 0)
                {
                    defaultSize = PrinterPaperSize.FromHundredthsOfAnInch(
                        driverDefault.PaperName,
                        driverDefault.Width,
                        driverDefault.Height,
                        driverDefault.RawKind);
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidPrinterException or System.ComponentModel.Win32Exception)
        {
            // Some XPS-only and legacy drivers expose sizes only through PrintCapabilities.
        }

        try
        {
            using var printServer = new LocalPrintServer();
            using var printQueue = printServer.GetPrintQueue(printer.Name);
            printQueue.Refresh();
            var capabilities = printQueue.GetPrintCapabilities();
            if (capabilities.PageMediaSizeCapability is { } mediaSizes)
            {
                foreach (var mediaSize in mediaSizes)
                {
                    if (mediaSize.Width is not > 0 || mediaSize.Height is not > 0)
                    {
                        continue;
                    }

                    var mediaSizeName = mediaSize.PageMediaSizeName is PageMediaSizeName.Unknown
                        ? null
                        : mediaSize.PageMediaSizeName;
                    var matchingIndex = sizes.FindIndex(size =>
                        size.HasSameDimensions(mediaSize.Width, mediaSize.Height));
                    if (matchingIndex >= 0)
                    {
                        if (sizes[matchingIndex].MediaSizeName is null && mediaSizeName is not null)
                        {
                            sizes[matchingIndex] = sizes[matchingIndex] with
                            {
                                MediaSizeName = mediaSizeName
                            };
                        }
                        continue;
                    }

                    AddUniqueSize(sizes, new PrinterPaperSize(
                        GetMediaDisplayName(mediaSizeName),
                        mediaSizeName,
                        mediaSize.Width.Value,
                        mediaSize.Height.Value));
                }
            }

            var ticketSize = (printQueue.UserPrintTicket ?? printQueue.DefaultPrintTicket)?.PageMediaSize;
            if (ticketSize?.Width is > 0 && ticketSize.Height is > 0)
            {
                var ticketSizeName = ticketSize.PageMediaSizeName is PageMediaSizeName.Unknown
                    ? null
                    : ticketSize.PageMediaSizeName;
                defaultSize = new PrinterPaperSize(
                    GetMediaDisplayName(ticketSizeName),
                    ticketSizeName,
                    ticketSize.Width.Value,
                    ticketSize.Height.Value);
            }
        }
        catch (Exception exception) when (
            exception is PrintSystemException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Keep sizes already reported by the Win32 printer driver.
        }

        if (sizes.Count == 0)
        {
            sizes.Add(new PrinterPaperSize("A4", PageMediaSizeName.ISOA4, 793.7, 1122.52));
            sizes.Add(new PrinterPaperSize("Letter", PageMediaSizeName.NorthAmericaLetter, 816, 1056));
        }

        if (defaultSize is not null)
        {
            var defaultIndex = sizes.FindIndex(size =>
                defaultSize.MediaSizeName is not null && size.MediaSizeName == defaultSize.MediaSizeName ||
                size.HasSameDimensions(defaultSize.Width, defaultSize.Height));
            if (defaultIndex > 0)
            {
                var selectedDefault = sizes[defaultIndex];
                sizes.RemoveAt(defaultIndex);
                sizes.Insert(0, selectedDefault);
            }
        }

        return sizes;
    }

    private static void AddUniqueSize(List<PrinterPaperSize> sizes, PrinterPaperSize candidate)
    {
        var duplicate = sizes.Any(size =>
            string.Equals(size.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
            size.HasSameDimensions(candidate.Width, candidate.Height));
        if (!duplicate)
        {
            sizes.Add(candidate);
        }
    }

    private static string GetMediaDisplayName(PageMediaSizeName? mediaSizeName)
    {
        if (mediaSizeName is null or PageMediaSizeName.Unknown)
        {
            return "Custom";
        }

        return mediaSizeName switch
        {
            PageMediaSizeName.ISOA3 => "A3",
            PageMediaSizeName.ISOA4 => "A4",
            PageMediaSizeName.ISOA5 => "A5",
            PageMediaSizeName.NorthAmericaLetter => "Letter",
            PageMediaSizeName.NorthAmericaLegal => "Legal",
            _ => Regex.Replace(mediaSizeName.Value.ToString(), "(?<=[a-z0-9])(?=[A-Z])", " ")
                .Replace("ISO", string.Empty, StringComparison.Ordinal)
                .Replace("North America ", string.Empty, StringComparison.Ordinal)
        };
    }

    private static bool TicketUsesRequestedPaper(PrintTicket ticket, PrinterPaperSize requestedSize)
    {
        var mediaSize = ticket.PageMediaSize;

        return (requestedSize.MediaSizeName is not null &&
                mediaSize?.PageMediaSizeName == requestedSize.MediaSizeName) ||
               requestedSize.HasSameDimensions(mediaSize?.Width, mediaSize?.Height, 8d);
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
        if (!TicketUsesRequestedPaper(printTicket, options.PaperSize))
        {
            throw new InvalidOperationException(
                $"{options.PaperSize.DisplayName} is not available on this printer model.");
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
            var source = _imageService.LoadForPrint(page.FilePath, page.Rotation, options.ColorMode);
            var image = new System.Windows.Controls.Image
            {
                Source = source,
                SnapsToDevicePixels = true
            };
            if (options.FitToSelectedPrintSize)
            {
                image.Width = printableWidth;
                image.Height = printableHeight;
                image.Stretch = Stretch.Uniform;
            }
            else
            {
                image.Width = source.Width;
                image.Height = source.Height;
                image.Stretch = Stretch.None;
            }
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
        ticket.PageMediaSize = options.PaperSize.MediaSizeName is { } mediaSizeName
            ? new PageMediaSize(mediaSizeName)
            : new PageMediaSize(options.PaperSize.Width, options.PaperSize.Height);
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
