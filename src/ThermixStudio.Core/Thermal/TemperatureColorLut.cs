namespace ThermixStudio.Core.Thermal;

/// <summary>
/// LUT universal: mapeia TEMPERATURA → COR diretamente.
/// Construída alinhando a matriz de temperaturas com o JPEG original.
/// 256 bins — densidade ideal para distribuições térmicas.
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
    /// Constrói a LUT alinhando cada pixel da matriz de temperaturas
    /// com o pixel correspondente do JPEG original da câmera.
    /// </summary>
    public static TemperatureColorLut Build(
        double[,] temperatures, byte[] originalBgra,
        int width, int height,
        double minC, double maxC,
        int numBins = 512, double cropMargin = 0.08, int smoothRadius = 2)
    {
        int x0 = (int)(width * cropMargin);
        int x1 = (int)(width * (1.0 - cropMargin));
        int y0 = (int)(height * cropMargin);
        int y1 = (int)(height * (1.0 - cropMargin));

        if (maxC <= minC) maxC = minC + 0.01;
        var lut = new TemperatureColorLut(numBins, minC, maxC);

        // Acumuladores por bin: soma R, G, B e contagem
        var sumR = new long[numBins];
        var sumG = new long[numBins];
        var sumB = new long[numBins];
        var count = new int[numBins];

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                double t = temperatures[y, x];
                if (double.IsNaN(t) || double.IsInfinity(t)) continue;
                int bin = (int)((t - minC) / lut._range * (numBins - 1));
                if (bin < 0 || bin >= numBins) continue;

                int idx = (y * width + x) * 4;
                sumB[bin] += originalBgra[idx];
                sumG[bin] += originalBgra[idx + 1];
                sumR[bin] += originalBgra[idx + 2];
                count[bin]++;
            }
        }

        // Calcular médias por bin
        for (int b = 0; b < numBins; b++)
        {
            if (count[b] > 0)
            {
                lut._lutR[b] = (byte)(sumR[b] / count[b]);
                lut._lutG[b] = (byte)(sumG[b] / count[b]);
                lut._lutB[b] = (byte)(sumB[b] / count[b]);
            }
        }

        // Interpolar bins vazios
        FillEmptyBins(lut._lutR, numBins);
        FillEmptyBins(lut._lutG, numBins);
        FillEmptyBins(lut._lutB, numBins);

        // Suavizar (média ponderada, raio configurável)
        SmoothLut(lut._lutR, numBins, smoothRadius);
        SmoothLut(lut._lutG, numBins, smoothRadius);
        SmoothLut(lut._lutB, numBins, smoothRadius);

        // Garantir que extremos são preto (bin 0) e branco (bin 255)
        ForceExtremeColors(lut._lutR, lut._lutG, lut._lutB, numBins);

        lut._isValid = true;
        return lut;
    }

    private static void ForceExtremeColors(byte[] r, byte[] g, byte[] b, int numBins)
    {
        // Bin 0 → preto (RGB 0,0,0)
        r[0] = g[0] = b[0] = 0;
        // Último bin → branco (RGB 255,255,255)
        r[numBins - 1] = g[numBins - 1] = b[numBins - 1] = 255;
    }

    /// <summary>
    /// Aplica a LUT. displayMin/displayMax definem a faixa visível (sliders).
    /// Abaixo de displayMin → 1ª cor da LUT. Acima de displayMax → última cor.
    /// </summary>
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

                // Normalizar dentro da faixa visível (sliders)
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

    private static void FillEmptyBins(byte[] lut, int numBins)
    {
        // Preencher bins vazios com interpolação linear entre vizinhos
        for (int b = 0; b < numBins; b++)
        {
            if (lut[b] != 0) continue;

            // Procurar vizinho à esquerda
            int left = b - 1;
            while (left >= 0 && lut[left] == 0) left--;

            // Procurar vizinho à direita
            int right = b + 1;
            while (right < numBins && lut[right] == 0) right++;

            if (left >= 0 && right < numBins)
            {
                double t = (double)(b - left) / (right - left);
                lut[b] = (byte)(lut[left] + (lut[right] - lut[left]) * t);
            }
            else if (left >= 0)
            {
                lut[b] = lut[left];
            }
            else if (right < numBins)
            {
                lut[b] = lut[right];
            }
        }
    }

    private static void SmoothLut(byte[] lut, int numBins, int radius = 2)
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

    private static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
}
