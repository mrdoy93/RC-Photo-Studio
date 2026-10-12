using System.Collections.Generic;
using System.Linq;

namespace RCPhotoStudio.Models
{
    public class PaperDefinition
    {
        public string Name { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }

        public override string ToString() { return Name; }

        public static IList<PaperDefinition> StandardPapers()
        {
            return new List<PaperDefinition>
            {
                new PaperDefinition { Name = "4R (4 × 6 in)", WidthMm = 101.6, HeightMm = 152.4 },
                new PaperDefinition { Name = "5R (5 × 7 in)", WidthMm = 127.0, HeightMm = 177.8 },
                new PaperDefinition { Name = "A4", WidthMm = 210.0, HeightMm = 297.0 },
                new PaperDefinition { Name = "Letter", WidthMm = 215.9, HeightMm = 279.4 },
                new PaperDefinition { Name = "Legal", WidthMm = 215.9, HeightMm = 355.6 }
            };
        }

        public static PaperDefinition Find(string name)
        {
            return StandardPapers().FirstOrDefault(p => p.Name == name) ?? StandardPapers()[0];
        }
    }
}
