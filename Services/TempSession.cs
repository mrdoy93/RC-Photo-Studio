using System.IO;

namespace PrinterScanner.Services;

public sealed class TempSession : IDisposable
{
    private readonly string _folder;

    public TempSession()
    {
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrinterScanner",
            "Temp",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public string NewPath(string extension)
    {
        if (!extension.StartsWith('.'))
        {
            extension = $".{extension}";
        }

        return Path.Combine(_folder, $"{Guid.NewGuid():N}{extension}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch
        {
            // Windows image codecs or a printer driver may briefly retain a file handle.
        }
    }
}
