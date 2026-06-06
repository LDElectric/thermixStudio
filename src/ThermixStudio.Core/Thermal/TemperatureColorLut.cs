using System.Diagnostics;

namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Máscara de overlay FLIR: posições fixas dos elementos de UI.
/// </summary>
public record OverlayMask(
    int TopRows,
    int BottomRows,
    int LeftColsTop,
    int LogoRows,
    int LogoCols,
    int RightCols,
    int CrosshairThickness,  // espessura das linhas do crosshair
    int CrosshairLength,     // comprimento do braço (metade)
    int CrosshairRadius)     // raio do círculo central
{
    public bool IsOverlay(int x, int y, int w, int h)
    {
        int cx = w / 2, cy = h / 2;

        // Barras fixas
        if (y < TopRows || y >= h - BottomRows) return true;
        if (x < LeftColsTop && y < TopRows + 20) return true;  // leitura temp
        if (x < LogoCols && y >= h - LogoRows) return true;    // logo
        if (x >= w - RightCols) return true;                    // escala

        // Crosshair: linha horizontal
        if (Math.Abs(y - cy) <= CrosshairThickness / 2 &&
            Math.Abs(x - cx) <= CrosshairLength) return true;
        // Crosshair: linha vertical
        if (Math.Abs(x - cx) <= CrosshairThickness / 2 &&
            Math.Abs(y - cy) <= CrosshairLength) return true;
        // Crosshair: círculo central
        int dx = x - cx, dy = y - cy;
        if (dx * dx + dy * dy <= CrosshairRadius * CrosshairRadius) return true;

        return false;
    }

    /// <summary>Máscara FLIR E8xt (320×240): logo inf-esq, temp sup-esq, escala dir, crosshair.</summary>
    public static OverlayMask FlirE8xt => new(
        TopRows: 22,
        BottomRows: 38,
        LeftColsTop: 60,
        LogoRows: 38,
        LogoCols: 45,
        RightCols: 32,
        CrosshairThickness: 4,
        CrosshairLength: 35,
        CrosshairRadius: 8);
}

/// <summary>
/// LUT temperatura→cor calibrada pelo JPEG com exclusão de overlay.
/// Usa MEDIANA por bin para rejeitar outliers (overlay residual).
/// </summary>
public sealed class TemperatureColorLut
{
    private readonly byte[] _lutR, _lutG, _lutB;
    private readonly double _minC, _maxC, _range;
    private readonly int _numBins;
    private readonly double _dataMinC, _dataMaxC; // range com dados válidos (display)
    private bool _isValid;

    private TemperatureColorLut(int numBins, double minC, double maxC,
        double dataMinC, double dataMaxC)
    {
        _numBins = numBins;
        _minC = minC;
        _maxC = maxC;
        _range = maxC - minC;
        _dataMinC = dataMinC;
        _dataMaxC = dataMaxC;
        _lutR = new byte[numBins];
        _lutG = new byte[numBins];
        _lutB = new byte[numBins];
    }

