using System.IO;

namespace Visor.Services;

/// <summary>Loads embedded pack:// assets.</summary>
public static class AssetLoader
{
    private const string PackRoot = "pack://application:,,,/Assets/";

    /// <summary>Best-fitting frame of Assets/app.ico (ICO frame order is not guaranteed).</summary>
    public static System.Windows.Media.ImageSource? AppIcon(int desiredPx = 64)
    {
        try
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                new Uri(PackRoot + "app.ico", UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderBy(f => f.PixelWidth >= desiredPx ? 0 : 1)
                .ThenBy(f => Math.Abs(f.PixelWidth - desiredPx))
                .FirstOrDefault();
            frame?.Freeze();
            return frame;
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
}
