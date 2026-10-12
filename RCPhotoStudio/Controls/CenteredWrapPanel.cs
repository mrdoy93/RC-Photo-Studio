using System;
using System.Windows;
using System.Windows.Controls;

namespace RCPhotoStudio.Controls
{
    public sealed class CenteredWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var availableWidth = double.IsInfinity(availableSize.Width)
                ? double.MaxValue
                : Math.Max(0, availableSize.Width);
            var lineWidth = 0.0;
            var lineHeight = 0.0;
            var requiredWidth = 0.0;
            var requiredHeight = 0.0;

            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = child.DesiredSize;

                if (lineWidth > 0 && lineWidth + size.Width > availableWidth)
                {
                    requiredWidth = Math.Max(requiredWidth, lineWidth);
                    requiredHeight += lineHeight;
                    lineWidth = 0;
                    lineHeight = 0;
                }

                lineWidth += size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
            }

            requiredWidth = Math.Max(requiredWidth, lineWidth);
            requiredHeight += lineHeight;
            return new Size(double.IsInfinity(availableSize.Width)
                ? requiredWidth
                : Math.Min(requiredWidth, availableSize.Width), requiredHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var lineStart = 0;
            var lineWidth = 0.0;
            var lineHeight = 0.0;
            var top = 0.0;

            for (var index = 0; index < InternalChildren.Count; index++)
            {
                var child = InternalChildren[index];
                if (child == null) continue;
                var size = child.DesiredSize;

                if (lineWidth > 0 && lineWidth + size.Width > finalSize.Width)
                {
                    ArrangeLine(lineStart, index, top, lineWidth, lineHeight, finalSize.Width);
                    top += lineHeight;
                    lineStart = index;
                    lineWidth = 0;
                    lineHeight = 0;
                }

                lineWidth += size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
            }

            ArrangeLine(lineStart, InternalChildren.Count, top, lineWidth, lineHeight, finalSize.Width);
            return finalSize;
        }

        private void ArrangeLine(int start, int end, double top, double lineWidth,
            double lineHeight, double availableWidth)
        {
            var left = Math.Max(0, (availableWidth - lineWidth) / 2);
            for (var index = start; index < end; index++)
            {
                var child = InternalChildren[index];
                if (child == null) continue;
                var size = child.DesiredSize;
                child.Arrange(new Rect(left, top + (lineHeight - size.Height) / 2, size.Width, size.Height));
                left += size.Width;
            }
        }
    }
}
