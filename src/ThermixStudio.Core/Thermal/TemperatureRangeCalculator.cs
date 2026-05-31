namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Cálculo de faixa de temperatura a partir de matrizes radiométricas.
/// Consolidado de ThermalRenderEngine, VisualScaleDetector e ThermalPaletteEngine.
/// </summary>
public static class TemperatureRangeCalculator
{
    public static (double min, double max) GetRange(ThermalImageData image)
        => GetRange(image.Temperatures, image.Width, image.Height);

    public static (double min, double max) GetRange(double[,] temperatures, int width, int height)
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = temperatures[y, x];
                if (!double.IsFinite(t))
                {
                    continue;
                }

                if (t < min)
                {
                    min = t;
                }

                if (t > max)
                {
                    max = t;
                }
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
        {
            return (0, 1);
        }

        return (min, max);
    }
}
