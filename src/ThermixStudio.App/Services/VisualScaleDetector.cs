using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services;

public sealed class VisualScaleDetector : IVisualScaleDetector
{
    public Task<VisualScaleDetectionResult> DetectAsync(
        string imagePath,
        ThermalImageData image,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(imagePath))
        {
            return Task.FromResult(Failed("Arquivo original nao encontrado."));
        }

        if (IsFlir(image.Metadata))
        {
            var fit = TryFitFlirVisualScaleToReference(imagePath, image);
            if (fit.Success)
            {
                return Task.FromResult(fit);
            }
        }

        if (image.Metadata.PaletteScaleMinC.HasValue &&
            image.Metadata.PaletteScaleMaxC.HasValue &&
            image.Metadata.PaletteScaleMaxC.Value > image.Metadata.PaletteScaleMinC.Value)
        {
            return Task.FromResult(new VisualScaleDetectionResult
            {
                Success = true,
                MinC = image.Metadata.PaletteScaleMinC.Value,
                MaxC = image.Metadata.PaletteScaleMaxC.Value,
                Source = VisualScaleSource.ExifImageTemperature,
                Confidence = 0.35,
                DetectorName = nameof(VisualScaleDetector),
                Notes = "Fallback: ImageTemperatureMin/Max do EXIF. Pode diferir da escala queimada no JPEG."
            });
        }

