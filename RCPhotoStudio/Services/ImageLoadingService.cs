using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RCPhotoStudio.Services
{
    internal static class ImageLoadingService
    {
        public static bool ShouldRotateToMatch(double imageWidth, double imageHeight,
            double boxWidth, double boxHeight)
        {
            if (imageWidth <= 0 || imageHeight <= 0 || boxWidth <= 0 || boxHeight <= 0 ||
                Math.Abs(imageWidth - imageHeight) < 0.001 ||
                Math.Abs(boxWidth - boxHeight) < 0.001)
                return false;

            return (imageWidth > imageHeight) != (boxWidth > boxHeight);
        }

        public static ImageSource Load(string path, int decodePixelWidth)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    if (bitmap.CanFreeze) bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
