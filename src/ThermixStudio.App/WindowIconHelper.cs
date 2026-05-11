using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ThermixStudio.App;

internal static class WindowIconHelper
{
    public static void Apply(Window window)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "thermix_studio.ico");
            if (!File.Exists(iconPath))
            {
                return;
            }

            using var stream = File.OpenRead(iconPath);
            var decoder = new IconBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var bestFrame = decoder.Frames
                .Where(f => f.PixelWidth > 0 && f.PixelHeight > 0 && f.PixelWidth == f.PixelHeight)
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault()
                ?? decoder.Frames.FirstOrDefault();

            if (bestFrame is null)
            {
                return;
            }

            bestFrame.Freeze();
            window.Icon = bestFrame;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIconHelper] Icon load failed: {ex.Message}");
        }
    }
}
