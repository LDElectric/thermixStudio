namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Equalização por match de histograma: mapeia as cores do render (source)
/// para que tenham a mesma distribuição do original (reference).
/// 
/// Elimina a necessidade de calibrar stretch/whiteboost manualmente —
/// o algoritmo aprende a transformação diretamente da imagem original da câmera.
/// Funciona para qualquer termograma, qualquer faixa de temperatura, qualquer paleta.
/// </summary>
public static class HistogramMatcher
{
    /// <summary>
    /// Aplica match de histograma canal a canal (R, G, B).
    /// </summary>
    /// <param name="source">Pixels BGRA do render térmico</param>
    /// <param name="reference">Pixels BGRA do original da câmera</param>
    /// <param name="width">Largura em pixels</param>
    /// <param name="height">Altura em pixels</param>
    /// <param name="cropMargin">Margem (0.0-0.15) a excluir das bordas ao
    /// computar o histograma. Remove overlay da câmera (escala, logo).</param>
    /// <returns>Novo array BGRA com cores equalizadas ao original</returns>
    public static byte[] Match(byte[] source, byte[] reference, int width, int height, double cropMargin = 0.08)
    {
        var result = new byte[source.Length];
        Array.Copy(source, result, source.Length);

        // Calcular ROI (excluir bordas com overlay)
        int x0 = (int)(width * cropMargin);
        int x1 = (int)(width * (1.0 - cropMargin));
        int y0 = (int)(height * cropMargin);
        int y1 = (int)(height * (1.0 - cropMargin));

        // Para cada canal B, G, R (índices 0, 1, 2 no formato BGRA)
        for (int channel = 0; channel < 3; channel++)
        {
            MatchChannelCropped(result, reference, width, height, channel, x0, x1, y0, y1);
        }

        return result;
    }

    private static void MatchChannelCropped(byte[] source, byte[] reference, int width, int height,
        int channelOffset, int x0, int x1, int y0, int y1)
    {
        const int bins = 256;
        var histSrc = new int[bins];
        var histRef = new int[bins];
        int pixelCount = 0;

        // Construir histogramas APENAS da ROI (sem overlay)
        for (int y = y0; y < y1; y++)
        {
            int rowStart = y * width;
            for (int x = x0; x < x1; x++)
            {
                int idx = (rowStart + x) * 4 + channelOffset;
                histSrc[source[idx]]++;
                histRef[reference[idx]]++;
                pixelCount++;
            }
        }

        if (pixelCount == 0) return;

        // Calcular CDFs normalizadas
        var cdfSrc = new double[bins];
        var cdfRef = new double[bins];
        double accumSrc = 0, accumRef = 0;

        for (int v = 0; v < bins; v++)
        {
            accumSrc += (double)histSrc[v] / pixelCount;
            accumRef += (double)histRef[v] / pixelCount;
            cdfSrc[v] = accumSrc;
            cdfRef[v] = accumRef;
        }

        // Construir LUT de mapeamento
        var lut = new byte[bins];
        int refIdx = 0;

        for (int srcVal = 0; srcVal < bins; srcVal++)
        {
            while (refIdx < bins - 1 && cdfRef[refIdx] < cdfSrc[srcVal])
            {
                refIdx++;
            }

            if (refIdx > 0 && cdfRef[refIdx] > cdfSrc[srcVal])
            {
                double t = (cdfSrc[srcVal] - cdfRef[refIdx - 1]) /
                           (cdfRef[refIdx] - cdfRef[refIdx - 1] + 1e-12);
                lut[srcVal] = (byte)Math.Clamp(
                    (int)Math.Round(refIdx - 1 + t), 0, 255);
            }
            else
            {
                lut[srcVal] = (byte)refIdx;
            }
        }

        // Aplicar LUT em TODOS os pixels (não só ROI)
        int totalPixels = width * height;
        for (int i = 0; i < totalPixels; i++)
        {
            int idx = i * 4 + channelOffset;
            source[idx] = lut[source[idx]];
        }
    }

