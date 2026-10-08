using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace PrinterScanner.Models;

public sealed class ScanPage : INotifyPropertyChanged
{
    private int _displayNumber;
    private int _rotation;
    private BitmapSource _thumbnail;

    public ScanPage(string filePath, BitmapSource thumbnail, ScanColorMode colorMode)
    {
        FilePath = filePath;
        _thumbnail = thumbnail;
        ColorMode = colorMode;
    }

    public string FilePath { get; }
    public ScanColorMode ColorMode { get; }

    public int DisplayNumber
    {
        get => _displayNumber;
        set => SetField(ref _displayNumber, value);
    }

    public int Rotation
    {
        get => _rotation;
        set => SetField(ref _rotation, ((value % 360) + 360) % 360);
    }

    public BitmapSource Thumbnail
    {
        get => _thumbnail;
        set => SetField(ref _thumbnail, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
