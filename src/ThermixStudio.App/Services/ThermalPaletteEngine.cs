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

        double minVal = SignalFromTemp(minT);
        double maxVal = SignalFromTemp(maxT);
        if (minVal > maxVal) { var tmp = minVal; minVal = maxVal; maxVal = tmp; }
        
        double range = maxVal - minVal;
        if (range <= 0) range = 0.01;

        var pixels = new byte[width * height * 4];
        var useLimitColors = FlirColorUtils.UsesFlirLimitColors(metadata);
        var belowColor = FlirColorUtils.ResolveYCrCbLimitColor(metadata?.PaletteBelowColorYCrCb, fallbackY: 50);
        var aboveColor = FlirColorUtils.ResolveYCrCbLimitColor(metadata?.PaletteAboveColorYCrCb, fallbackY: 170);
        var underflowColor = FlirColorUtils.ResolveYCrCbLimitColor(metadata?.PaletteUnderflowColorYCrCb, fallbackY: 41);
        var overflowColor = FlirColorUtils.ResolveYCrCbLimitColor(metadata?.PaletteOverflowColorYCrCb, fallbackY: 67);

        // 1. Calcular histograma de Sinal (Plateau Equalization / DDE algorithm)
        int numBins = 16384; // Resolução de 14 bits típica das matrizes térmicas
        int[] hist = new int[numBins];
        double[] vals = new double[width * height];
        int i = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = temperatures[y, x];
                double val = SignalFromTemp(t);
                vals[i++] = val;

                if (useLimitColors && (val < minVal || val > maxVal))
                {
                    continue;
                }

                int bin = (int)(((val - minVal) / range) * (numBins - 1));
                bin = Math.Clamp(bin, 0, numBins - 1);
                hist[bin]++;
            }
        }

        // 2. Limitar picos do histograma (Plateau) para que fundos gigantes não esmaguem o contraste
        // Usa 0.05% como padrão (mais próximo do comportamento FLIR); ajustável via PaletteStretch
        double plateauFraction = 0.0005;
        if (metadata?.PaletteStretch.HasValue == true && metadata.PaletteStretch.Value > 0)
        {
            // PaletteStretch da câmera: valores típicos 1-255, mapear para fração 0.01%-0.5%
            plateauFraction = Math.Clamp(metadata.PaletteStretch.Value / 50000.0, 0.0001, 0.005);
        }
        int plateau = Math.Max(1, (int)(width * height * plateauFraction));
        double[] clippedHist = new double[numBins];
        for (int b = 0; b < numBins; b++)
        {
            clippedHist[b] = Math.Min(hist[b], plateau);
        }

        double[] smoothHist = SmoothHistogram(clippedHist);
        double[] cdf = new double[numBins];
        double currentSum = 0;
        for (int b = 0; b < numBins; b++)
        {
            currentSum += smoothHist[b];
            cdf[b] = currentSum;
        }

        double cdfMax = currentSum;
        if (cdfMax <= 0) cdfMax = 1;
        double cdfMin = 0;
        for (int b = 0; b < numBins; b++)
        {
            if (hist[b] > 0)
            {
                cdfMin = cdf[b];
                break;
            }
        }

        double cdfRange = cdfMax - cdfMin;
        if (cdfRange <= 0) cdfRange = 1;

        // 3. Mapear os valores pelo CDF Equalizado (Distribuição Não-Linear igual FLIR)
        i = 0;
        // Limites de clipping do sensor — usar valores da câmera se disponíveis, defaults FLIR caso contrário
        double sensorMinC = metadata?.CameraTemperatureMinClip ?? -40.0;
        double sensorMaxC = metadata?.CameraTemperatureMaxClip ?? 280.0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double val = vals[i];
                double t = temperatures[y, x];
                i++;
                int dest = (y * width + x) * 4;

                // Prioridade 1: Underflow/Overflow do sensor (fora do range do hardware)
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

                // Removed Priority 2: Below/Above da escala visual.
                // FLIR uses Above/Below for Alarms. For regular visual scale, it clamps to palette limits.
                double binPosition = ((val - minVal) / range) * (numBins - 1);
                double linearNorm  = Math.Clamp((val - minVal) / range, 0.0, 1.0);
                double heNorm      = (InterpolateCdf(cdf, binPosition) - cdfMin) / cdfRange;
                double normalized = (linearNorm * 0.70) + (heNorm * 0.30);

                // Two-zone smooth curve — replica o DDE da FLIR:
                //   Zona fria  (< knee): gammaOut > 1 -> comprime midtones, empurra para roxo escuro
                //   Zona quente (> knee): hiOut -> expande, abrindo os laranjas e amarelos suavemente
                double knee     = 0.75;
                double softness = 0.12;
                double curveT   = Math.Clamp((normalized - (knee - softness)) / (2.0 * softness), 0.0, 1.0);
                double tSmooth  = curveT * curveT * (3.0 - 2.0 * curveT);

                double gammaOut = Math.Pow(Math.Max(normalized / knee, 1e-6), 1.25) * knee;
                double xHi      = Math.Clamp((normalized - knee) / (1.0 - knee), 0.0, 1.0);
                double hiOut    = knee + Math.Pow(xHi, 0.15) * (1.0 - knee);

                normalized = Math.Clamp(gammaOut * (1.0 - tSmooth) + hiOut * tSmooth, 0.0, 1.0);

                // Preserve detalhamento nos laranjas e ocre (warm detail)
                normalized = PreserveWarmDetail(normalized);

                // Injeção de brilho extremo apenas no núcleo (>94%)
                double whiteBoost = SmoothStep(0.94, 0.99, normalized);
                normalized = Math.Clamp(normalized + (whiteBoost * 0.015), 0.0, 1.0);

                WriteInterpolatedLutColor(lut, normalized, pixels, dest);
                pixels[dest + 3] = 255;
            }
        }
     return pixels;

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
        double warm1 = SmoothStep(0.52, 0.68, normalized);

        // faixa onde a FLIR preserva o amarelo escuro
        double warm2 = SmoothStep(0.68, 0.84, normalized);

        // liberar perto do branco real
        double whiteRelease = SmoothStep(0.90, 0.985, normalized);

        double hold =
            warm1 * 0.030 +
            warm2 * 0.045;

        hold *= (1.0 - whiteRelease);

        // pequena compressão local da faixa quente
        double x = normalized;

        if (x > 0.66 && x < 0.90)
        {
            double t = (x - 0.66) / (0.24);

            // curva S leve
            t = t * t * (3.0 - 2.0 * t);

            x -= 0.018 * t;
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
            ["Twilight"] = "twilight_lut.json"
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

