using System.IO;
using System.Windows.Media.Imaging;

namespace DisplayProfileManager.Services;

/// <summary>Loads embedded pack:// assets — no loose Assets folder on disk.</summary>
public static class AssetLoader
{
    private const string PackRoot = "pack://application:,,,/Assets/";

    public static BitmapImage? Image(string fileName)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(PackRoot + fileName, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static Stream? OpenStream(string fileName)
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri(PackRoot + fileName, UriKind.Absolute));
            return info?.Stream;
        }
        catch
        {
            return null;
        }
    }

    public static readonly string[] KnownImages =
    {
        "banner-walk.jpg", "banner-shelf.jpg", "banner-desk.jpg", "banner-wave.jpg", "banner-run.jpg",
        "hall-walk.jpg", "hall-shelf.jpg", "hall-desk.jpg", "hall-wave.jpg", "hall-run.jpg",
        "save-shelf.jpg", "scan-shelf.jpg", "boot-splash.jpg"
    };
}