        var (matrixMin, matrixMax) = GetTemperatureRange(image);
        return Task.FromResult(new VisualScaleDetectionResult
        {
            Success = double.IsFinite(matrixMin) && double.IsFinite(matrixMax) && matrixMax > matrixMin,
            MinC = matrixMin,
            MaxC = matrixMax,
            Source = VisualScaleSource.MatrixRange,
            Confidence = 0.2,
            DetectorName = nameof(VisualScaleDetector),
            Notes = "Fallback: range da matriz radiometrica."
        });
    }

    private static VisualScaleDetectionResult TryFitFlirVisualScaleToReference(string imagePath, ThermalImageData image)
    {
        if (image.Metadata.EmbeddedPaletteBgra is null || image.Metadata.EmbeddedPaletteBgra.Length != 256 * 4)
        {
            return Failed("Paleta embarcada FLIR indisponivel para ajuste visual.");
        }

        try
        {
            using var original = new Bitmap(imagePath);
            if (original.Width != image.Width || original.Height != image.Height)
            {
                return Failed("Ajuste visual ignorado: dimensoes da imagem original diferem da matriz termica.");
            }

            var seedMin = image.Metadata.PaletteScaleMinC;
            var seedMax = image.Metadata.PaletteScaleMaxC;
            if (!seedMin.HasValue || !seedMax.HasValue || seedMax.Value <= seedMin.Value)
            {
                (seedMin, seedMax) = GetTemperatureRange(image);
            }

            if (!seedMin.HasValue || !seedMax.HasValue || seedMax.Value <= seedMin.Value)
            {
                return Failed("Sem faixa inicial confiavel para ajuste visual.");
            }

            var best = SearchBestScale(
                original,
                image,
                seedMin.Value - 3.0,
                seedMin.Value + 3.0,
                seedMax.Value - 3.0,
                seedMax.Value + 3.0,
                0.25);

            best = SearchBestScale(
                original,
                image,
                best.min - 0.35,
                best.min + 0.35,
                best.max - 0.35,
                best.max + 0.35,
                0.05);

            var confidence = ScoreToConfidence(best.score);
            if (confidence < 0.3)
            {
                return Failed($"Ajuste visual teve baixa confianca. Score medio RGB^2={best.score:F1}.");
            }

            return new VisualScaleDetectionResult
            {
                Success = true,
                MinC = Math.Round(best.min, 1),
                MaxC = Math.Round(best.max, 1),
                Source = VisualScaleSource.VisualFitToReference,
                Confidence = confidence,
                DetectorName = "FLIR visual-fit",
                Notes = $"Escala estimada por comparacao com JPEG original. Score medio RGB^2={best.score:F1}."
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VISUAL_SCALE] Falha no ajuste visual FLIR: {ex.Message}");
            return Failed(ex.Message);
        }
    }

    private static (double min, double max, double score) SearchBestScale(
        Bitmap original,
        ThermalImageData image,
        double minStart,
        double minEnd,
        double maxStart,
        double maxEnd,
        double step)
    {
        var bestMin = minStart;
        var bestMax = maxEnd;
        var bestScore = double.MaxValue;

        for (var min = minStart; min <= minEnd + 0.0001; min += step)
        {
            for (var max = maxStart; max <= maxEnd + 0.0001; max += step)
            {
                if (max <= min + 1.0)
                {
                    continue;
                }

                var score = ScoreScale(original, image, min, max);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMin = min;
                    bestMax = max;
                }
            }
        }

        return (bestMin, bestMax, bestScore);
    }

    private static double ScoreScale(Bitmap original, ThermalImageData image, double minC, double maxC)
    {
        var palette = image.Metadata.EmbeddedPaletteBgra!;
        var below = ResolveYCrCbLimitColor(image.Metadata.PaletteBelowColorYCrCb, fallbackY: 50);
        var above = ResolveYCrCbLimitColor(image.Metadata.PaletteAboveColorYCrCb, fallbackY: 170);
        var range = Math.Max(0.01, maxC - minC);
        var total = 0.0;
        var count = 0;

        var xEnd = Math.Max(1, image.Width - (int)Math.Round(image.Width * 0.18));
        var yStart = Math.Max(0, (int)Math.Round(image.Height * 0.10));
        var yEnd = Math.Min(image.Height, (int)Math.Round(image.Height * 0.88));
        const int step = 3;

        for (var y = yStart; y < yEnd; y += step)
        {
            for (var x = 0; x < xEnd; x += step)
            {
                var target = original.GetPixel(x, y);
                if (IsLikelyCameraOverlay(target))
                {
                    continue;
                }

                var t = image.Temperatures[y, x];
                var predicted = MapTemperatureToColor(t, minC, range, palette, below, above);
                var dr = target.R - predicted.r;
                var dg = target.G - predicted.g;
                var db = target.B - predicted.b;
                total += (dr * dr) + (dg * dg) + (db * db);
                count++;
            }
        }

        return count == 0 ? double.MaxValue : total / count;
    }

    private static (byte r, byte g, byte b) MapTemperatureToColor(
        double tempC,
        double minC,
        double range,
        byte[] palette,
        (byte r, byte g, byte b) below,
        (byte r, byte g, byte b) above)
    {
        if (tempC < minC)
        {
            return below;
        }

        if (tempC > minC + range)
        {
            return above;
        }

        var normalized = Math.Clamp((tempC - minC) / range, 0.0, 1.0);
        var pos = normalized * 255.0;
        var lo = Math.Clamp((int)Math.Floor(pos), 0, 255);
        var hi = Math.Clamp(lo + 1, 0, 255);
        var f = pos - lo;
        var loIdx = lo * 4;
        var hiIdx = hi * 4;

        return (
            LerpByte(palette[loIdx + 2], palette[hiIdx + 2], f),
            LerpByte(palette[loIdx + 1], palette[hiIdx + 1], f),
            LerpByte(palette[loIdx], palette[hiIdx], f));
    }

    private static bool IsLikelyCameraOverlay(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        var brightness = (color.R + color.G + color.B) / 3;
        return brightness > 180 && max - min < 50;
    }

    private static (byte r, byte g, byte b) ResolveYCrCbLimitColor(int[]? yCrCb, int fallbackY)
    {
        var y = Math.Clamp(yCrCb is { Length: >= 1 } ? yCrCb[0] : fallbackY, 0, 255);
        var cr = Math.Clamp(yCrCb is { Length: >= 2 } ? yCrCb[1] : 128, 0, 255);
        var cb = Math.Clamp(yCrCb is { Length: >= 3 } ? yCrCb[2] : 128, 0, 255);

        var r = Math.Clamp(y + (1.402 * (cr - 128)), 0, 255);
        var g = Math.Clamp(y - (0.344 * (cb - 128)) - (0.714 * (cr - 128)), 0, 255);
        var b = Math.Clamp(y + (1.772 * (cb - 128)), 0, 255);

        return ((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
    }

    private static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);

    private static double ScoreToConfidence(double score)
    {
        if (!double.IsFinite(score) || score <= 0)
        {
            return 0.0;
        }

        var rmse = Math.Sqrt(score / 3.0);
        return Math.Clamp(1.0 - (rmse / 95.0), 0.0, 0.95);
    }

    private static (double min, double max) GetTemperatureRange(ThermalImageData image)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                if (double.IsFinite(t))
                {
                    if (t < min) min = t;
                    if (t > max) max = t;
                }
            }
        }

        return min <= max ? (min, max) : (0, 1);
    }

    private static bool IsFlir(RadiometricMetadata metadata)
        => metadata.Detector.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
           metadata.CameraModel.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
           metadata.Manufacturer.Contains("FLIR", StringComparison.OrdinalIgnoreCase);

    private static VisualScaleDetectionResult Failed(string notes) => new()
    {
        Success = false,
        DetectorName = nameof(VisualScaleDetector),
        Notes = notes
    };
}
