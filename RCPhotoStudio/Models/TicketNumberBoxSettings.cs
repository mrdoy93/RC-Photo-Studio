namespace RCPhotoStudio.Models
{
    public class TicketNumberBoxSettings
    {
        public double XInches { get; set; }
        public double YInches { get; set; }
        public double WidthInches { get; set; }
        public double HeightInches { get; set; }
        public double RotationDegrees { get; set; }
        public string TextValue { get; set; }
        public string FontFamilyName { get; set; }
        public double FontSizePoints { get; set; }
        public string TextColor { get; set; }
        public string BackgroundColor { get; set; }
        public bool IsBackgroundTransparent { get; set; }
        public string BorderColor { get; set; }
        public bool IsBorderTransparent { get; set; }
        public double BorderThicknessPixels { get; set; }

        public TicketNumberBoxSettings Clone()
        {
            return new TicketNumberBoxSettings
            {
                XInches = XInches,
                YInches = YInches,
                WidthInches = WidthInches,
                HeightInches = HeightInches,
                RotationDegrees = RotationDegrees,
                TextValue = TextValue,
                FontFamilyName = FontFamilyName,
                FontSizePoints = FontSizePoints,
                TextColor = TextColor,
                BackgroundColor = BackgroundColor,
                IsBackgroundTransparent = IsBackgroundTransparent,
                BorderColor = BorderColor,
                IsBorderTransparent = IsBorderTransparent,
                BorderThicknessPixels = BorderThicknessPixels
            };
        }
    }
}
