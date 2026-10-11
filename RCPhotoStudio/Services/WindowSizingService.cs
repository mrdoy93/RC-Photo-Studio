using System;
using System.Windows;

namespace RCPhotoStudio.Services
{
    public static class WindowSizingService
    {
        public static void Maximize(Window window)
        {
            if (window == null) return;
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
            window.WindowState = WindowState.Maximized;
        }

        public static void SetToScreenPercentage(Window window, double widthPercentage, double heightPercentage)
        {
            if (window == null) return;
            widthPercentage = Math.Max(0.25, Math.Min(1.0, widthPercentage));
            heightPercentage = Math.Max(0.25, Math.Min(1.0, heightPercentage));

            var workArea = SystemParameters.WorkArea;
            var targetWidth = Math.Floor(workArea.Width * widthPercentage);
            var targetHeight = Math.Floor(workArea.Height * heightPercentage);
            window.MinWidth = Math.Min(window.MinWidth, targetWidth);
            window.MinHeight = Math.Min(window.MinHeight, targetHeight);
            window.Width = targetWidth;
            window.Height = targetHeight;

            // Keep the controls at their authored pixel sizes. Only the available
            // layout area changes; WPF will reflow or scroll the content as needed.
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
        }
    }
}
