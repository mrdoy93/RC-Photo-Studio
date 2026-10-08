using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrinterScanner.Models;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PrinterScanner.Services;

public sealed class ImageService
{
    public BitmapSource LoadForDisplay(string path, int rotation = 0, int decodeWidth = 1100)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
        }
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();

        if (rotation == 0)
        {
            return image;
        }

        var transformed = new TransformedBitmap(image, new RotateTransform(rotation));
        transformed.Freeze();
        return transformed;
    }

    public BitmapSource LoadThumbnail(string path, int rotation = 0) => LoadForDisplay(path, rotation, 180);

    public BitmapSource LoadForPrint(string path, int rotation = 0) => LoadForDisplay(path, rotation, 0);

    public BitmapSource LoadForPrint(string path, int rotation, PrintColorMode colorMode)
    {
        var source = LoadForPrint(path, rotation);
        if (colorMode == PrintColorMode.Color)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(
            source,
            colorMode == PrintColorMode.BlackAndWhite ? PixelFormats.BlackWhite : PixelFormats.Gray8,
            null,
            0);
        converted.Freeze();
        return converted;
    }

    public void ProcessScan(string sourcePath, string destinationPath, ScanColorMode mode)
    {
        using var source = new Bitmap(sourcePath);
        using var working = To24Bit(source);

        switch (mode)
        {
            case ScanColorMode.Color:
                working.Save(destinationPath, ImageFormat.Png);
                break;
            case ScanColorMode.TrueGray:
                ConvertInPlace(working, PixelConversion.Grayscale);
                working.Save(destinationPath, ImageFormat.Png);
                break;
            case ScanColorMode.BlackAndWhite:
                using (var blackAndWhite = ToOneBit(working, useErrorDiffusion: false))
                {
                    blackAndWhite.Save(destinationPath, ImageFormat.Png);
                }
                break;
            case ScanColorMode.GrayErrorDiffusion:
                using (var diffused = ToOneBit(working, useErrorDiffusion: true))
                {
                    diffused.Save(destinationPath, ImageFormat.Png);
                }
                break;
        }
    }

    public Bitmap LoadPrintableBitmap(ScanPage page)
    {
        using var source = new Bitmap(page.FilePath);
        var result = new Bitmap(source);
        result.SetResolution(Math.Max(96, source.HorizontalResolution), Math.Max(96, source.VerticalResolution));

        if (page.Rotation == 90)
        {
            result.RotateFlip(RotateFlipType.Rotate90FlipNone);
        }
        else if (page.Rotation == 180)
        {
            result.RotateFlip(RotateFlipType.Rotate180FlipNone);
        }
        else if (page.Rotation == 270)
        {
            result.RotateFlip(RotateFlipType.Rotate270FlipNone);
        }

        return result;
    }

    public void SaveJpeg(ScanPage page, string outputPath, long quality)
    {
        using var bitmap = LoadPrintableBitmap(page);
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bitmap.Save(outputPath, encoder, parameters);
    }

    public void SavePng(ScanPage page, string outputPath)
    {
        using var bitmap = LoadPrintableBitmap(page);
        bitmap.Save(outputPath, ImageFormat.Png);
    }

    public void CropPage(
        ScanPage page,
        string outputPath,
        double normalizedLeft,
        double normalizedTop,
        double normalizedWidth,
        double normalizedHeight)
    {
        using var source = LoadPrintableBitmap(page);
        var left = Math.Clamp((int)Math.Floor(normalizedLeft * source.Width), 0, source.Width - 1);
        var top = Math.Clamp((int)Math.Floor(normalizedTop * source.Height), 0, source.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling((normalizedLeft + normalizedWidth) * source.Width), left + 1, source.Width);
        var bottom = Math.Clamp((int)Math.Ceiling((normalizedTop + normalizedHeight) * source.Height), top + 1, source.Height);
        var sourceRectangle = new Rectangle(left, top, right - left, bottom - top);

        using var cropped = new Bitmap(sourceRectangle.Width, sourceRectangle.Height, DrawingPixelFormat.Format32bppArgb);
        cropped.SetResolution(Math.Max(96, source.HorizontalResolution), Math.Max(96, source.VerticalResolution));
        using (var graphics = Graphics.FromImage(cropped))
        {
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, cropped.Width, cropped.Height),
                sourceRectangle,
                GraphicsUnit.Pixel);
        }

        cropped.Save(outputPath, ImageFormat.Png);
    }

    private static Bitmap To24Bit(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format24bppRgb);
        result.SetResolution(Math.Max(96, source.HorizontalResolution), Math.Max(96, source.VerticalResolution));
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImageUnscaled(source, 0, 0);
        return result;
    }

    private static void ConvertInPlace(Bitmap bitmap, PixelConversion conversion)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, DrawingPixelFormat.Format24bppRgb);
        try
        {
            var byteCount = Math.Abs(data.Stride) * data.Height;
            var pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            for (var y = 0; y < data.Height; y++)
            {
                var row = y * Math.Abs(data.Stride);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = row + (x * 3);
                    var b = pixels[index];
                    var g = pixels[index + 1];
                    var r = pixels[index + 2];

                    if (conversion == PixelConversion.Grayscale)
                    {
                        var gray = Luminance(r, g, b);
                        pixels[index] = gray;
                        pixels[index + 1] = gray;
                        pixels[index + 2] = gray;
                    }
                    else
                    {
                        pixels[index] = (byte)(b & 0xC0);
                        pixels[index + 1] = (byte)(g & 0xE0);
                        pixels[index + 2] = (byte)(r & 0xE0);
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap ToOneBit(Bitmap source, bool useErrorDiffusion)
    {
        var sourceRect = new Rectangle(0, 0, source.Width, source.Height);
        var sourceData = source.LockBits(sourceRect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format24bppRgb);
        var output = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format1bppIndexed);
        output.SetResolution(Math.Max(96, source.HorizontalResolution), Math.Max(96, source.VerticalResolution));
        var outputData = output.LockBits(sourceRect, ImageLockMode.WriteOnly, DrawingPixelFormat.Format1bppIndexed);

        try
        {
            var inputStride = Math.Abs(sourceData.Stride);
            var outputStride = Math.Abs(outputData.Stride);
            var input = new byte[inputStride * source.Height];
            var result = new byte[outputStride * source.Height];
            Marshal.Copy(sourceData.Scan0, input, 0, input.Length);

            var currentErrors = new float[source.Width + 2];
            var nextErrors = new float[source.Width + 2];

            for (var y = 0; y < source.Height; y++)
            {
                Array.Clear(nextErrors);
                var inputRow = y * inputStride;
                var outputRow = y * outputStride;

                for (var x = 0; x < source.Width; x++)
                {
                    var sourceIndex = inputRow + (x * 3);
                    var value = Luminance(input[sourceIndex + 2], input[sourceIndex + 1], input[sourceIndex]);
                    var adjusted = Math.Clamp(value + currentErrors[x + 1], 0f, 255f);
                    var white = adjusted >= 128f;

                    if (white)
                    {
                        result[outputRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }

                    if (useErrorDiffusion)
                    {
                        var error = adjusted - (white ? 255f : 0f);
                        currentErrors[x + 2] += error * 7f / 16f;
                        nextErrors[x] += error * 3f / 16f;
                        nextErrors[x + 1] += error * 5f / 16f;
                        nextErrors[x + 2] += error / 16f;
                    }
                }

                (currentErrors, nextErrors) = (nextErrors, currentErrors);
            }

            Marshal.Copy(result, 0, outputData.Scan0, result.Length);
        }
        finally
        {
            source.UnlockBits(sourceData);
            output.UnlockBits(outputData);
        }

        return output;
    }

    private static byte Luminance(byte r, byte g, byte b) =>
        (byte)Math.Clamp((int)Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b)), 0, 255);

    private enum PixelConversion
    {
        Grayscale
    }
}
