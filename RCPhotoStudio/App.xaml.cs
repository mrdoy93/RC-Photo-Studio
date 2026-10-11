using System.Windows;
using RCPhotoStudio.Services;

namespace RCPhotoStudio
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ThemeService.Apply(true);
            base.OnStartup(e);
        }
    }
}
