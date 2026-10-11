namespace PrinterScanner.Models;

public enum PrintColorMode
{
    Color,
    Grayscale,
    BlackAndWhite
}

public enum PrintQualityMode
{
    Draft,
    Normal,
    High
}

public sealed record PrintOptions(
    PrinterPaperSize PaperSize,
    PrintColorMode ColorMode,
    PrintQualityMode Quality,
    bool FitToSelectedPrintSize,
    int Copies);
