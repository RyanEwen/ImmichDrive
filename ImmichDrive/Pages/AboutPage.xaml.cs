using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Reflection;

namespace ImmichDrive.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v != null ? $"Version {v.Major}.{v.Minor}.{v.Build}" : "";
        if (File.Exists(App.IconPath))
        {
            try { AppIcon.Source = new BitmapImage(new Uri(App.IconPath)); } catch { }
        }
    }
}
