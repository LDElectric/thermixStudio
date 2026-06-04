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
    private bool _isValid;

    private TemperatureColorLut(int numBins, double minC, double maxC)
    {
        _numBins = numBins;
        _minC = minC;
        _maxC = maxC;
        _range = maxC - minC;
        _lutR = new byte[numBins];
        _lutG = new byte[numBins];
        _lutB = new byte[numBins];
    }

    /// <summary>
    /// Constrói LUT amostrando pixels LIMPOS do JPEG (fora do overlay).
    /// Usa MEDIANA por bin para rejeitar outliers estatisticamente.
    /// </summary>
    public static TemperatureColorLut Build(
        double[,] temperatures, byte[] jpegBgra,
        int width, int height,
        double minC, double maxC,
        OverlayMask? mask = null,
        int numBins = 4096)
    {
        mask ??= OverlayMask.FlirE8xt;
        int w = width, h = height;

        if (maxC <= minC) maxC = minC + 0.01;
        var lut = new TemperatureColorLut(numBins, minC, maxC);

        // Coleta: lista de cores por bin
        var sumR = new long[numBins];
        var sumG = new long[numBins];
        var sumB = new long[numBins];
        var count = new int[numBins];

        int skipped = 0, total = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (mask.IsOverlay(x, y, w, h)) { skipped++; continue; }

                double t = temperatures[y, x];
                if (!double.IsFinite(t)) continue;
                total++;

                int bin = (int)((t - minC) / lut._range * (numBins - 1));
                if (bin < 0 || bin >= numBins) continue;

                int idx = (y * w + x) * 4;
                sumR[bin] += jpegBgra[idx + 2];
                sumG[bin] += jpegBgra[idx + 1];
                sumB[bin] += jpegBgra[idx];
                count[bin]++;
            }
        }

        // Média por bin (mais fiel que mediana para cores térmicas)
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
        FillEmptyBinsNearest(lut._lutR, numBins);
        FillEmptyBinsNearest(lut._lutG, numBins);
        FillEmptyBinsNearest(lut._lutB, numBins);

        SmoothLut(lut._lutR, numBins, 1);
        SmoothLut(lut._lutG, numBins, 1);
        SmoothLut(lut._lutB, numBins, 1);

        Debug.WriteLine($"[CALIB-LUT] bins={numBins} range={minC:F2}~{maxC:F2} populated={populated}/{numBins} ({100.0*populated/numBins:F1}%) skipped={skipped} sampled={total}");

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
        double displayMin, double displayMax)
    {
        if (!_isValid) return;
        if (displayMax <= displayMin) displayMax = displayMin + 0.01;
        double displayRange = displayMax - displayMin;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = temperatures[y, x];
                int dest = (y * width + x) * 4;
                if (!double.IsFinite(t)) continue;

                // Fora da escala → preto (abaixo) ou branco (acima)
                if (t < displayMin) { bgraPixels[dest] = bgraPixels[dest + 1] = bgraPixels[dest + 2] = 0; continue; }
                if (t > displayMax) { bgraPixels[dest] = bgraPixels[dest + 1] = bgraPixels[dest + 2] = 255; continue; }

                double pos = (t - displayMin) / displayRange * (_numBins - 1);
                int lo = Math.Clamp((int)pos, 0, _numBins - 1);
                int hi = Math.Clamp(lo + 1, 0, _numBins - 1);
                double frac = pos - lo;

                bgraPixels[dest]     = LerpByte(_lutB[lo], _lutB[hi], frac);
                bgraPixels[dest + 1] = LerpByte(_lutG[lo], _lutG[hi], frac);
                bgraPixels[dest + 2] = LerpByte(_lutR[lo], _lutR[hi], frac);
            }
        }
    }

    private static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
}
