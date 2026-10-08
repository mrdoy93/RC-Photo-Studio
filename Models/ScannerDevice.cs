namespace PrinterScanner.Models;

public sealed record ScannerDevice(string Id, string Name, string Manufacturer)
{
    public bool IsPreferredBrand =>
        Name.Contains("Epson", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Brother", StringComparison.OrdinalIgnoreCase) ||
        Manufacturer.Contains("Epson", StringComparison.OrdinalIgnoreCase) ||
        Manufacturer.Contains("Brother", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => IsPreferredBrand ? Name : $"{Name} (other brand)";

    public override string ToString() => Name;
}