    /// <summary>
    /// Match adaptativo: corrige cores globalmente mas preserva bordas/detalhes
    /// do render original via blend com máscara de gradiente (Laplaciano).
    /// Áreas suaves recebem o match completo; bordas mantêm o render original.
    /// </summary>
    public static byte[] MatchAdaptive(byte[] source, byte[] reference, int width, int height, double cropMargin = 0.08)
    {
        var matched = Match(source, reference, width, height, cropMargin);
        var detailMask = ComputeDetailMask(source, width, height);

        for (int i = 0; i < source.Length; i += 4)
        {
            // w max = 0.3: preserva estrutura mas evita tons cinzas nas bordas
            float w = detailMask[i / 4] * 0.3f;
            for (int c = 0; c < 3; c++)
            {
                matched[i + c] = (byte)(matched[i + c] * (1.0f - w) + source[i + c] * w);
            }
        }
        return matched;
    }

    /// <summary>
    /// Constrói uma LUT de calibração por imagem comparando o render Linear
    /// com o JPEG original. A LUT captura stretch, whiteboost e curva de cor
    /// da câmera sem introduzir artefatos de borda.
    /// </summary>
    public static CalibratedLut BuildCalibratedLut(byte[] source, byte[] reference, int width, int height, double cropMargin = 0.08)
    {
        var lut = new CalibratedLut();

        int x0 = (int)(width * cropMargin);
        int x1 = (int)(width * (1.0 - cropMargin));
        int y0 = (int)(height * cropMargin);
        int y1 = (int)(height * (1.0 - cropMargin));

        // Para cada canal, construir CDFs e gerar LUT
        for (int ch = 0; ch < 3; ch++)
        {
            var histSrc = new int[256];
            var histRef = new int[256];
            int count = 0;

            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    int idx = (row + x) * 4 + ch;
                    histSrc[source[idx]]++;
                    histRef[reference[idx]]++;
                    count++;
                }
            }

            if (count == 0) continue;

            var cdfSrc = new double[256];
            var cdfRef = new double[256];
            double aSrc = 0, aRef = 0;
            for (int v = 0; v < 256; v++)
            {
                aSrc += (double)histSrc[v] / count;
                aRef += (double)histRef[v] / count;
                cdfSrc[v] = aSrc;
                cdfRef[v] = aRef;
            }

            var channelLut = ch switch { 0 => lut.LutB, 1 => lut.LutG, _ => lut.LutR };
            int refIdx = 0;

            for (int srcVal = 0; srcVal < 256; srcVal++)
            {
                while (refIdx < 255 && cdfRef[refIdx] < cdfSrc[srcVal])
                    refIdx++;

                if (refIdx > 0 && cdfRef[refIdx] > cdfSrc[srcVal])
                {
                    double t = (cdfSrc[srcVal] - cdfRef[refIdx - 1]) /
                               (cdfRef[refIdx] - cdfRef[refIdx - 1] + 1e-12);
                    channelLut[srcVal] = (byte)Math.Clamp((int)Math.Round(refIdx - 1 + t), 0, 255);
                }
                else
                {
                    channelLut[srcVal] = (byte)refIdx;
                }
            }
        }

        lut.Validate();
        return lut;
    }

    private static float[] ComputeDetailMask(byte[] pixels, int width, int height)
    {
        var mask = new float[width * height];
        float maxGrad = 0;

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int c = (y * width + x) * 4;
                float lc = pixels[c] * 0.114f + pixels[c + 1] * 0.587f + pixels[c + 2] * 0.299f;
                float lt = pixels[c - width * 4] * 0.114f + pixels[c - width * 4 + 1] * 0.587f + pixels[c - width * 4 + 2] * 0.299f;
                float lb = pixels[c + width * 4] * 0.114f + pixels[c + width * 4 + 1] * 0.587f + pixels[c + width * 4 + 2] * 0.299f;
                float ll = pixels[c - 4] * 0.114f + pixels[c - 4 + 1] * 0.587f + pixels[c - 4 + 2] * 0.299f;
                float lr = pixels[c + 4] * 0.114f + pixels[c + 4 + 1] * 0.587f + pixels[c + 4 + 2] * 0.299f;

                float grad = Math.Abs(4 * lc - lt - lb - ll - lr);
                mask[y * width + x] = grad;
                if (grad > maxGrad) maxGrad = grad;
            }
        }

        if (maxGrad > 0)
        {
            for (int i = 0; i < mask.Length; i++)
                mask[i] = Math.Clamp(mask[i] / (maxGrad * 0.5f), 0f, 1f);
        }

        return mask;
    }
}
