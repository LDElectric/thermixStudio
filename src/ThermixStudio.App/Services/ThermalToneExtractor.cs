using System.Diagnostics;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services;

/// <summary>
/// Extrai o ThermalToneProfile (Curve256) do JPEG original da câmera
/// via reverse-lookup: correlaciona cores do JPEG com a paleta embarcada
/// para inferir a curva tonal que a câmera aplicou.
///
/// Algoritmo: 10 passos conforme CHANGELOG_Refatoracao_3_Camadas.md
/// </summary>
internal static class ThermalToneExtractor
{
    private const float MaxColorDistance = 15.0f;
    private const int SampleStep = 2;
    private const int MinValidSamples = 100;
    private const int CurveSize = 256;

    /// <summary>
    /// Extrai o perfil tonal do JPEG. Retorna null se a extração falhar
    /// (fallback seguro: pipeline opera como identidade).
    /// 
    /// A normalização usa o espaço de sinal Planck (mesmo do renderizador)
    /// para que a Curve256 extraída case perfeitamente com o pipeline de renderização.
    /// </summary>
    public static ThermalToneProfile? Extract(
        double[,] temperatures,
        byte[] jpegBgra,
        int width, int height,
        byte[] embeddedPaletteBgra,
        OverlayMask mask,
        double? planckR1 = null,
        double? planckR2 = null,
        double? planckB = null,
        double? planckF = null,
        double? planckO = null,
        double levelMinC = 0,
        double levelMaxC = 100)
    {
        if (embeddedPaletteBgra is not { Length: 256 * 4 })
        {
            Debug.WriteLine("[ToneExtractor] Paleta embarcada inválida ou ausente.");
            return null;
        }

        if (temperatures.GetLength(0) != height || temperatures.GetLength(1) != width)
        {
            Debug.WriteLine("[ToneExtractor] Dimensões da matriz não coincidem.");
            return null;
        }

        int w = width, h = height;

        // ── Normalização: mesma do renderizador (Planck signal-space) ──
        bool hasPlanck = planckR1 is > 0 && planckR2 is > 0 && planckB is > 0;
        if (levelMaxC <= levelMinC) levelMaxC = levelMinC + 0.01;

        double SignalFromTemp(double tempC)
        {
            if (!hasPlanck) return tempC;
            double tk = tempC + 273.15;
            if (tk <= 0) return 0;
            return planckR1!.Value / (planckR2!.Value * (Math.Exp(planckB!.Value / tk) - (planckF ?? 1.0))) - (planckO ?? 0);
        }

        double minVal = SignalFromTemp(levelMinC);
        double maxVal = SignalFromTemp(levelMaxC);
        if (minVal > maxVal) { var tmp = minVal; minVal = maxVal; maxVal = tmp; }
        double sigRange = maxVal - minVal;
        if (sigRange <= 0) sigRange = 0.01;

        // ── Passo 1: Cache de hash RGB → índice da paleta ──
        var paletteHash = new Dictionary<int, int>(256);
        for (int pi = 0; pi < 256; pi++)
        {
            int pOff = pi * 4;
            int hash = HashColor(
                embeddedPaletteBgra[pOff + 2],
                embeddedPaletteBgra[pOff + 1],
                embeddedPaletteBgra[pOff]);
            paletteHash[hash] = pi;
        }

        // ── Passo 3: Coleta ──
        var binAccum = new List<float>[CurveSize];
        for (int i = 0; i < CurveSize; i++)
            binAccum[i] = [];

        int totalSamples = 0;

        for (int y = 0; y < h; y += SampleStep)
        {
            for (int x = 0; x < w; x += SampleStep)
            {
                if (mask.IsOverlay(x, y, w, h)) continue;

                double t = temperatures[y, x];
                if (!double.IsFinite(t)) continue;

                // Normalização física (Planck signal-space, igual ao renderizador)
                double physicalNorm = Math.Clamp((SignalFromTemp(t) - minVal) / sigRange, 0.0, 1.0);

                int pxOff = (y * w + x) * 4;
                int jpegR = jpegBgra[pxOff + 2];
                int jpegG = jpegBgra[pxOff + 1];
                int jpegB = jpegBgra[pxOff];

                // ── Passos 3-4: Busca cor mais próxima + threshold ──
                int jpegHash = HashColor((byte)jpegR, (byte)jpegG, (byte)jpegB);

                // Tentar hit exato primeiro
                int? bestPalIdx = null;
                float bestDist = float.MaxValue;

                if (paletteHash.TryGetValue(jpegHash, out int exactIdx))
                {
                    bestPalIdx = exactIdx;
                    bestDist = 0;
                }
                else
                {
                    // Busca por vizinhança (step=4, depois refina)
                    for (int pi = 0; pi < 256; pi += 4)
                    {
                        int pOff = pi * 4;
                        float dr = jpegR - embeddedPaletteBgra[pOff + 2];
                        float dg = jpegG - embeddedPaletteBgra[pOff + 1];
                        float db = jpegB - embeddedPaletteBgra[pOff];
                        float dist = dr * dr + dg * dg + db * db;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestPalIdx = pi;
                        }
                    }

                    // Refinamento local
                    if (bestPalIdx.HasValue)
                    {
                        int start = Math.Max(0, bestPalIdx.Value - 4);
                        int end = Math.Min(255, bestPalIdx.Value + 4);
                        for (int pi = start; pi <= end; pi++)
                        {
                            int pOff = pi * 4;
                            float dr = jpegR - embeddedPaletteBgra[pOff + 2];
                            float dg = jpegG - embeddedPaletteBgra[pOff + 1];
                            float db = jpegB - embeddedPaletteBgra[pOff];
                            float dist = dr * dr + dg * dg + db * db;
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestPalIdx = pi;
                            }
                        }
                    }
                }

                if (bestDist > MaxColorDistance * MaxColorDistance || !bestPalIdx.HasValue)
                    continue;

                // Posição ajustada = posição na paleta (0..255) normalizada
                float adjustedNorm = bestPalIdx.Value / 255f;

                int bin = Math.Clamp((int)(physicalNorm * (CurveSize - 1) + 0.5f), 0, CurveSize - 1);
                binAccum[bin].Add(adjustedNorm);
                totalSamples++;
            }
        }

        Debug.WriteLine($"[ToneExtractor] valid={totalSamples >= MinValidSamples} samples={totalSamples} bins_preenchidos={binAccum.Count(b => b.Count > 0)}");

        if (totalSamples < MinValidSamples)
            return null;

        // ── Passo 6: Mediana por bin ──
        var curve = new float[CurveSize];
        for (int i = 0; i < CurveSize; i++)
        {
            if (binAccum[i].Count > 0)
            {
                var sorted = binAccum[i];
                sorted.Sort();
                curve[i] = sorted[sorted.Count / 2];
            }
            else
            {
                curve[i] = float.NaN;
            }
        }

        // ── Passo 7: Preenchimento de buracos (nearest-neighbor) ──
        // Forward fill
        float lastValid = 0f;
        for (int i = 0; i < CurveSize; i++)
        {
            if (!float.IsNaN(curve[i]))
                lastValid = curve[i];
            else
                curve[i] = lastValid;
        }
        // Backward fill: propaga o primeiro valor válido para os bins iniciais vazios
        // (evita que áreas frias com poucas amostras caiam em preto puro)
        for (int i = 0; i < CurveSize; i++)
        {
            if (curve[i] > 0f)
            {
                float firstValid = curve[i];
                for (int j = 0; j < i; j++)
                    curve[j] = firstValid;
                break;
            }
        }

        // ── Passo 8: Monotonicidade ──
        for (int i = 1; i < CurveSize; i++)
        {
            if (curve[i] < curve[i - 1])
                curve[i] = curve[i - 1];
        }

        // ── Passo 9: Suavização (moving average, janela 3) ──
        var smoothed = new float[CurveSize];
        Array.Copy(curve, smoothed, CurveSize);
        for (int i = 0; i < CurveSize; i++)
        {
            float sum = 0;
            int count = 0;
            for (int j = -1; j <= 1; j++)
            {
                int idx = Math.Clamp(i + j, 0, CurveSize - 1);
                sum += curve[idx];
                count++;
            }
            smoothed[i] = sum / count;
        }
        curve = smoothed;

        // ── Passo 10: Normalização de endpoints ──
        curve[0] = 0f;
        curve[CurveSize - 1] = 1f;

        int populatedBins = binAccum.Count(b => b.Count > 0);
        Debug.WriteLine($"[ToneExtractor] Curve256 preenchida: {populatedBins}/{CurveSize} bins, " +
                        $"range=[{curve[0]:F3}..{curve[255]:F3}], samples={totalSamples}");

        return new ThermalToneProfile(curve);
    }

    private static int HashColor(byte r, byte g, byte b)
        => (r << 16) | (g << 8) | b;
}
