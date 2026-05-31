namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Utilitários compartilhados de cor FLIR (YCbCr, limit colors, detecção de marca).
/// Consolidado de ThermalRenderEngine, ThermalPaletteEngine e VisualScaleDetector.
/// </summary>
public static class FlirColorUtils
{
    public static bool IsFlir(RadiometricMetadata metadata)
        => metadata.Detector.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
           metadata.CameraModel.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
           metadata.Manufacturer.Contains("FLIR", StringComparison.OrdinalIgnoreCase);

    public static bool UsesFlirLimitColors(RadiometricMetadata? metadata)
    {
        if (metadata is null)
        {
            return false;
        }

        return metadata.PaletteBelowColorYCrCb is not null ||
            metadata.PaletteAboveColorYCrCb is not null ||
            IsFlir(metadata);
    }

    public static (byte R, byte G, byte B) ResolveYCrCbLimitColor(int[]? yCrCb, int fallbackY)
    {
        var y = Math.Clamp(yCrCb is { Length: >= 1 } ? yCrCb[0] : fallbackY, 0, 255);
        var cr = Math.Clamp(yCrCb is { Length: >= 2 } ? yCrCb[1] : 128, 0, 255);
        var cb = Math.Clamp(yCrCb is { Length: >= 3 } ? yCrCb[2] : 128, 0, 255);

        var r = Math.Clamp(y + (1.402 * (cr - 128)), 0, 255);
        var g = Math.Clamp(y - (0.344 * (cb - 128)) - (0.714 * (cr - 128)), 0, 255);
        var b = Math.Clamp(y + (1.772 * (cb - 128)), 0, 255);

        return ((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
    }

    public static void WriteLimitColor((byte R, byte G, byte B) color, byte[] pixels, int dest)
    {
        pixels[dest] = color.B;
        pixels[dest + 1] = color.G;
        pixels[dest + 2] = color.R;
        pixels[dest + 3] = 255;
    }

    public static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);

    public static byte LerpByte(int a, int b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);

    public static (byte r, byte g, byte b) MapTemperatureToEmbeddedPalette(
        double tempC,
        double minC,
        double range,
        byte[] paletteBgra,
        (byte r, byte g, byte b) below,
        (byte r, byte g, byte b) above)
    {
        if (tempC < minC)
        {
            return below;
        }

        if (tempC > minC + range)
        {
            return above;
        }

        var normalized = Math.Clamp((tempC - minC) / range, 0.0, 1.0);
        var pos = normalized * 255.0;
        var lo = Math.Clamp((int)Math.Floor(pos), 0, 255);
        var hi = Math.Clamp(lo + 1, 0, 255);
        var f = pos - lo;
        var loIdx = lo * 4;
        var hiIdx = hi * 4;

        return (
            LerpByte(paletteBgra[loIdx + 2], paletteBgra[hiIdx + 2], f),
            LerpByte(paletteBgra[loIdx + 1], paletteBgra[hiIdx + 1], f),
            LerpByte(paletteBgra[loIdx], paletteBgra[hiIdx], f));
    }
}
