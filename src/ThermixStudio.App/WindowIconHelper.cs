using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ThermixStudio.App;

internal static class WindowIconHelper
{
    private const string PrimaryIconName = "icone_windows.ico";

    public static void Apply(Window window)
    {
        try
        {
            // Primary: use manifest resource stream — works in single-file publish
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using var resStream = asm.GetManifestResourceStream("ThermixStudio.App.icone_windows");
                if (resStream is not null)
                {
                    var decoder = new IconBitmapDecoder(
                        resStream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    var bestFrame = decoder.Frames
                        .Where(f => f.PixelWidth > 0 && f.PixelHeight > 0 && f.PixelWidth == f.PixelHeight)
                        .OrderByDescending(f => f.PixelWidth)
                        .FirstOrDefault()
                        ?? decoder.Frames.FirstOrDefault();

                    if (bestFrame is not null)
                    {
                        bestFrame.Freeze();
                        window.Icon = bestFrame;
                        return;
                    }
                }
            }
            catch { }

            // Fallback: Try pack:// URI with component syntax (for embedded resources)
            var uris = new[]
            {
                new Uri($"pack://application:,,,/ThermixStudio.App;component/icone_windows.ico", UriKind.Absolute),
                new Uri($"pack://application:,,,/{PrimaryIconName}", UriKind.Absolute),
            };

            foreach (var resourceUri in uris)
            {
                try
                {
                    var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
                    if (streamInfo is not null)
                    {
                        using var stream = streamInfo.Stream;
                        var decoder = new IconBitmapDecoder(
                            stream,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);

                        var bestFrame = decoder.Frames
                            .Where(f => f.PixelWidth > 0 && f.PixelHeight > 0 && f.PixelWidth == f.PixelHeight)
                            .OrderByDescending(f => f.PixelWidth)
                            .FirstOrDefault()
                            ?? decoder.Frames.FirstOrDefault();

                        if (bestFrame is not null)
                        {
                            bestFrame.Freeze();
                            window.Icon = bestFrame;
                            return;
                        }
                    }
                }
                catch { }
            }

            // Fall back to file on disk
            var iconPath = Path.Combine(AppContext.BaseDirectory, PrimaryIconName);
            if (!File.Exists(iconPath))
            {
                return;
            }

            using var fileStream = File.OpenRead(iconPath);
            var fileDecoder = new IconBitmapDecoder(
                fileStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var fileFrame = fileDecoder.Frames
                .Where(f => f.PixelWidth > 0 && f.PixelHeight > 0 && f.PixelWidth == f.PixelHeight)
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault()
                ?? fileDecoder.Frames.FirstOrDefault();

            if (fileFrame is not null)
            {
                fileFrame.Freeze();
                window.Icon = fileFrame;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIconHelper] Icon load failed: {ex.Message}");
        }
    }
}