    /// <summary>
    /// Constrói LUT amostrando pixels LIMPOS do JPEG (fora do overlay).
    /// Usa MEDIANA por bin para rejeitar outliers estatisticamente.
    /// </summary>
        /// <summary>
    /// Constrói LUT de 256 cores extraídas do JPEG (área limpa, sem overlay).
    /// A paleta é fixa — o mapeamento temperatura->cor usa o slider range.
    /// Comportamento igual ao FLIR Tools: sliders redistribuem as cores.
    /// </summary>
    public static TemperatureColorLut BuildPalette(
        double[,] temperatures, byte[] jpegBgra,
        int width, int height,
        OverlayMask? mask = null)
    {
        mask ??= OverlayMask.FlirE8xt;
        int w = width, h = height;
        var lut = new TemperatureColorLut(256, 0.0, 1.0, 0.0, 1.0);

        var samples = new List<(double temp, byte r, byte g, byte b)>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (mask.IsOverlay(x, y, w, h)) continue;
                double temp = temperatures[y, x];
                if (!double.IsFinite(temp)) continue;
                int idx = (y * w + x) * 4;
                samples.Add((temp, jpegBgra[idx + 2], jpegBgra[idx + 1], jpegBgra[idx]));
            }
        }

        if (samples.Count < 256) { lut._isValid = true; return lut; }

        samples.Sort((a, b) => a.temp.CompareTo(b.temp));
        int n = samples.Count;
        for (int i = 0; i < 256; i++)
        {
            int idx = (int)((double)i / 255 * (n - 1));
            lut._lutR[i] = samples[idx].r;
            lut._lutG[i] = samples[idx].g;
            lut._lutB[i] = samples[idx].b;
        }

        SmoothLut(lut._lutR, 256, 1);
        SmoothLut(lut._lutG, 256, 1);
        SmoothLut(lut._lutB, 256, 1);

        System.Diagnostics.Debug.WriteLine("[PALETTE-LUT] 256 cores extraidas do JPEG (area limpa)");
        lut._isValid = true;
        return lut;
    }

    public static TemperatureColorLut Build(
        double[,] temperatures, byte[] jpegBgra,
        int width, int height,
        double minC, double maxC,
        OverlayMask? mask = null,
        int numBins = 4096,
        double? samplingMinC = null,
        double? samplingMaxC = null)
    {
        mask ??= OverlayMask.FlirE8xt;
        int w = width, h = height;

        if (maxC <= minC) maxC = minC + 0.01;
        double dataMinC = samplingMinC ?? minC;
        double dataMaxC = samplingMaxC ?? maxC;
        if (dataMaxC <= dataMinC) dataMaxC = dataMinC + 0.01;
        var lut = new TemperatureColorLut(numBins, minC, maxC, dataMinC, dataMaxC);

        // Coleta: lista de cores por bin
        var sumR = new long[numBins];
        var sumG = new long[numBins];
        var sumB = new long[numBins];
        var count = new int[numBins];

        int skipped = 0, total = 0, clipped = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (mask.IsOverlay(x, y, w, h)) { skipped++; continue; }

                double t = temperatures[y, x];
                if (!double.IsFinite(t)) continue;
                total++;

                // Filtrar: só amostrar pixels DENTRO do range de display
                // Fora desse range o JPEG tem preto/branco (clip da câmera)
                if (samplingMinC.HasValue && t < samplingMinC.Value) { clipped++; continue; }
                if (samplingMaxC.HasValue && t > samplingMaxC.Value) { clipped++; continue; }

                int bin = (int)((t - minC) / lut._range * (numBins - 1));
                if (bin < 0 || bin >= numBins) { clipped++; continue; }

                int idx = (y * w + x) * 4;
                sumR[bin] += jpegBgra[idx + 2];
                sumG[bin] += jpegBgra[idx + 1];
                sumB[bin] += jpegBgra[idx];
                count[bin]++;
            }
        }

        // Média por bin
        int populated = 0;
        for (int b = 0; b < numBins; b++)
        {
            if (count[b] > 0)
            {
                lut._lutR[b] = (byte)(sumR[b] / count[b]);
                lut._lutG[b] = (byte)(sumG[b] / count[b]);
                lut._lutB[b] = (byte)(sumB[b] / count[b]);
                populated++;
            }
        }

        // Preencher bins vazios com vizinho mais próximo
        // (estende cores das bordas para todo o range Planck)
        FillEmptyBinsNearest(lut._lutR, numBins);
        FillEmptyBinsNearest(lut._lutG, numBins);
        FillEmptyBinsNearest(lut._lutB, numBins);

        SmoothLut(lut._lutR, numBins, 1);
        SmoothLut(lut._lutG, numBins, 1);
        SmoothLut(lut._lutB, numBins, 1);

        Debug.WriteLine($"[CALIB-LUT] bins={numBins} fullRange={minC:F2}~{maxC:F2} dataRange={dataMinC:F2}~{dataMaxC:F2} populated={populated}/{numBins} ({100.0*populated/numBins:F1}%) skipped={skipped} sampled={total} clipped={clipped}");

        lut._isValid = true;
        return lut;
    }

    private static byte Median(List<byte> interleaved, int offset, int n)
    {
        var values = new byte[n];
        for (int i = 0; i < n; i++)
            values[i] = interleaved[i * 3 + offset];
        Array.Sort(values);
        return values[n / 2];
    }

    private static void SmoothLut(byte[] lut, int numBins, int radius)
    {
        var smoothed = new byte[numBins];
        Array.Copy(lut, smoothed, numBins);
        for (int b = 0; b < numBins; b++)
        {
            int sum = 0, w = 0;
            for (int r = -radius; r <= radius; r++)
            {
                int idx = Math.Clamp(b + r, 0, numBins - 1);
                int weight = radius + 1 - Math.Abs(r);
                sum += lut[idx] * weight;
                w += weight;
            }
            smoothed[b] = (byte)(sum / w);
        }
        Array.Copy(smoothed, lut, numBins);
    }

    private static void FillEmptyBinsNearest(byte[] lut, int numBins)
    {
        for (int b = 0; b < numBins; b++)
        {
            if (lut[b] != 0) continue; // bin has data (or filled by neighbor)
            int left = b - 1;
            while (left >= 0 && lut[left] == 0) left--;
            int right = b + 1;
            while (right < numBins && lut[right] == 0) right++;
            if (left >= 0 && right < numBins)
                lut[b] = (b - left) <= (right - b) ? lut[left] : lut[right];
            else if (left >= 0)
                lut[b] = lut[left];
            else if (right < numBins)
                lut[b] = lut[right];
        }
    }

    public void Apply(double[,] temperatures, byte[] bgraPixels, int width, int height,
        double sliderMin, double sliderMax)
    {
        if (!_isValid) return;
        if (sliderMax <= sliderMin) sliderMax = sliderMin + 0.01;
        double sliderRange = sliderMax - sliderMin;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = temperatures[y, x];
                int dest = (y * width + x) * 4;
                if (!double.IsFinite(t)) continue;

                // Fora da escala → preto (≤min) ou branco (≥max)
                // Igual à barra de escala do FLIR original
                if (t <= sliderMin)
                {
                    bgraPixels[dest] = bgraPixels[dest + 1] = bgraPixels[dest + 2] = 0;
                    continue;
                }
                if (t >= sliderMax)
                {
                    bgraPixels[dest] = bgraPixels[dest + 1] = bgraPixels[dest + 2] = 255;
                    continue;
                }

                double normalized = (t - sliderMin) / sliderRange;
                double pos = normalized * (_numBins - 1);
                int lo = Math.Clamp((int)pos, 0, _numBins - 1);
                int hi = Math.Clamp(lo + 1, 0, _numBins - 1);
                double frac = pos - lo;

                bgraPixels[dest]     = LerpByte(_lutB[lo], _lutB[hi], frac);
                bgraPixels[dest + 1] = LerpByte(_lutG[lo], _lutG[hi], frac);
                bgraPixels[dest + 2] = LerpByte(_lutR[lo], _lutR[hi], frac);
            }
        }
    }

    /// <summary>
    /// Extrai uma paleta normalizada de N cores a partir da LUT temperatura→cor.
    /// As cores são amostradas uniformemente no range de display original.
    /// Esta paleta contém o DDE/stretch da câmera embutido nas cores.
    /// </summary>
    public byte[] ToNormalizedPalette(int numColors = 256)
    {
        var palette = new byte[numColors * 3]; // RGB interleaved (R,G,B,R,G,B,...)
        for (int i = 0; i < numColors; i++)
        {
            double pos = (double)i / (numColors - 1) * (_numBins - 1);
            int lo = Math.Clamp((int)pos, 0, _numBins - 1);
            int hi = Math.Clamp(lo + 1, 0, _numBins - 1);
            double frac = pos - lo;
            palette[i * 3 + 0] = LerpByte(_lutR[lo], _lutR[hi], frac);
            palette[i * 3 + 1] = LerpByte(_lutG[lo], _lutG[hi], frac);
            palette[i * 3 + 2] = LerpByte(_lutB[lo], _lutB[hi], frac);
        }
        return palette;
    }

    /// <summary>
    /// Aplica paleta normalizada com range DINÂMICO (sliders).
    /// Diferente de Apply(), que usa o range original do display.
    /// Aqui o sliderMin/sliderMax controlam o mapeamento temp→cor.
    /// </summary>
    public static void ApplyNormalized(
        double[,] temperatures, byte[] bgraPixels,
        int width, int height,
        double sliderMin, double sliderMax,
        byte[] normalizedPalette, int numColors = 256)
    {
        double sliderRange = sliderMax - sliderMin;
        if (sliderRange <= 0) sliderRange = 0.01;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = temperatures[y, x];
                int dest = (y * width + x) * 4;
                if (!double.IsFinite(t)) continue;

                double normalized = (t - sliderMin) / sliderRange;

                // Fora da escala → preto (≤min) ou branco (≥max)
                if (normalized <= 0)
                {
                    bgraPixels[dest] = bgraPixels[dest + 1] = bgraPixels[dest + 2] = 0;
                    continue;
                }
                if (normalized >= 1)
                {
                    bgraPixels[dest] = bgraPixels[dest + 1] = bgraPixels[dest + 2] = 255;
                    continue;
                }

                double pos = normalized * (numColors - 1);
                int lo = Math.Clamp((int)pos, 0, numColors - 1);
                int hi = Math.Clamp(lo + 1, 0, numColors - 1);
                double frac = pos - lo;

                // normalizedPalette: RGB interleaved → BGRA output
                bgraPixels[dest]     = LerpByte(normalizedPalette[lo * 3 + 2], normalizedPalette[hi * 3 + 2], frac);
                bgraPixels[dest + 1] = LerpByte(normalizedPalette[lo * 3 + 1], normalizedPalette[hi * 3 + 1], frac);
                bgraPixels[dest + 2] = LerpByte(normalizedPalette[lo * 3 + 0], normalizedPalette[hi * 3 + 0], frac);
            }
        }
    }

    private static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
}
