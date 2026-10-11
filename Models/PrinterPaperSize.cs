using System.Globalization;
using System.Printing;

namespace PrinterScanner.Models;

public sealed record PrinterPaperSize(
    string Name,
    PageMediaSizeName? MediaSizeName,
    double Width,
    double Height,
    int? DriverPaperKind = null)
{
    private const double DipsPerMillimeter = 96d / 25.4d;

    public string DisplayName =>
        Width > 0 && Height > 0
            ? $"{Name} ({FormatMillimeters(Width)} × {FormatMillimeters(Height)} mm)"
            : Name;

    public static PrinterPaperSize FromHundredthsOfAnInch(
        string name,
        int width,
        int height,
        int? driverPaperKind = null) =>
        new(
            name,
            null,
            Math.Max(1, width) * 0.96d,
            Math.Max(1, height) * 0.96d,
            driverPaperKind);

    public bool HasSameDimensions(double? width, double? height, double tolerance = 2d)
    {
        if (width is null || height is null || Width <= 0 || Height <= 0)
        {
            return false;
        }

        return Math.Abs(Width - width.Value) <= tolerance && Math.Abs(Height - height.Value) <= tolerance ||
               Math.Abs(Width - height.Value) <= tolerance && Math.Abs(Height - width.Value) <= tolerance;
    }

    public override string ToString() => DisplayName;

    private static string FormatMillimeters(double dips)
    {
        var millimeters = dips / DipsPerMillimeter;
        return millimeters.ToString(
            Math.Abs(millimeters - Math.Round(millimeters)) < 0.05d ? "0" : "0.#",
            CultureInfo.CurrentCulture);
    }
}
