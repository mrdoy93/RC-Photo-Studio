namespace PrinterScanner.Models;

public sealed record PrinterDevice(string Name)
{
    public bool IsPreferredBrand =>
        Name.Contains("Epson", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Brother", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => IsPreferredBrand ? Name : $"{Name} (other brand)";

    public override string ToString() => Name;
}
