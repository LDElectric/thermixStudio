using ThermixStudio.Core;

namespace ThermixStudio.App.Services;

/// <summary>
/// Motor de renderização MINIMALISTA - suporta apenas:
/// 1. Paleta Original embarcada (extraída de metadados FLIR via EXIF)
/// 2. Fallback Iron para emergências
/// 
/// NOTA: Todas as outras paletas (Iron, Rainbow, Grayscale, Hotmetal, Arctic, Thermal, Jet, Hot, Cool)
/// são renderizadas pelo ThermalPaletteEngine que é derivado do ThermalCS.
/// </summary>
public sealed class ThermalRenderEngine : IThermalRenderEngine
{
    private byte[]? _embeddedPaletteBgra;  // 256 × 4 bytes (BGRA)

    /// <summary>
    /// Define a paleta FLIR Original embarcada (extraída de EXIF da câmera).
    /// Deve ser BGRA com exatamente 1024 bytes (256 cores × 4 canais).
    /// </summary>
    public void SetEmbeddedPalette(byte[]? paletteData)
    {
        if (paletteData is not null && paletteData.Length == 256 * 4)
            _embeddedPaletteBgra = paletteData;
        else
            _embeddedPaletteBgra = null;
    }

    /// <summary>
    /// Renderiza imagem térmica usando:
    /// - Paleta Original embarcada se disponível e selecionada
    /// - Fallback Iron simples para qualquer outra paleta (emergência apenas)
    /// 
    /// IMPORTANTE: Usar ThermalPaletteEngine para todas as outras paletas!
    /// </summary>
    public ThermalRenderResult Render(ThermalImageData image, ThermalRenderParameters parameters)
    {
        var width = image.Width;
        var height = image.Height;

        var (absoluteMin, absoluteMax) = GetAbsoluteRange(image);
        var appliedMin = parameters.AutoScale ? absoluteMin : parameters.LevelMinC ?? absoluteMin;
        var appliedMax = parameters.AutoScale ? absoluteMax : parameters.LevelMaxC ?? absoluteMax;

        if (appliedMax <= appliedMin)
            appliedMax = appliedMin + 0.01;

        var range = appliedMax - appliedMin;
        var pixels = new byte[width * height * 4];

        // Seleciona LUT baseado na paleta
        byte[] lut = SelectLut(parameters.Palette);
        var useLimitColors = UsesFlirLimitColors(image.Metadata);
        var belowColor = ResolveYCrCbLimitColor(image.Metadata.PaletteBelowColorYCrCb, fallbackY: 50);
        var aboveColor = ResolveYCrCbLimitColor(image.Metadata.PaletteAboveColorYCrCb, fallbackY: 170);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = image.Temperatures[y, x];
                var idx = ((y * width) + x) * 4;

                if (useLimitColors && t < appliedMin)
                {
                    WriteLimitColor(belowColor, pixels, idx);
                    continue;
                }

                if (useLimitColors && t > appliedMax)
                {
                    WriteLimitColor(aboveColor, pixels, idx);
                    continue;
                }

                var n = Math.Clamp((t - appliedMin) / range, 0.0, 1.0);
                var colorIdx = (int)(n * 255) * 4;

                pixels[idx]     = lut[colorIdx];
                pixels[idx + 1] = lut[colorIdx + 1];
                pixels[idx + 2] = lut[colorIdx + 2];
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

    private byte[] SelectLut(ThermalPalette palette)
    {
        // Se paleta Original foi solicitada e temos embedded, usa
        if (palette == ThermalPalette.Original && _embeddedPaletteBgra is not null)
            return _embeddedPaletteBgra;

        // Fallback: Iron genérico (simples - 256 cinzas de preto a branco)
        // Usado apenas em emergências quando PaletteEngine falhar
        var lut = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var idx = i * 4;
            lut[idx]     = (byte)i;     // B
            lut[idx + 1] = (byte)i;     // G
            lut[idx + 2] = (byte)i;     // R
            lut[idx + 3] = 255;         // A
        }
        return lut;
    }

    private static bool UsesFlirLimitColors(RadiometricMetadata metadata)
    {
        return metadata.PaletteBelowColorYCrCb is not null ||
            metadata.PaletteAboveColorYCrCb is not null ||
            metadata.Detector.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
            metadata.CameraModel.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
            metadata.Manufacturer.Contains("FLIR", StringComparison.OrdinalIgnoreCase);
    }

    private static (byte R, byte G, byte B) ResolveYCrCbLimitColor(int[]? yCrCb, int fallbackY)
    {
        var y = Math.Clamp(yCrCb is { Length: >= 1 } ? yCrCb[0] : fallbackY, 0, 255);
        var cr = Math.Clamp(yCrCb is { Length: >= 2 } ? yCrCb[1] : 128, 0, 255);
        var cb = Math.Clamp(yCrCb is { Length: >= 3 } ? yCrCb[2] : 128, 0, 255);

        var r = Math.Clamp(y + (1.402 * (cr - 128)), 0, 255);
        var g = Math.Clamp(y - (0.344 * (cb - 128)) - (0.714 * (cr - 128)), 0, 255);
        var b = Math.Clamp(y + (1.772 * (cb - 128)), 0, 255);

        return ((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
    }

    private static void WriteLimitColor((byte R, byte G, byte B) color, byte[] pixels, int dest)
    {
        pixels[dest] = color.B;
        pixels[dest + 1] = color.G;
        pixels[dest + 2] = color.R;
        pixels[dest + 3] = 255;
    }

    private static (double min, double max) GetAbsoluteRange(ThermalImageData image)
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                if (t < min) min = t;
                if (t > max) max = t;
            }

        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
            return (0, 1);

        return (min, max);
    }
}
