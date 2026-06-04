using System.Diagnostics;

namespace ThermixStudio.Core.Thermal;

/// <summary>
/// LUT temperatura→cor: amostra cores REAIS do JPEG original da câmera,
/// filtra overlay (preto/branco exato + crosshair), interpola bins vazios
/// com vizinho mais próximo. Sem ForceExtremeColors — transições suaves.
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

    public static TemperatureColorLut Build(
        double[,] temperatures, byte[] jpegBgra,
        int width, int height,
        double minC, double maxC,
        int numBins = 4096, double cropMargin = 0.10)
    {
        int x0 = (int)(width * cropMargin);
        int x1 = (int)(width * (1.0 - cropMargin));
        int y0 = (int)(height * cropMargin);
        int y1 = (int)(height * (1.0 - cropMargin));
        int cx = width / 2, cy = height / 2;

        if (maxC <= minC) maxC = minC + 0.01;
        var lut = new TemperatureColorLut(numBins, minC, maxC);

        var sumR = new long[numBins];
        var sumG = new long[numBins];
        var sumB = new long[numBins];
        var count = new int[numBins];
        int skipped = 0;

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                double t = temperatures[y, x];
                if (!double.IsFinite(t)) continue;
                int bin = (int)((t - minC) / lut._range * (numBins - 1));
                if (bin < 0 || bin >= numBins) continue;

                int idx = (y * width + x) * 4;
                byte jr = jpegBgra[idx + 2], jg = jpegBgra[idx + 1], jb = jpegBgra[idx];

                // Filtrar overlay: preto/branco EXATO + crosshair central
                bool pureBlack = jr == 0 && jg == 0 && jb == 0;
                bool pureWhite = jr == 255 && jg == 255 && jb == 255;
                bool crosshair = Math.Abs(x - cx) <= 2 && Math.Abs(y - cy) <= 2;
                if (pureBlack || pureWhite || crosshair) { skipped++; continue; }

                sumR[bin] += jr;
                sumG[bin] += jg;
                sumB[bin] += jb;
                count[bin]++;
            }
        }

        // Médias + hasData
        var hasData = new bool[numBins];
        int populated = 0, firstPop = -1, lastPop = -1;
        for (int b = 0; b < numBins; b++)
        {
            if (count[b] > 0)
            {
                lut._lutR[b] = (byte)(sumR[b] / count[b]);
                lut._lutG[b] = (byte)(sumG[b] / count[b]);
                lut._lutB[b] = (byte)(sumB[b] / count[b]);
                hasData[b] = true;
                populated++;
                if (firstPop < 0) firstPop = b;
                lastPop = b;
            }
        }

        Debug.WriteLine($"[TEMP-LUT] bins={numBins} range={minC:F2}~{maxC:F2} populated={populated}/{numBins} ({100.0*populated/numBins:F1}%) skippedOverlay={skipped}");
        if (firstPop >= 0)
            Debug.WriteLine($"[TEMP-LUT] firstPop@={minC + firstPop*lut._range/numBins:F2}°C lastPop@={minC + lastPop*lut._range/numBins:F2}°C");

        // Vizinho mais próximo (não interpolação linear — evita cores falsas)
        FillEmptyBinsNearest(lut._lutR, numBins, hasData);
        FillEmptyBinsNearest(lut._lutG, numBins, hasData);
        FillEmptyBinsNearest(lut._lutB, numBins, hasData);

        // SEM ForceExtremeColors — transições suaves nos limiares

        lut._isValid = true;
        return lut;
    }

    private static void FillEmptyBinsNearest(byte[] lut, int numBins, bool[] hasData)
    {
        for (int b = 0; b < numBins; b++)
        {
            if (hasData[b]) continue;
            int left = b - 1;
            while (left >= 0 && !hasData[left]) left--;
            int right = b + 1;
            while (right < numBins && !hasData[right]) right++;
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
                if (t < -50.0) continue;

                double pos = Math.Clamp((t - displayMin) / displayRange, 0.0, 1.0) * (_numBins - 1);
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
