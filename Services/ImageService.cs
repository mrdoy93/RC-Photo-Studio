using System.Buffers;
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
    private static readonly byte[] Bayer4X4 =
    [
         0,  8,  2, 10,
        12,  4, 14,  6,
         3, 11,  1,  9,
        15,  7, 13,  5
    ];

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
                var colorLuminance = ReadLuminance(working);
                var colorMask = DetectDominantObject(
                    colorLuminance,
                    working.Width,
                    working.Height,
                    Math.Max(96f, working.HorizontalResolution));
                ApplyWhiteBackground(working, colorMask);
                working.Save(destinationPath, ImageFormat.Png);
                break;
            case ScanColorMode.TrueGray:
                var grayscale = EnhanceDocumentLuminance(
                    working,
                    detailStrength: 2,
                    out _);
                WriteGrayscaleInPlace(working, grayscale);
                working.Save(destinationPath, ImageFormat.Png);
                break;
            case ScanColorMode.BlackAndWhite:
                using (var blackAndWhite = ToOneBit(
                           working,
                           useErrorDiffusion: false,
                           out _))
                {
                    blackAndWhite.Save(destinationPath, ImageFormat.Png);
                }
                break;
            case ScanColorMode.GrayErrorDiffusion:
                using (var diffused = ToOneBit(
                           working,
                           useErrorDiffusion: true,
                           out _))
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

    private static byte[] EnhanceDocumentLuminance(
        Bitmap bitmap,
        int detailStrength,
        out Rectangle? subjectBounds)
    {
        var luminance = ReadLuminance(bitmap);

        var dpi = Math.Max(96f, bitmap.HorizontalResolution);
        var objectMask = DetectDominantObject(luminance, bitmap.Width, bitmap.Height, dpi);
        subjectBounds = FindMaskBounds(objectMask, bitmap.Width, bitmap.Height);
        NormalizeLevels(luminance, objectMask);
        ApplyWhiteBackground(luminance, objectMask);
        var windowSize = Math.Clamp((int)Math.Round(dpi / 4f), 31, 121) | 1;
        var enhanced = ApplyReferenceDocumentTone(
            luminance,
            bitmap.Width,
            bitmap.Height,
            windowSize,
            detailStrength,
            shadowLift: objectMask is null ? 24 : 240);
        ApplyWhiteBackground(enhanced, objectMask);
        return enhanced;
    }

    private static byte[] ReadLuminance(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format24bppRgb);
        var byteCount = Math.Abs(data.Stride) * data.Height;
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        var luminance = new byte[bitmap.Width * bitmap.Height];
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            for (var y = 0; y < data.Height; y++)
            {
                var row = y * Math.Abs(data.Stride);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = row + (x * 3);
                    luminance[(y * bitmap.Width) + x] = Luminance(
                        pixels[index + 2], pixels[index + 1], pixels[index]);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
            ArrayPool<byte>.Shared.Return(pixels);
        }

        return luminance;
    }

    private static bool[]? DetectDominantObject(
        byte[] luminance,
        int width,
        int height,
        float dpi)
    {
        var blockSize = Math.Clamp((int)Math.Round(dpi / 10f), 20, 48);
        var columns = (width + blockSize - 1) / blockSize;
        var rows = (height + blockSize - 1) / blockSize;
        var marked = new bool[columns * rows];
        var darkFractions = new double[columns * rows];

        for (var blockY = 1; blockY < rows - 1; blockY++)
        {
            var top = blockY * blockSize;
            var bottom = Math.Min(height, top + blockSize);
            for (var blockX = 1; blockX < columns - 1; blockX++)
            {
                var left = blockX * blockSize;
                var right = Math.Min(width, left + blockSize);
                var darkPixels = 0;
                var pixelCount = (right - left) * (bottom - top);
                for (var y = top; y < bottom; y++)
                {
                    var row = y * width;
                    for (var x = left; x < right; x++)
                    {
                        if (luminance[row + x] < 185)
                        {
                            darkPixels++;
                        }
                    }
                }

                var blockIndex = (blockY * columns) + blockX;
                darkFractions[blockIndex] = darkPixels / (double)pixelCount;
                marked[blockIndex] = darkFractions[blockIndex] >= 0.20;
            }
        }

        var visited = new bool[marked.Length];
        var queue = new Queue<int>();
        var bestCount = 0;
        var bestLeft = 0;
        var bestTop = 0;
        var bestRight = 0;
        var bestBottom = 0;
        List<Point>? bestBlocks = null;

        for (var index = 0; index < marked.Length; index++)
        {
            if (!marked[index] || visited[index])
            {
                continue;
            }

            visited[index] = true;
            queue.Enqueue(index);
            var count = 0;
            var minX = columns;
            var minY = rows;
            var maxX = 0;
            var maxY = 0;
            var accumulatedDarkFraction = 0d;
            var componentBlocks = new List<Point>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % columns;
                var y = current / columns;
                count++;
                accumulatedDarkFraction += darkFractions[current];
                componentBlocks.Add(new Point(x, y));
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (Math.Abs(offsetX) + Math.Abs(offsetY) != 1)
                        {
                            continue;
                        }

                        var neighborX = x + offsetX;
                        var neighborY = y + offsetY;
                        if (neighborX < 0 || neighborX >= columns || neighborY < 0 || neighborY >= rows)
                        {
                            continue;
                        }

                        var neighbor = (neighborY * columns) + neighborX;
                        if (marked[neighbor] && !visited[neighbor])
                        {
                            visited[neighbor] = true;
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            var boundingWidth = maxX - minX + 1;
            var boundingHeight = maxY - minY + 1;
            var boundingBlocks = boundingWidth * boundingHeight;
            var coverage = count / (double)boundingBlocks;
            var candidateLeft = minX * blockSize;
            var candidateTop = minY * blockSize;
            var candidateRight = Math.Min(width, (maxX + 1) * blockSize);
            var candidateBottom = Math.Min(height, (maxY + 1) * blockSize);
            var candidateAreaRatio =
                (candidateRight - candidateLeft) * (double)(candidateBottom - candidateTop) /
                (width * (double)height);
            var candidateAspectRatio = boundingWidth / (double)boundingHeight;
            var averageDarkFraction = accumulatedDarkFraction / count;
            var touchesScannerEdge = minX <= 1 || minY <= 1 ||
                                     maxX >= columns - 2 || maxY >= rows - 2;
            var validObject = coverage >= 0.40 &&
                              averageDarkFraction >= 0.45 &&
                              candidateAreaRatio is >= 0.005 and <= 0.55 &&
                              candidateAspectRatio is >= 0.18 and <= 5.5 &&
                              !touchesScannerEdge;
            if (validObject && count > bestCount)
            {
                bestCount = count;
                bestLeft = candidateLeft;
                bestTop = candidateTop;
                bestRight = candidateRight;
                bestBottom = candidateBottom;
                bestBlocks = componentBlocks;
            }
        }

        if (bestCount < 4 || bestBlocks is null)
        {
            return null;
        }

        var objectWidth = bestRight - bestLeft;
        var objectHeight = bestBottom - bestTop;
        var objectAreaRatio = objectWidth * (double)objectHeight / (width * (double)height);
        if (objectAreaRatio is < 0.01 or > 0.55)
        {
            return null;
        }

        var padding = Math.Clamp((int)Math.Round(dpi * 0.02f), 4, 18);
        var outlinePoints = new List<Point>(bestBlocks.Count * 4);
        foreach (var block in bestBlocks)
        {
            var left = Math.Max(0, (block.X * blockSize) - padding);
            var top = Math.Max(0, (block.Y * blockSize) - padding);
            var right = Math.Min(width, ((block.X + 1) * blockSize) + padding);
            var bottom = Math.Min(height, ((block.Y + 1) * blockSize) + padding);
            outlinePoints.Add(new Point(left, top));
            outlinePoints.Add(new Point(right, top));
            outlinePoints.Add(new Point(right, bottom));
            outlinePoints.Add(new Point(left, bottom));
        }

        var outline = ConvexHull(outlinePoints);
        if (outline.Count < 3)
        {
            return null;
        }

        var mask = new bool[luminance.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var scanY = y + 0.5;
            var minimumX = double.PositiveInfinity;
            var maximumX = double.NegativeInfinity;
            for (var index = 0; index < outline.Count; index++)
            {
                var start = outline[index];
                var end = outline[(index + 1) % outline.Count];
                if (!((start.Y <= scanY && end.Y > scanY) ||
                      (end.Y <= scanY && start.Y > scanY)))
                {
                    continue;
                }

                var intersectionX = start.X +
                    ((scanY - start.Y) * (end.X - start.X) / (end.Y - start.Y));
                minimumX = Math.Min(minimumX, intersectionX);
                maximumX = Math.Max(maximumX, intersectionX);
            }

            if (double.IsPositiveInfinity(minimumX))
            {
                continue;
            }

            var keepLeft = Math.Clamp((int)Math.Floor(minimumX), 0, width);
            var keepRight = Math.Clamp((int)Math.Ceiling(maximumX), keepLeft, width);
            Array.Fill(mask, true, row + keepLeft, keepRight - keepLeft);
        }

        return mask;
    }

    private static List<Point> ConvexHull(List<Point> points)
    {
        var ordered = points
            .Distinct()
            .OrderBy(point => point.X)
            .ThenBy(point => point.Y)
            .ToList();
        if (ordered.Count <= 2)
        {
            return ordered;
        }

        var lower = new List<Point>();
        foreach (var point in ordered)
        {
            while (lower.Count >= 2 &&
                   Cross(lower[^2], lower[^1], point) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<Point>();
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var point = ordered[index];
            while (upper.Count >= 2 &&
                   Cross(upper[^2], upper[^1], point) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static long Cross(Point origin, Point first, Point second) =>
        ((long)first.X - origin.X) * (second.Y - origin.Y) -
        ((long)first.Y - origin.Y) * (second.X - origin.X);

    private static void NormalizeLevels(byte[] luminance, bool[]? mask)
    {
        var histogram = new int[256];
        var sampleCount = 0;
        for (var index = 0; index < luminance.Length; index++)
        {
            if (mask is not null && !mask[index])
            {
                continue;
            }

            histogram[luminance[index]]++;
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return;
        }

        var darkPoint = Percentile(histogram, sampleCount, 0.02);
        var measuredLightPoint = Percentile(histogram, sampleCount, 0.90);
        var objectBodyPoint = Percentile(histogram, sampleCount, 0.70);
        var darkDetectedObject = mask is not null && objectBodyPoint < 120;
        var lightPoint = darkDetectedObject
            ? objectBodyPoint
            : Math.Max(180, measuredLightPoint);
        if (lightPoint - darkPoint < 64)
        {
            darkPoint = Math.Max(0, lightPoint - (darkDetectedObject ? 48 : 160));
        }

        var range = Math.Max(1, lightPoint - darkPoint);
        for (var index = 0; index < luminance.Length; index++)
        {
            if (mask is not null && !mask[index])
            {
                continue;
            }

            var normalized = darkDetectedObject
                ? 20 + Math.Clamp((luminance[index] - darkPoint) * 210 / range, 0, 210)
                : Math.Clamp((luminance[index] - darkPoint) * 255 / range, 0, 255);
            var whiteSnapThreshold = mask is null ? 232 : 250;
            luminance[index] = (byte)(normalized >= whiteSnapThreshold ? 255 : normalized);
        }
    }

    private static void ApplyWhiteBackground(byte[] luminance, bool[]? mask)
    {
        if (mask is null)
        {
            return;
        }

        for (var index = 0; index < luminance.Length; index++)
        {
            if (!mask[index])
            {
                luminance[index] = 255;
            }
        }
    }

    private static void ApplyWhiteBackground(Bitmap bitmap, bool[]? mask)
    {
        if (mask is null)
        {
            return;
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, DrawingPixelFormat.Format24bppRgb);
        var byteCount = Math.Abs(data.Stride) * data.Height;
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            for (var y = 0; y < bitmap.Height; y++)
            {
                var pixelRow = y * Math.Abs(data.Stride);
                var maskRow = y * bitmap.Width;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (mask[maskRow + x])
                    {
                        continue;
                    }

                    var pixelIndex = pixelRow + (x * 3);
                    pixels[pixelIndex] = 255;
                    pixels[pixelIndex + 1] = 255;
                    pixels[pixelIndex + 2] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static Rectangle? FindMaskBounds(bool[]? mask, int width, int height)
    {
        if (mask is null)
        {
            return null;
        }

        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (!mask[row + x])
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right >= left && bottom >= top
            ? new Rectangle(left, top, right - left + 1, bottom - top + 1)
            : null;
    }

    private static void SaveProcessedBitmap(
        Bitmap bitmap,
        string destinationPath,
        Rectangle? subjectBounds)
    {
        if (subjectBounds is null ||
            subjectBounds.Value.Width == bitmap.Width &&
            subjectBounds.Value.Height == bitmap.Height)
        {
            bitmap.Save(destinationPath, ImageFormat.Png);
            return;
        }

        using var cropped = bitmap.Clone(subjectBounds.Value, bitmap.PixelFormat);
        cropped.SetResolution(
            Math.Max(96, bitmap.HorizontalResolution),
            Math.Max(96, bitmap.VerticalResolution));
        cropped.Save(destinationPath, ImageFormat.Png);
    }

    private static int Percentile(int[] histogram, int total, double percentile)
    {
        var target = (int)Math.Ceiling(total * percentile);
        var accumulated = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            accumulated += histogram[value];
            if (accumulated >= target)
            {
                return value;
            }
        }

        return 255;
    }

    private static byte[] ApplyReferenceDocumentTone(
        byte[] source,
        int width,
        int height,
        int windowSize,
        int detailStrength,
        int shadowLift)
    {
        var result = new byte[source.Length];
        var radius = windowSize / 2;
        var columnSums = new int[width];
        var initialBottom = Math.Min(height - 1, radius);
        for (var y = 0; y <= initialBottom; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                columnSums[x] += source[row + x];
            }
        }

        for (var y = 0; y < height; y++)
        {
            if (y > 0)
            {
                var removeY = y - radius - 1;
                if (removeY >= 0)
                {
                    var removeRow = removeY * width;
                    for (var x = 0; x < width; x++)
                    {
                        columnSums[x] -= source[removeRow + x];
                    }
                }

                var addY = y + radius;
                if (addY < height)
                {
                    var addRow = addY * width;
                    for (var x = 0; x < width; x++)
                    {
                        columnSums[x] += source[addRow + x];
                    }
                }
            }

            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(height - 1, y + radius);
            var verticalCount = bottom - top + 1;
            long windowSum = 0;
            var initialRight = Math.Min(width - 1, radius);
            for (var x = 0; x <= initialRight; x++)
            {
                windowSum += columnSums[x];
            }

            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (x > 0)
                {
                    var removeX = x - radius - 1;
                    if (removeX >= 0)
                    {
                        windowSum -= columnSums[removeX];
                    }

                    var addX = x + radius;
                    if (addX < width)
                    {
                        windowSum += columnSums[addX];
                    }
                }

                var left = Math.Max(0, x - radius);
                var right = Math.Min(width - 1, x + radius);
                var localMean = (int)(windowSum / ((right - left + 1) * verticalCount));
                var value = source[row + x];
                var difference = localMean - value;
                var lifted = value == 0
                    ? 0
                    : value * (255 + shadowLift) / (value + shadowLift);
                var detail = Math.Max(0, difference - 6) * detailStrength;
                if (difference >= 10)
                {
                    detail += 18;
                }
                result[row + x] = (byte)Math.Clamp(lifted - detail, 0, 255);
            }
        }

        return result;
    }

    private static void WriteGrayscaleInPlace(Bitmap bitmap, byte[] luminance)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, DrawingPixelFormat.Format24bppRgb);
        var byteCount = Math.Abs(data.Stride) * data.Height;
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            for (var y = 0; y < data.Height; y++)
            {
                var pixelRow = y * Math.Abs(data.Stride);
                var grayRow = y * bitmap.Width;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixelIndex = pixelRow + (x * 3);
                    var gray = luminance[grayRow + x];
                    pixels[pixelIndex] = gray;
                    pixels[pixelIndex + 1] = gray;
                    pixels[pixelIndex + 2] = gray;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static Bitmap ToOneBit(
        Bitmap source,
        bool useErrorDiffusion,
        out Rectangle? subjectBounds)
    {
        var sourceRect = new Rectangle(0, 0, source.Width, source.Height);
        var output = new Bitmap(source.Width, source.Height, DrawingPixelFormat.Format1bppIndexed);
        output.SetResolution(Math.Max(96, source.HorizontalResolution), Math.Max(96, source.VerticalResolution));
        var outputData = output.LockBits(sourceRect, ImageLockMode.WriteOnly, DrawingPixelFormat.Format1bppIndexed);

        try
        {
            var outputStride = Math.Abs(outputData.Stride);
            var result = new byte[outputStride * source.Height];
            var input = EnhanceDocumentLuminance(
                source,
                detailStrength: useErrorDiffusion ? 3 : 3,
                out subjectBounds);

            var currentErrors = new float[source.Width + 2];
            var nextErrors = new float[source.Width + 2];

            for (var y = 0; y < source.Height; y++)
            {
                Array.Clear(nextErrors);
                var inputRow = y * source.Width;
                var outputRow = y * outputStride;

                for (var x = 0; x < source.Width; x++)
                {
                    if (input[inputRow + x] == 255)
                    {
                        result[outputRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        currentErrors[x + 1] = 0;
                        continue;
                    }

                    var adjusted = Math.Clamp(input[inputRow + x] + currentErrors[x + 1], 0f, 255f);
                    var threshold = useErrorDiffusion
                        ? 128f
                        : 96f + (Bayer4X4[((y & 3) * 4) + (x & 3)] * 10f);
                    var white = adjusted >= threshold;

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
            output.UnlockBits(outputData);
        }

        return output;
    }

    private static byte Luminance(byte r, byte g, byte b) =>
        (byte)Math.Clamp((int)Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b)), 0, 255);

}
