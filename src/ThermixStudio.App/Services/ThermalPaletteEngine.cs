using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services;

/// <summary>
/// Motor de renderização de paletas térmicas extraído de ThermalCS.
/// Implementa detecção automática de paleta e o algoritmo ProcessSmartHD
/// que remapeia paletas preservando elementos de UI da câmera.
/// </summary>
public sealed class ThermalPaletteEngine : IThermalPaletteEngine
{
    private readonly Dictionary<string, ThermalPaletteLutData> _lutCache = new();
    private bool _lutsLoaded;

    public async Task<ThermalPaletteLutData?> LoadLutAsync(string paletteName, CancellationToken cancellationToken = default)
    {
        if (!_lutsLoaded)
            await LoadAllLutsAsync(cancellationToken).ConfigureAwait(false);

        return _lutCache.TryGetValue(paletteName, out var lut) ? lut : null;
    }

    /// <summary>
    /// Obtém LUT do cache de forma síncrona (sem I/O). Thread-safe.
    /// Se as LUTs ainda não foram carregadas, retorna null.
    /// </summary>
    public ThermalPaletteLutData? GetCachedLut(string paletteName)
    {
        return _lutCache.TryGetValue(paletteName, out var lut) ? lut : null;
    }

    public async Task<string?> DetectPaletteAsync(Bitmap originalImage, CancellationToken cancellationToken = default)
    {
        if (!_lutsLoaded)
            await LoadAllLutsAsync(cancellationToken).ConfigureAwait(false);

        if (_lutCache.Count == 0)
            return "Iron"; // Fallback

        // Amostragem aleatória de 500 pixels
        var rand = new Random();
        var samples = new List<Color>();
        int w = originalImage.Width;
        int h = originalImage.Height;

        for (int i = 0; i < 500; i++)
        {
            int x = rand.Next(w / 4, 3 * w / 4);
            int y = rand.Next(h / 4, 3 * h / 4);
            samples.Add(originalImage.GetPixel(x, y));
        }

        // Encontra paleta com menor distância Euclidiana média
        string bestPalette = "Iron";
        double minDistance = double.MaxValue;

        foreach (var (palName, lut) in _lutCache)
        {
            double avgDist = samples.Average(sample =>
            {
                double minD = double.MaxValue;
                for (int j = 0; j < lut.Rgb.Count; j += 10)
                {
                    var rgb = lut.Rgb[j];
                    double dr = sample.R - rgb[0];
                    double dg = sample.G - rgb[1];
                    double db = sample.B - rgb[2];
                    double dist = Math.Sqrt(dr * dr + dg * dg + db * db);
                    if (dist < minD) minD = dist;
                }
                return minD;
            });

            if (avgDist < minDistance)
            {
                minDistance = avgDist;
                bestPalette = palName;
            }
        }

        return minDistance > 70 ? null : bestPalette;
    }

    public Bitmap ProcessSmartHD(
        Bitmap srcImg,
        string srcName,
        string targetName)
    {
        if (!_lutsLoaded)
        {
            var task = LoadAllLutsAsync(CancellationToken.None);
            task.Wait(5000);
        }

        if (!_lutCache.TryGetValue(srcName, out var srcLut))
            srcLut = _lutCache["Iron"];
        if (!_lutCache.TryGetValue(targetName, out var tgtLut))
            tgtLut = _lutCache["Iron"];

        int w = srcImg.Width;
        int h = srcImg.Height;
        var dstImg = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var sData = srcImg.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dData = dstImg.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        byte[] sBuf = new byte[sData.Stride * h];
        byte[] dBuf = new byte[dData.Stride * h];
        Marshal.Copy(sData.Scan0, sBuf, 0, sBuf.Length);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * sData.Stride) + (x * 4);
                byte b = sBuf[idx], g = sBuf[idx + 1], r = sBuf[idx + 2], a = sBuf[idx + 3];

                // Remapeamento de cor puro (sem preservação de UI aqui, 
                // pois ela deve ser feita no final do pipeline pelo ThermalModeEngine.OverlayCameraUI)
                
                // Find best match in source LUT
                int bestIdx = 0;
                int minDist = int.MaxValue;

