namespace ThermixStudio.Core.Thermal;

/// <summary>
/// LUT universal: mapeia TEMPERATURA → COR diretamente.
/// Construída alinhando a matriz de temperaturas com o JPEG original da câmera.
/// Funciona para qualquer termograma, qualquer cena, qualquer paleta —
/// porque a temperatura é um dado físico, não uma cor intermediária.
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
    /// <param name="temperatures">Matriz de temperaturas em Celsius</param>
    /// <param name="originalBgra">Pixels BGRA do JPEG original</param>
    /// <param name="width">Largura</param>
    /// <param name="height">Altura</param>
    /// <param name="minC">Temperatura mínima da escala</param>
    /// <param name="maxC">Temperatura máxima da escala</param>
    /// <param name="numBins">Número de bins de temperatura (padrão: 256)</param>
    /// <param name="cropMargin">Margem a excluir (overlay)</param>
    public static TemperatureColorLut Build(
        double[,] temperatures, byte[] originalBgra,
        int width, int height,
        double minC, double maxC,
        int numBins = 256, double cropMargin = 0.08)
    {
        var lut = new TemperatureColorLut(numBins, minC, maxC);

        int x0 = (int)(width * cropMargin);
        int x1 = (int)(width * (1.0 - cropMargin));
        int y0 = (int)(height * cropMargin);
        int y1 = (int)(height * (1.0 - cropMargin));

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

                // Mapear temperatura para bin
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

        // Suavizar (média móvel de 3)
        SmoothLut(lut._lutR, numBins);
        SmoothLut(lut._lutG, numBins);
        SmoothLut(lut._lutB, numBins);

        lut._isValid = true;
        return lut;
    }

    /// <summary>Aplica a LUT nos pixels BGRA usando interpolação linear entre bins.</summary>
    public void Apply(double[,] temperatures, byte[] bgraPixels, int width, int height)
    {
        if (!_isValid) return;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = temperatures[y, x];
                int dest = (y * width + x) * 4;

                // Temperaturas fora da escala: manter cor original do render
                if (t < _minC || t > _maxC) continue;

                double pos = (t - _minC) / _range * (_numBins - 1);
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

    private static void SmoothLut(byte[] lut, int numBins)
    {
        var smoothed = new byte[numBins];
        Array.Copy(lut, smoothed, numBins);

        for (int b = 1; b < numBins - 1; b++)
        {
            int sum = lut[b - 1] + lut[b] * 2 + lut[b + 1];
            smoothed[b] = (byte)(sum / 4);
        }

        Array.Copy(smoothed, lut, numBins);
    }

    private static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
}
