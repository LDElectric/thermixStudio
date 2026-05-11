using System.Windows.Media;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services;

public sealed class ThermalRenderEngine : IThermalRenderEngine
{
    public ThermalRenderResult Render(ThermalImageData image, ThermalRenderParameters parameters)
    {
        var width = image.Width;
        var height = image.Height;

        var (absoluteMin, absoluteMax) = GetAbsoluteRange(image);
        var appliedMin = parameters.AutoScale ? absoluteMin : parameters.LevelMinC ?? absoluteMin;
        var appliedMax = parameters.AutoScale ? absoluteMax : parameters.LevelMaxC ?? absoluteMax;

        if (appliedMax <= appliedMin)
        {
            appliedMax = appliedMin + 0.01;
        }

        var range = appliedMax - appliedMin;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = image.Temperatures[y, x];
                var n = Math.Clamp((t - appliedMin) / range, 0.0, 1.0);
                var color = parameters.Palette switch
                {
                    ThermalPalette.Rainbow => MapRainbow(n),
                    ThermalPalette.Grayscale => MapGrayscale(n),
                    _ => MapIron(n)
                };

                var idx = ((y * width) + x) * 4;
                pixels[idx] = color.B;
                pixels[idx + 1] = color.G;
                pixels[idx + 2] = color.R;
                pixels[idx + 3] = 255;
            }
        }

        return new ThermalRenderResult
        {
            Width = width,
            Height = height,
            BgraPixels = pixels,
            AppliedMinC = appliedMin,
            AppliedMaxC = appliedMax
        };
    }

    private static (double min, double max) GetAbsoluteRange(ThermalImageData image)
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
        {
            return (0, 1);
        }

        return (min, max);
    }

    private static Color MapGrayscale(double n)
    {
        var value = (byte)(n * 255);
        return Color.FromRgb(value, value, value);
    }

    // Interpolates linearly between an array of (position, R, G, B) control points.
    private static Color LerpStops((double T, int R, int G, int B)[] stops, double n)
    {
        for (var i = 1; i < stops.Length; i++)
        {
            if (n <= stops[i].T)
            {
                var span = stops[i].T - stops[i - 1].T;
                var f = span <= 0 ? 0 : (n - stops[i - 1].T) / span;
                return Color.FromRgb(
                    (byte)(stops[i - 1].R + f * (stops[i].R - stops[i - 1].R)),
                    (byte)(stops[i - 1].G + f * (stops[i].G - stops[i - 1].G)),
                    (byte)(stops[i - 1].B + f * (stops[i].B - stops[i - 1].B)));
            }
        }
        var last = stops[stops.Length - 1];
        return Color.FromRgb((byte)last.R, (byte)last.G, (byte)last.B);
    }

    // FLIR Iron (Ironbow): black → dark indigo → purple → dark red → orange → yellow → white
    // Calibrated from FLIR E-series camera output.
    private static Color MapIron(double n)
    {
        (double T, int R, int G, int B)[] stops =
        {
            (0.00,   0,   0,   0),   // black
            (0.10,   5,   0,  40),   // near-black with slight indigo
            (0.20,  25,   0,  90),   // dark indigo
            (0.30,  75,   0, 110),   // dark purple/violet
            (0.40, 130,   0,  75),   // deep magenta
            (0.50, 175,  15,   5),   // dark red
            (0.60, 225,  70,   0),   // orange-red
            (0.70, 255, 150,   0),   // orange
            (0.80, 255, 230,  25),   // yellow
            (0.92, 255, 255, 190),   // light yellow / cream
            (1.00, 255, 255, 255),   // white
        };
        return LerpStops(stops, n);
    }

    // FLIR Rainbow (Arco-Íris): dark navy → blue → cyan → green → yellow → orange → dark red
    // Calibrated from FLIR E-series camera output.
    private static Color MapRainbow(double n)
    {
        (double T, int R, int G, int B)[] stops =
        {
            (0.00,   0,   0,  50),   // near-black navy
            (0.12,   0,   0, 160),   // dark blue
            (0.25,   0,  60, 210),   // medium blue
            (0.38,   0, 180, 215),   // cyan
            (0.50,   0, 215, 120),   // cyan-green
            (0.60,  50, 220,   0),   // green
            (0.70, 200, 225,   0),   // yellow-green
            (0.80, 255, 140,   0),   // orange
            (0.90, 245,  35,   0),   // red-orange
            (1.00, 190,   0,   0),   // dark red
        };
        return LerpStops(stops, n);
    }
}
