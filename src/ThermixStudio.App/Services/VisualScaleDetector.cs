using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ThermixStudio.App.Services.Thermal;
using ThermixStudio.Core;
using ThermixStudio.Core.Thermal;

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
            return Task.FromResult(Failed("Arquivo original nao encontrado."));

        // Prioridade 1: ImageTemperatureMin/Max do EXIF (Level/Span em Kelvin → °C)
        // Estes valores são muito próximos dos burned-in (≤0.5°C) e são a fonte mais confiável
        if (image.Metadata.ImageTemperatureMinK.HasValue &&
            image.Metadata.ImageTemperatureMaxK.HasValue &&
            image.Metadata.ImageTemperatureMaxK.Value > image.Metadata.ImageTemperatureMinK.Value)
        {
            var minC = RadiometricMetadataExtractor.NormalizeExifTemperatureToCelsius(image.Metadata.ImageTemperatureMinK.Value);
            var maxC = RadiometricMetadataExtractor.NormalizeExifTemperatureToCelsius(image.Metadata.ImageTemperatureMaxK.Value);
            return Task.FromResult(new VisualScaleDetectionResult
            {
                Success = true,
                MinC = Math.Round(minC, 1),
                MaxC = Math.Round(maxC, 1),
                Source = VisualScaleSource.ExifImageTemperature,
                Confidence = 0.9,
                DetectorName = nameof(VisualScaleDetector),
                Notes = "ImageTemperatureMin/Max do EXIF (Level/Span da camera)."
            });
        }

        // Prioridade 2: visual-fit da barra de escala queimada
        // Usado quando EXIF não tem ImageTemperature (ex: câmeras não-FLIR ou metadados incompletos)
        if (image.Metadata.EmbeddedPaletteBgra is not null && image.Metadata.EmbeddedPaletteBgra.Length == 256 * 4)
        {
            var fit = TryFitVisualScaleToReference(imagePath, image);
            if (fit.Success && fit.Confidence >= 0.3)
                return Task.FromResult(fit);
        }

        // Prioridade 3: range da matriz radiométrica (último recurso)
        var (matrixMin, matrixMax) = TemperatureRangeCalculator.GetRange(image);
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

    private static VisualScaleDetectionResult TryFitVisualScaleToReference(string imagePath, ThermalImageData image)
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
                (seedMin, seedMax) = TemperatureRangeCalculator.GetRange(image);
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
                DetectorName = "visual-fit",
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
        var below = FlirColorUtils.ResolveYCrCbLimitColor(image.Metadata.PaletteBelowColorYCrCb, fallbackY: 50);
        var above = FlirColorUtils.ResolveYCrCbLimitColor(image.Metadata.PaletteAboveColorYCrCb, fallbackY: 170);
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
                var predicted = FlirColorUtils.MapTemperatureToEmbeddedPalette(t, minC, range, palette, below, above);
                var dr = target.R - predicted.r;
                var dg = target.G - predicted.g;
                var db = target.B - predicted.b;
                total += (dr * dr) + (dg * dg) + (db * db);
                count++;
            }
        }

        return count == 0 ? double.MaxValue : total / count;
    }

    private static bool IsLikelyCameraOverlay(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        var brightness = (color.R + color.G + color.B) / 3;
        return brightness > 180 && max - min < 50;
    }

    private static double ScoreToConfidence(double score)
    {
        if (!double.IsFinite(score) || score <= 0)
        {
            return 0.0;
        }

        var rmse = Math.Sqrt(score / 3.0);
        return Math.Clamp(1.0 - (rmse / 95.0), 0.0, 0.95);
    }

    private static VisualScaleDetectionResult Failed(string notes) => new()
    {
        Success = false,
        DetectorName = nameof(VisualScaleDetector),
        Notes = notes
    };
}
