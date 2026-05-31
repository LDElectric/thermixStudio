namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Conversão de paletas FLIR embutidas (YCbCr) para LUT BGRA 256×4.
/// </summary>
internal static class FlirPaletteConverter
{
    public static byte[]? ConvertEmbeddedPaletteToBgraLut(byte[]? paletteRaw)
    {
        if (paletteRaw is null || paletteRaw.Length < 3)
        {
            return null;
        }

        if (paletteRaw.Length == 256 * 4)
        {
            return paletteRaw;
        }

        if (paletteRaw.Length % 3 != 0)
        {
            return null;
        }

        var colorCount = paletteRaw.Length / 3;
        if (colorCount < 16)
        {
            return null;
        }

        var bgra = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var srcIndex = (int)Math.Round(i * (colorCount - 1) / 255.0);
            var baseSrc = srcIndex * 3;

            int y = paletteRaw[baseSrc];
            int cr = paletteRaw[baseSrc + 1];
            int cb = paletteRaw[baseSrc + 2];
            var r = Math.Clamp((int)(y + 1.402 * (cr - 128)), 0, 255);
            var g = Math.Clamp((int)(y - 0.344 * (cb - 128) - 0.714 * (cr - 128)), 0, 255);
            var b = Math.Clamp((int)(y + 1.772 * (cb - 128)), 0, 255);

            var dst = i * 4;
            bgra[dst] = (byte)b;
            bgra[dst + 1] = (byte)g;
            bgra[dst + 2] = (byte)r;
            bgra[dst + 3] = 255;
        }

        return bgra;
    }
}