                for (int k = 0; k < srcLut.Rgb.Count; k += 4)
                {
                    var col = srcLut.Rgb[k];
                    int dr = r - col[0];
                    int dg = g - col[1];
                    int db = b - col[2];
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < minDist) { minDist = dist; bestIdx = k; }
                }

                // Refine with neighbors
                int start = Math.Max(0, bestIdx - 4);
                int end = Math.Min(srcLut.Rgb.Count, bestIdx + 5);
                for (int k = start; k < end; k++)
                {
                    var col = srcLut.Rgb[k];
                    int dr = r - col[0];
                    int dg = g - col[1];
                    int db = b - col[2];
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < minDist) { minDist = dist; bestIdx = k; }
                }

                // Map to target palette via ratio
                float ratio = (float)bestIdx / (srcLut.Rgb.Count - 1);
                var nc = tgtLut.Rgb[(int)(ratio * (tgtLut.Rgb.Count - 1))];

                dBuf[idx] = (byte)nc[2];     // B
                dBuf[idx + 1] = (byte)nc[1]; // G
                dBuf[idx + 2] = (byte)nc[0]; // R
                dBuf[idx + 3] = 255;
            }
        }

        Marshal.Copy(dBuf, 0, dData.Scan0, dBuf.Length);
        srcImg.UnlockBits(sData);
        dstImg.UnlockBits(dData);

        return dstImg;
    }

    [Obsolete("Use RenderWithProfileAsync com RenderProfile.FromMetadata()")]
    public async Task<byte[]> RenderThermalWithPaletteAsync(
        double[,] temperatures,
        int width, int height,
        string paletteName,
        double? levelMinC = null,
        double? levelMaxC = null,
        RadiometricMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!_lutsLoaded)
            await LoadAllLutsAsync(cancellationToken).ConfigureAwait(false);

        var lut = TryBuildEmbeddedLut(paletteName, metadata) ??
                  await LoadLutAsync(paletteName, cancellationToken).ConfigureAwait(false)
                  ?? await LoadLutAsync("Iron", cancellationToken).ConfigureAwait(false);
        if (lut == null)
            throw new InvalidOperationException($"Paleta {paletteName} não encontrada");

        // Auto-scale
        double minT = levelMinC ?? temperatures.Cast<double>().Min();
        double maxT = levelMaxC ?? temperatures.Cast<double>().Max();

        if (maxT <= minT)
            maxT = minT + 0.01;

        double planckR1 = metadata?.PlanckR1 ?? 0;
        double planckR2 = metadata?.PlanckR2 ?? 0;
        double planckB = metadata?.PlanckB ?? 0;
        double planckF = metadata?.PlanckF ?? 0;
        double planckO = metadata?.PlanckO ?? 0;
        bool useSignal = planckR1 > 0 && planckR2 > 0 && planckB > 0 && planckF > 0 && metadata?.PlanckO != null;

        double SignalFromTemp(double tempC)
        {
            if (!useSignal) return tempC;
            double tk = tempC + 273.15;
            if (tk <= 0) return 0;
            return planckR1 / (planckR2 * (Math.Exp(planckB / tk) - planckF)) - planckO;
        }

        // Normalização: usar o range REAL dos sinais da cena (não o derivado do Planck),
        // para evitar distorção na distribuição das cores.
        var pixels = new byte[width * height * 4];
        var useLimitColors = false; // Below/Above/Under/Over são alarmes, NÃO escala visível
        var underflowColor = FlirColorUtils.ResolveYCrCbLimitColor(metadata?.PaletteUnderflowColorYCrCb, fallbackY: 41);
        var overflowColor = FlirColorUtils.ResolveYCrCbLimitColor(metadata?.PaletteOverflowColorYCrCb, fallbackY: 67);

        if (ShouldUseFlirEmbeddedDisplayMapping(paletteName, metadata))
        {
            return RenderFlirEmbeddedDisplay(
                temperatures,
                width,
                height,
                lut,
                minT,
                maxT,
                useLimitColors,
                underflowColor,
                overflowColor,
                metadata);
        }

        // Normalização: respeitar levelMinC/levelMaxC (escala do usuário),
        // com fallback para o range real da cena quando não definidos
        double minTForNorm = levelMinC ?? temperatures.Cast<double>().Min();
        double maxTForNorm = levelMaxC ?? temperatures.Cast<double>().Max();
        if (maxTForNorm <= minTForNorm) maxTForNorm = minTForNorm + 0.01;
        double minVal = SignalFromTemp(minTForNorm);
        double maxVal = SignalFromTemp(maxTForNorm);
        if (minVal > maxVal) { var tmp = minVal; minVal = maxVal; maxVal = tmp; }
        double range = maxVal - minVal;
        if (range <= 0) range = 0.01;

        // Stretch calibrado pelo metadata: PaletteStretch 0→0%, 1→25%, 2→50%
        double stretchBlend = (metadata?.PaletteStretch ?? 0) * 0.25;
        stretchBlend = Math.Clamp(stretchBlend, 0.0, 1.0);

        // Pipeline FLIR: linearNorm → ApplyFlirPaletteStretch → WhiteBoost → LUT
        double sensorMinC = metadata?.CameraTemperatureMinClip ?? -40.0;
        double sensorMaxC = metadata?.CameraTemperatureMaxClip ?? 280.0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = temperatures[y, x];
                double val = SignalFromTemp(t);
                int dest = (y * width + x) * 4;

                if (useLimitColors && t < sensorMinC)
                {
                    FlirColorUtils.WriteLimitColor(underflowColor, pixels, dest);
                    continue;
                }
                if (useLimitColors && t > sensorMaxC)
                {
                    FlirColorUtils.WriteLimitColor(overflowColor, pixels, dest);
                    continue;
                }

                double linearNorm = Math.Clamp((val - minVal) / range, 0.0, 1.0);

                // FLIR Palette Stretch (curva não-linear)
                double stretched = ApplyFlirPaletteStretch(linearNorm);
                double normalized = stretched * stretchBlend + linearNorm * (1.0 - stretchBlend);

                // White Boost (>94%)
                double whiteBoost = SmoothStep(0.94, 0.99, normalized);
                normalized = Math.Clamp(normalized + (whiteBoost * 0.015), 0.0, 1.0);

                WriteInterpolatedLutColor(lut, normalized, pixels, dest);
                pixels[dest + 3] = 255;
            }
        }
     return pixels;

     }

    private static bool ShouldUseFlirEmbeddedDisplayMapping(string paletteName, RadiometricMetadata? metadata)
    {
        if (metadata?.EmbeddedPaletteBgra is not { Length: 256 * 4 })
        {
            return false;
        }

        if (!FlirColorUtils.IsFlir(metadata))
        {
            return false;
        }

        var requestedOriginal = paletteName.Equals("Original", StringComparison.OrdinalIgnoreCase);
        var matchesMetadataName = !string.IsNullOrWhiteSpace(metadata.PaletteName) &&
            paletteName.Equals(metadata.PaletteName, StringComparison.OrdinalIgnoreCase);
        var matchesDetectedPalette = metadata.DetectedPalette.HasValue &&
            paletteName.Equals(metadata.DetectedPalette.Value.ToString(), StringComparison.OrdinalIgnoreCase);

        return requestedOriginal || matchesMetadataName || matchesDetectedPalette;
    }

    private static byte[] RenderFlirEmbeddedDisplay(
        double[,] temperatures,
        int width,
        int height,
        ThermalPaletteLutData lut,
        double minT,
        double maxT,
        bool useLimitColors,
        (byte R, byte G, byte B) underflowColor,
        (byte R, byte G, byte B) overflowColor,
        RadiometricMetadata? metadata)
    {
        var pixels = new byte[width * height * 4];
        var range = Math.Max(0.01, maxT - minT);
        var sensorMinC = metadata?.CameraTemperatureMinClip ?? -40.0;
        var sensorMaxC = metadata?.CameraTemperatureMaxClip ?? 280.0;

        // Stretch calibrado pelo metadata: PaletteStretch 0→0%, 1→25%, 2→50%
        double stretchBlend = (metadata?.PaletteStretch ?? 0) * 0.25;
        stretchBlend = Math.Clamp(stretchBlend, 0.0, 1.0);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var t = temperatures[y, x];
                var dest = (y * width + x) * 4;

                if (useLimitColors && t < sensorMinC)
                {
                    FlirColorUtils.WriteLimitColor(underflowColor, pixels, dest);
                    continue;
                }

                if (useLimitColors && t > sensorMaxC)
                {
                    FlirColorUtils.WriteLimitColor(overflowColor, pixels, dest);
                    continue;
                }

                var normalized = Math.Clamp((t - minT) / range, 0.0, 1.0);
                double stretched = ApplyFlirPaletteStretch(normalized);
                normalized = stretched * stretchBlend + normalized * (1.0 - stretchBlend);

                WriteInterpolatedLutColor(lut, normalized, pixels, dest);
                pixels[dest + 3] = 255;
            }
        }

        return pixels;
    }

    // ────────────────────────────────────────────────────────────
    //  Novo: pipeline controlado por RenderProfile (por imagem)
    //  Inspirado no Blackbody — cada termograma tem seu próprio
    //  perfil com stretch, whiteboost e planck OPCIONAIS.
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renderiza usando <see cref="RenderProfile"/> — pipeline controlado por imagem.
    /// </summary>
    public async Task<byte[]> RenderWithProfileAsync(
        double[,] temperatures,
        int width, int height,
        string paletteName,
        RenderProfile profile,
        RadiometricMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!_lutsLoaded)
            await LoadAllLutsAsync(cancellationToken).ConfigureAwait(false);

        var lut = TryBuildEmbeddedLut(paletteName, metadata) ??
                  await LoadLutAsync(paletteName, cancellationToken).ConfigureAwait(false)
                  ?? await LoadLutAsync("Iron", cancellationToken).ConfigureAwait(false);
        if (lut == null)
            throw new InvalidOperationException($"Paleta {paletteName} não encontrada");

        double minT = profile.LevelMinC;
        double maxT = profile.LevelMaxC;
        if (maxT <= minT) maxT = minT + 0.01;

        double SignalFromTemp(double tempC)
        {
            if (!profile.ApplyPlanckTransform || metadata == null) return tempC;
            double r1 = metadata.PlanckR1 ?? 0;
            double r2 = metadata.PlanckR2 ?? 0;
            double b  = metadata.PlanckB  ?? 0;
            double f  = metadata.PlanckF  ?? 0;
            double o  = metadata.PlanckO  ?? 0;
            if (r1 <= 0 || r2 <= 0 || b <= 0) return tempC;
            double tk = tempC + 273.15;
            if (tk <= 0) return 0;
            return r1 / (r2 * (Math.Exp(b / tk) - f)) - o;
        }

        // Embedded FLIR display — atalho mantido, mas profile-aware
        if (ShouldUseFlirEmbeddedDisplayMapping(paletteName, metadata))
        {
            return RenderFlirEmbeddedDisplayWithProfile(
                temperatures, width, height, lut, minT, maxT, profile, metadata);
        }

        // Caminho principal de renderização
        double minVal = SignalFromTemp(minT);
        double maxVal = SignalFromTemp(maxT);
        if (minVal > maxVal) { var tmp = minVal; minVal = maxVal; maxVal = tmp; }
        double range = maxVal - minVal;
        if (range <= 0) range = 0.01;

        // Pré-computa DDE: modifica RAW → RAW', depois converte a temperaturas
        // Hypothesis D: RAW → ApplyDdePlateau() → RAW' → Planck → Temp → pipeline normal
        double[,]? ddeTemperatures = null;
        if (profile.ApplyDde && profile.RawValues is not null && metadata != null
            && metadata.PlanckR1.HasValue && metadata.PlanckR2.HasValue
            && metadata.PlanckB.HasValue && metadata.PlanckF.HasValue
            && metadata.PlanckO.HasValue)
        {
            var rawPrime = ApplyDdePlateau(profile.RawValues, width, height,
                profile.DdePlateauPercent, profile.DdeGamma, profile.DdeKnee);
            ddeTemperatures = ConvertRawToTemperatures(rawPrime, width, height, metadata);
        }

        var pixels = new byte[width * height * 4];
        double sensorMinC = profile.SensorMinC ?? -40.0;
        double sensorMaxC = profile.SensorMaxC ?? 280.0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = ddeTemperatures is not null ? ddeTemperatures[y, x] : temperatures[y, x];
                double val = SignalFromTemp(t);
                int dest = (y * width + x) * 4;

                if (profile.UseLimitColors && t < sensorMinC)
                {
                    FlirColorUtils.WriteLimitColor(profile.UnderflowColor, pixels, dest);
                    continue;
                }
                if (profile.UseLimitColors && t > sensorMaxC)
                {
                    FlirColorUtils.WriteLimitColor(profile.OverflowColor, pixels, dest);
                    continue;
                }

                double normalized = Math.Clamp((val - minVal) / range, 0.0, 1.0);
                                // Priority 2: below/above PALETTE scale (EXIF PaletteScaleMinC/MaxC)
                // Doc FLIR: BelowColor para t < PaletteScaleMin, AboveColor para t > PaletteScaleMax
                // NÃO confundir com VisualScale (detectado da barra de UI na imagem)
                double belowThreshold = profile.PaletteScaleMinC ?? minT;
                double aboveThreshold = profile.PaletteScaleMaxC ?? maxT;
                if (profile.BelowColor.HasValue && t < belowThreshold)
                {
                    FlirColorUtils.WriteLimitColor(profile.BelowColor.Value, pixels, dest);
                    continue;
                }
                if (profile.AboveColor.HasValue && t > aboveThreshold)
                {
                    FlirColorUtils.WriteLimitColor(profile.AboveColor.Value, pixels, dest);
                    continue;
                }

                // ── Camada 2: Tone Mapping (curva tonal do JPEG da câmera) ──
                normalized = ApplyToneMapping(normalized, metadata?.ToneProfile);

                WriteInterpolatedLutColor(lut, normalized, pixels, dest);
                pixels[dest + 3] = 255;
            }
        }
        return pixels;
    }

    private static byte[] RenderFlirEmbeddedDisplayWithProfile(
        double[,] temperatures,
        int width, int height,
        ThermalPaletteLutData lut,
        double minT, double maxT,
        RenderProfile profile,
        RadiometricMetadata? metadata)
    {
        var pixels = new byte[width * height * 4];
        var sensorMinC = profile.SensorMinC ?? -40.0;
        var sensorMaxC = profile.SensorMaxC ?? 280.0;

        // ── Planck signal transform: camera maps SIGNAL→palette (non-linear), not °C→palette ──
        double SignalFromTemp(double tempC)
        {
            if (!profile.ApplyPlanckTransform || metadata == null) return tempC;
            double r1 = metadata.PlanckR1 ?? 0;
            double r2 = metadata.PlanckR2 ?? 0;
            double b  = metadata.PlanckB  ?? 0;
            double f  = metadata.PlanckF  ?? 0;
            double o  = metadata.PlanckO  ?? 0;
            if (r1 <= 0 || r2 <= 0 || b <= 0) return tempC;
            double tk = tempC + 273.15;
            if (tk <= 0) return 0;
            return r1 / (r2 * (Math.Exp(b / tk) - f)) - o;
        }

        double minVal = SignalFromTemp(minT);
        double maxVal = SignalFromTemp(maxT);
        if (minVal > maxVal) { var tmp = minVal; minVal = maxVal; maxVal = tmp; }
        double range = maxVal - minVal;
        if (range <= 0) range = 0.01;

        // ── DDE (pré-computa) ──
        double[,]? ddeTemperaturesEmb = null;
        if (profile.ApplyDde && profile.RawValues is not null && metadata != null
            && metadata.PlanckR1.HasValue && metadata.PlanckR2.HasValue
            && metadata.PlanckB.HasValue && metadata.PlanckF.HasValue
            && metadata.PlanckO.HasValue)
        {
            var rawPrimeEmb = ApplyDdePlateau(profile.RawValues, width, height,
                profile.DdePlateauPercent, profile.DdeGamma, profile.DdeKnee);
            ddeTemperaturesEmb = ConvertRawToTemperatures(rawPrimeEmb, width, height, metadata);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var t = ddeTemperaturesEmb is not null ? ddeTemperaturesEmb[y, x] : temperatures[y, x];
                var dest = (y * width + x) * 4;

                if (profile.UseLimitColors && t < sensorMinC)
                {
                    FlirColorUtils.WriteLimitColor(profile.UnderflowColor, pixels, dest);
                    continue;
                }
                if (profile.UseLimitColors && t > sensorMaxC)
                {
                    FlirColorUtils.WriteLimitColor(profile.OverflowColor, pixels, dest);
                    continue;
                }

                // ── Planck signal-space normalization (same as camera) ──
                double val = SignalFromTemp(t);
                var normalized = Math.Clamp((val - minVal) / range, 0.0, 1.0);

                // Priority 2: below/above PALETTE scale (EXIF PaletteScaleMinC/MaxC)
                double belowThreshold = profile.PaletteScaleMinC ?? minT;
                double aboveThreshold = profile.PaletteScaleMaxC ?? maxT;
                if (profile.BelowColor.HasValue && t < belowThreshold)
                {
                    FlirColorUtils.WriteLimitColor(profile.BelowColor.Value, pixels, dest);
                    continue;
                }
                if (profile.AboveColor.HasValue && t > aboveThreshold)
                {
                    FlirColorUtils.WriteLimitColor(profile.AboveColor.Value, pixels, dest);
                    continue;
                }

                // ── Camada 2: Tone Mapping (curva tonal do JPEG da câmera) ──
                normalized = ApplyToneMapping(normalized, metadata?.ToneProfile);

                WriteInterpolatedLutColor(lut, normalized, pixels, dest);
                pixels[dest + 3] = 255;
            }
        }
        return pixels;
    }

    private static double ApplyFlirPaletteStretch(double normalized)
    {
        ReadOnlySpan<double> source =
        [
            0.000, 0.038, 0.062, 0.088, 0.113, 0.138, 0.163, 0.188, 0.213, 0.237,
            0.288, 0.388, 0.488, 0.588, 0.688, 0.788, 0.863, 0.913, 1.000
        ];
        ReadOnlySpan<double> target =
        [
            0.000, 0.027, 0.058, 0.090, 0.148, 0.233, 0.314, 0.444, 0.511, 0.538,
            0.578, 0.641, 0.704, 0.762, 0.816, 0.865, 0.924, 0.951, 1.000
        ];

        normalized = Math.Clamp(normalized, 0.0, 1.0);
        for (var i = 0; i < source.Length - 1; i++)
        {
            if (normalized > source[i + 1])
            {
                continue;
            }

            var width = source[i + 1] - source[i];
            if (width <= 0)
            {
                return target[i];
            }

            var t = (normalized - source[i]) / width;
            t = t * t * (3.0 - (2.0 * t));
            return target[i] + ((target[i + 1] - target[i]) * t);
        }

        return 1.0;
    }

    /// <summary>
    /// Camada 2 — Tone Mapping: aplica a curva tonal extraída do JPEG da câmera.
    /// Se o ToneProfile for nulo ou inválido, opera como identidade (sem alteração).
    /// Esta camada replica o contraste/gamma que a câmera FLIR aplicou no JPEG.
    /// </summary>
    public static double ApplyToneMapping(double normalized, ThermalToneProfile? toneProfile)
    {
        if (toneProfile is not { IsValid: true })
            return normalized;

        return toneProfile.Map(normalized);
    }

    /// <summary>
    /// FLIR DDE (Digital Detail Enhancement): plateau equalization + two-zone curve.
    /// Opera sobre os valores RAW 16-bit (pré-Planck), igual à câmera FLIR.
    /// </summary>
    /// <summary>
    /// Aplica DDE (Digital Detail Enhancement) via Plateau Equalization
    /// nos valores RAW 14-bit, retornando RAW' (modificado) no mesmo domínio.
    /// Hypothesis D: RAW → RAW' → Planck → Temp → Level/Span → Palette.
    /// </summary>
    private static ushort[,] ApplyDdePlateau(
        ushort[,] rawValues,
        int width, int height,
        double plateauPercent = 2.0,
        double gamma = 0.85,
        double knee = 0.75)
    {
        const int bins = 256;
        var hist = new double[bins];
        ushort min = ushort.MaxValue, max = ushort.MinValue;

        // 1. Encontrar min/max e construir histograma
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var v = rawValues[y, x];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double range = max - min;
        if (range <= 0) range = 1;

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int bin = (int)((rawValues[y, x] - min) / range * (bins - 1));
            if (bin >= 0 && bin < bins) hist[bin]++;
        }

        // 2. Plateau: limitar cada bin a plateauPercent% do total
        double total = width * height;
        double clipLevel = total * plateauPercent / 100.0;
        double excess = 0;
        for (int i = 0; i < bins; i++)
        {
            if (hist[i] > clipLevel)
            {
                excess += hist[i] - clipLevel;
                hist[i] = clipLevel;
            }
        }
        if (excess > 0)
        {
            double add = excess / bins;
            for (int i = 0; i < bins; i++) hist[i] += add;
        }

        // 3. CDF normalizada
        var cdf = new double[bins];
        cdf[0] = hist[0];
        for (int i = 1; i < bins; i++) cdf[i] = cdf[i - 1] + hist[i];
        for (int i = 0; i < bins; i++) cdf[i] /= cdf[bins - 1];

        // 4. Mapear cada pixel via CDF + two-zone curve, de volta ao range RAW
        var result = new ushort[height, width];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int bin = (int)((rawValues[y, x] - min) / range * (bins - 1));
            bin = Math.Clamp(bin, 0, bins - 1);
            double eq = cdf[bin];

            // Two-zone curve: gamma para sombras, linear para highlights
            if (eq <= knee)
                eq = knee * Math.Pow(eq / knee, gamma);
            else
                eq = knee + (1.0 - knee) * (eq - knee) / (1.0 - knee);

            // Mapear de volta ao range RAW original (Hypothesis D: RAW → RAW')
            result[y, x] = (ushort)(min + eq * range + 0.5);
        }

        return result;
    }

    /// <summary>
    /// Converte RAW 14-bit → temperaturas (°C) via Planck (forward).
    /// Inclui correção de emissividade e temperatura refletida.
    /// </summary>
    private static double[,] ConvertRawToTemperatures(
        ushort[,] rawValues,
        int width, int height,
        RadiometricMetadata metadata)
    {
        var r1 = metadata.PlanckR1!.Value;
        var r2 = metadata.PlanckR2!.Value;
        var b  = metadata.PlanckB!.Value;
        var f  = metadata.PlanckF!.Value;
        var o  = metadata.PlanckO!.Value;
        var emissivity = Math.Clamp(metadata.Emissivity ?? 1.0, 0.01, 1.0);
        var trefl = metadata.ReflectedTemperatureC ?? 20.0;
        var treflK = trefl + 273.15;
        var rawRefl = r1 / (r2 * (Math.Exp(b / treflK) - f)) - o;

        var result = new double[height, width];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var raw = rawValues[y, x];
            var rawObj = (raw - (1.0 - emissivity) * rawRefl) / emissivity;
            var correctedRaw = Math.Max(1.0, rawObj);
            var denominator = r2 * (correctedRaw + o);
            var lnInput = (r1 / Math.Max(denominator, 0.000001)) + f;
            if (lnInput <= 0) lnInput = 1.000001;
            var tempK = b / Math.Log(lnInput);
            result[y, x] = tempK - 273.15;
        }
        return result;
    }

    private static double[] SmoothHistogram(double[] hist)
    {
        var result = new double[hist.Length];
        for (int i = 0; i < hist.Length; i++)
        {
            double sum = hist[i] * 6.0;
            double weight = 6.0;

            if (i > 0)
            {
                sum += hist[i - 1] * 4.0;
                weight += 4.0;
            }

            if (i + 1 < hist.Length)
            {
                sum += hist[i + 1] * 4.0;
                weight += 4.0;
            }

            if (i > 1)
            {
                sum += hist[i - 2];
                weight += 1.0;
            }

            if (i + 2 < hist.Length)
            {
                sum += hist[i + 2];
                weight += 1.0;
            }

            result[i] = sum / weight;
        }

        return result;
    }

    private static double InterpolateCdf(double[] cdf, double binPosition)
    {
        if (cdf.Length == 0)
            return 0;

        double clamped = Math.Clamp(binPosition, 0.0, cdf.Length - 1);
        int lo = (int)Math.Floor(clamped);
        int hi = Math.Min(lo + 1, cdf.Length - 1);
        double t = clamped - lo;
        return cdf[lo] + ((cdf[hi] - cdf[lo]) * t);
    }

    private static double PreserveWarmDetail(double normalized)
    {
        // faixa quente inicia mais cedo
        double warm1 = SmoothStep(0.56, 0.70, normalized);

        // faixa onde a FLIR preserva o amarelo escuro
        double warm2 = SmoothStep(0.70, 0.86, normalized);

        // liberar perto do branco real
        double whiteRelease = SmoothStep(0.90, 0.985, normalized);

        // Hold original — preserva laranjas/amarelos sem desbotar
        double hold =
            warm1 * 0.015 +
            warm2 * 0.020;

        hold *= (1.0 - whiteRelease);

        // compressão local da faixa quente
        double x = normalized;

        if (x > 0.68 && x < 0.90)
        {
            double t = (x - 0.68) / (0.22);

            // curva S leve
            t = t * t * (3.0 - 2.0 * t);

            x -= 0.008 * t;
        }

        return Math.Clamp(x - hold, 0.0, 1.0);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        if (edge1 <= edge0)
            return value >= edge1 ? 1.0 : 0.0;

        double t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static void WriteInterpolatedLutColor(ThermalPaletteLutData lut, double normalized, byte[] pixels, int dest)
    {
        if (lut.Rgb.Count == 0)
        {
            pixels[dest] = 0;
            pixels[dest + 1] = 0;
            pixels[dest + 2] = 0;
            return;
        }

        double pos = Math.Clamp(normalized, 0.0, 1.0) * (lut.Rgb.Count - 1);
        int lo = Math.Clamp((int)Math.Floor(pos), 0, lut.Rgb.Count - 1);
        int hi = Math.Clamp(lo + 1, 0, lut.Rgb.Count - 1);
        double t = pos - lo;

        var c0 = lut.Rgb[lo];
        var c1 = lut.Rgb[hi];

        pixels[dest]     = FlirColorUtils.LerpByte(c0[2], c1[2], t); // B
        pixels[dest + 1] = FlirColorUtils.LerpByte(c0[1], c1[1], t); // G
        pixels[dest + 2] = FlirColorUtils.LerpByte(c0[0], c1[0], t); // R
    }

    private static ThermalPaletteLutData? TryBuildEmbeddedLut(string paletteName, RadiometricMetadata? metadata)
    {
        if (metadata?.EmbeddedPaletteBgra is not { Length: 256 * 4 } embedded)
            return null;

        var requestedOriginal = paletteName.Equals("Original", StringComparison.OrdinalIgnoreCase);
        var matchesMetadataName = !string.IsNullOrWhiteSpace(metadata.PaletteName) &&
            paletteName.Equals(metadata.PaletteName, StringComparison.OrdinalIgnoreCase);
        var matchesDetectedPalette = metadata.DetectedPalette.HasValue &&
            paletteName.Equals(metadata.DetectedPalette.Value.ToString(), StringComparison.OrdinalIgnoreCase);

        if (!requestedOriginal && !matchesMetadataName && !matchesDetectedPalette)
            return null;

        // A câmera FLIR pode usar menos de 256 cores (ex: Iron = 224).
        // Reamostrar a paleta embedded para o tamanho real usado pela câmera.
        int cameraColors = metadata.PaletteColors ?? 256;
        cameraColors = Math.Clamp(cameraColors, 32, 256);

        var colors = new List<int[]>((int)(cameraColors * 1.1));
        for (var i = 0; i < cameraColors; i++)
        {
            // Mapear índice da paleta da câmera (0..cameraColors-1)
            // para o buffer BGRA de 256 entradas via interpolação
            double srcPos = (double)i / (cameraColors - 1) * 255.0;
            int lo = (int)Math.Floor(srcPos);
            int hi = Math.Min(lo + 1, 255);
            double t = srcPos - lo;

            int loIdx = lo * 4;
            int hiIdx = hi * 4;

            colors.Add([
                FlirColorUtils.LerpByte(embedded[loIdx + 2], embedded[hiIdx + 2], t), // R
                FlirColorUtils.LerpByte(embedded[loIdx + 1], embedded[hiIdx + 1], t), // G
                FlirColorUtils.LerpByte(embedded[loIdx],     embedded[hiIdx],     t)  // B
            ]);
        }

        return new ThermalPaletteLutData
        {
            Name = $"{paletteName} (embedded, {cameraColors}c)",
            Rgb = colors
        };
    }

    #region Carregamento de LUTs

    private async Task LoadAllLutsAsync(CancellationToken cancellationToken = default)
    {
        if (_lutsLoaded)
            return;

        _lutsLoaded = true;
        _lutCache.Clear();

        var lutSearchDirs = ResolveLutSearchDirectories().ToList();
        var lutFiles = new Dictionary<string, string>
        {
            // Paletas clássicas FLIR (originais)
            ["Iron"] = "iron_lut.json",
            ["Rainbow"] = "rainbow_lut.json",
            ["Grayscale"] = "grayscale_lut.json",
            ["Hotmetal"] = "hotmetal_lut.json",
            ["Arctic"] = "arctic_lut.json",
            ["Thermal"] = "thermal_lut.json",
            
            // Paletas científicas perceptualmente uniformes (matplotlib-like)
            ["Viridis"] = "viridis_lut.json",
            ["Plasma"] = "plasma_lut.json",
            ["Inferno"] = "inferno_lut.json",
            ["Magma"] = "magma_lut.json",
            ["Cividis"] = "cividis_lut.json",
            
            // Paletas térmicas clássicas
            ["Jet"] = "jet_lut.json",
            ["Hot"] = "hot_lut.json",
            ["Cool"] = "cool_lut.json",
            ["Coolwarm"] = "coolwarm_lut.json",
            ["Copper"] = "copper_lut.json",
            
            // Paletas simples
            ["Bone"] = "bone_lut.json",
            ["Spring"] = "spring_lut.json",
            ["Summer"] = "summer_lut.json",
            ["Autumn"] = "autumn_lut.json",
            ["Winter"] = "winter_lut.json",
            ["Gray"] = "gray_lut.json",
            
            // Paletas avançadas
            ["Turbo"] = "turbo_lut.json",
            ["Twilight"] = "twilight_lut.json",

            // Paletas FLIR extraídas de termogramas reais (Hypothesis C)
            ["FlirIron"] = "flir_iron_lut.json",
            ["FlirRainbow"] = "flir_rainbow_lut.json",
            ["FlirGrayscale"] = "flir_grayscale_lut.json"
        };

        await Task.Run(() =>
        {
            foreach (var kv in lutFiles)
            {
                if (_lutCache.ContainsKey(kv.Key))
                {
                    continue;
                }

                string? path = null;
                foreach (var lutDir in lutSearchDirs)
                {
                    var candidate = Path.Combine(lutDir, kv.Value);
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        break;
                    }
                }

                if (path is null)
                {
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    var rgbArray = doc.RootElement.GetProperty("rgb");
                    var colors = new List<int[]>();

                    foreach (var triplet in rgbArray.EnumerateArray())
                    {
                        colors.Add(new[]
                        {
                            triplet[0].GetInt32(),
                            triplet[1].GetInt32(),
                            triplet[2].GetInt32()
                        });
                    }

                    _lutCache[kv.Key] = new ThermalPaletteLutData
                    {
                        Name = kv.Key,
                        Rgb = colors
                    };

                    Debug.WriteLine($"[PALETTE_ENGINE] LUT carregada: {kv.Key} ({colors.Count} cores) de {path}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PALETTE_ENGINE] Erro ao carregar {kv.Key}: {ex.Message}");
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> ResolveLutSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "paletas")
        };

        var probe = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(probe); depth++)
        {
            candidates.Add(Path.Combine(probe, "paletas"));
            probe = Path.GetDirectoryName(probe);
        }

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var full = Path.GetFullPath(dir);
            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    #endregion
}

