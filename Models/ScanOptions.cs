namespace PrinterScanner.Models;

public enum ScanSource
{
    Flatbed,
    AutomaticDocumentFeeder
}

public enum ScanColorMode
{
    Color,
    TrueGray,
    GrayErrorDiffusion,
    BlackAndWhite
}

public enum PdfCompression
{
    Smallest,
    Balanced,
    HighQuality
}

public sealed record ScanOptions(
    ScanSource Source,
    ScanColorMode ColorMode,
    int Dpi,
    string PaperSize);
