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
    string PaperSize,
    PrintColorMode ColorMode,
    PrintQualityMode Quality,
    string ReductionPreset,
    int Copies);
