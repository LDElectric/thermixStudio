using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThermixStudio.Core;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool TryInferCapturePresentation(
        ThermalImageData image,
        string imagePath,
        string? visibleImagePath,
        out ImageViewMode inferredMode,
        out ThermalPalette inferredPalette)
    {
        inferredMode = ImageViewMode.Thermal;
        inferredPalette = SelectedPalette;

        if (!TryLoadImageBgraPixels(imagePath, image.Width, image.Height, out var originalPixels) || originalPixels is null)
        {
            return false;
        }

        byte[]? visiblePixels = null;
        var hasVisible = !string.IsNullOrWhiteSpace(visibleImagePath) &&
                         TryLoadImageBgraPixels(visibleImagePath, image.Width, image.Height, out visiblePixels) &&
                         visiblePixels is not null;
        var metadataPalette = ResolvePaletteFromMetadata(image.Metadata);
        var imageName = Path.GetFileName(imagePath);

        if (hasVisible)
        {
            var originalLuma = ComputeBgraLumaPlane(originalPixels, image.Width, image.Height);
            var visibleLuma = ComputeBgraLumaPlane(visiblePixels!, image.Width, image.Height);
            var thermalLuma = TryBuildRenderedThermalLumaPlane(image, metadataPalette, out var renderedThermalLuma)
                ? renderedThermalLuma!
                : ComputeTemperatureLumaPlane(image.Temperatures, image.Width, image.Height);

            var corrOriginalThermal = CalculatePearsonCorrelation(originalLuma, thermalLuma);
            var corrOriginalVisible = CalculatePearsonCorrelation(originalLuma, visibleLuma);

            var highFreqOriginal = CalculateHighFrequencyEnergy(originalLuma, image.Width, image.Height);
            var highFreqVisible = CalculateHighFrequencyEnergy(visibleLuma, image.Width, image.Height);
            var highFreqRatioVisible = highFreqOriginal / Math.Max(highFreqVisible, 1e-6);

            LogToFile($"[MODE_INFER_RULE] file={imageName} signals corrOT={corrOriginalThermal:F4} corrOV={corrOriginalVisible:F4} hfO={highFreqOriginal:F4} hfV={highFreqVisible:F4} hfRatioOV={highFreqRatioVisible:F4}");

            if (corrOriginalThermal <= -0.05 && corrOriginalVisible <= -0.05 && highFreqRatioVisible >= 2.7)
            {
                inferredMode = ImageViewMode.Msx;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=MSX if corrOT<=-0.05 && corrOV<=-0.05 && hfRatioOV>=2.7");
                return true;
            }

            // Backup MSX rule: keeps strong edge dominance behavior even when corrOT is not negative.
            if (corrOriginalVisible <= -0.08 && highFreqRatioVisible >= 2.0 && highFreqOriginal >= 3.0)
            {
                inferredMode = ImageViewMode.Msx;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=MSX if corrOV<=-0.08 && hfRatioOV>=2.0 && hfO>=3.0");
                return true;
            }

            if (corrOriginalThermal >= 0.12 && corrOriginalVisible >= 0.10)
            {
                inferredMode = ImageViewMode.Blending;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=Blending if corrOT>=0.12 && corrOV>=0.10");
                return true;
            }

            if (corrOriginalThermal >= 0.15 && corrOriginalVisible <= 0.05)
            {
                inferredMode = ImageViewMode.Thermal;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=Thermal if corrOT>=0.15 && corrOV<=0.05");
                return true;
            }

            LogToFile($"[MODE_INFER_RULE] file={imageName} no explicit rule hit; falling back to legacy score search");
        }
        else
        {
            LogToFile($"[MODE_INFER_RULE] file={imageName} visible pair unavailable; using legacy score search");
        }

        var paletteCandidates = new List<ThermalPalette> { metadataPalette };
        if (SelectedPalette != ThermalPalette.Original && SelectedPalette != metadataPalette)
        {
            paletteCandidates.Add(SelectedPalette);
        }

        var bestScore = double.MaxValue;
        var found = false;
        var bestMode = ImageViewMode.Thermal;
        var bestPalette = SelectedPalette;
        var bestModeScore = new Dictionary<ImageViewMode, double>();
        var originalEdgeMap = ComputeLumaEdgeEnergyMap(originalPixels, image.Width, image.Height);

        var blendIntensityCandidates = new[] { 0.35, 0.45, 0.55, 0.70, 0.85 };
        var msxIntensityCandidates = new[] { 0.10, 0.18, 0.25, 0.35, 0.45, 0.60 };
        var pipScaleCandidates = new[] { 0.40, 0.50, 0.55, 0.60 };

        foreach (var palette in paletteCandidates)
        {
            byte[] thermalPixels;
            try
            {
                var paletteName = palette == ThermalPalette.Original ? "Iron" : palette.ToString();
                var profile = RenderProfile.FromMetadata(image.Metadata, LevelMinC, LevelMaxC);
                thermalPixels = _viewPipeline.RenderRadiometricWithProfileAsync(
                    image, paletteName, profile).GetAwaiter().GetResult();
            }
            catch
            {
                continue;
            }

            EvaluateCandidate(ImageViewMode.Thermal, palette, thermalPixels);

            if (!hasVisible)
            {
                continue;
            }

            EvaluateCandidate(ImageViewMode.Visible, palette, visiblePixels!);

            foreach (var blendIntensity in blendIntensityCandidates)
            {
                EvaluateCandidate(
                    ImageViewMode.Blending,
                    palette,
                    _viewPipeline.ComposeViewMode(
                        global::ThermixStudio.Core.ImageViewMode.Blending,
                        thermalPixels,
                        image.Width,
                        image.Height,
                        visiblePixels!,
                        blendIntensity,
                        Math.Clamp(PipScale, 0.1, 0.8),
                        image));
            }

            foreach (var pipScale in pipScaleCandidates)
            {
                EvaluateCandidate(
                    ImageViewMode.PiP,
                    palette,
                    _viewPipeline.ComposeViewMode(
                        global::ThermixStudio.Core.ImageViewMode.PiP,
                        thermalPixels,
                        image.Width,
                        image.Height,
                        visiblePixels!,
                        Math.Clamp(BlendFactor, 0.0, 1.0),
                        pipScale,
                        image));
            }

            foreach (var msxIntensity in msxIntensityCandidates)
            {
                EvaluateCandidate(
                    ImageViewMode.Msx,
                    palette,
                    _viewPipeline.ComposeViewMode(
                        global::ThermixStudio.Core.ImageViewMode.Msx,
                        thermalPixels,
                        image.Width,
                        image.Height,
                        visiblePixels!,
                        msxIntensity,
                        Math.Clamp(PipScale, 0.1, 0.8),
                        image));
            }
        }

        void EvaluateCandidate(ImageViewMode mode, ThermalPalette palette, byte[] candidatePixels)
        {
            var colorScore = CalculateBgraDistance(originalPixels, candidatePixels);
            var edgeScore = CalculateEdgeDistance(originalEdgeMap, candidatePixels, image.Width, image.Height);
            var modePenalty = mode == ImageViewMode.Blending ? 0.35 : 0.0;
            var score = colorScore + (0.45 * edgeScore) + modePenalty;

            if (!bestModeScore.TryGetValue(mode, out var currentModeBest) || score < currentModeBest)
            {
                bestModeScore[mode] = score;
            }

            if (score >= bestScore)
            {
                return;
            }

            bestScore = score;
            bestMode = mode;
            bestPalette = palette;
            found = true;
        }

        if (found)
        {
            if (hasVisible &&
                bestModeScore.TryGetValue(ImageViewMode.Blending, out var blendingScore) &&
                bestModeScore.TryGetValue(ImageViewMode.Msx, out var msxScore) &&
                Math.Abs(msxScore - blendingScore) <= Math.Max(1.0, blendingScore * 0.06))
            {
                // Neutral tie-break: if captured frame is closer to thermal anchor than visible anchor,
                // prefer MSX; otherwise prefer blending.
                var thermalAnchor = bestModeScore.TryGetValue(ImageViewMode.Thermal, out var thermalScore)
                    ? thermalScore
                    : double.MaxValue;
                var visibleAnchor = bestModeScore.TryGetValue(ImageViewMode.Visible, out var visibleScore)
                    ? visibleScore
                    : double.MaxValue;

                bestMode = thermalAnchor <= visibleAnchor
                    ? ImageViewMode.Msx
                    : ImageViewMode.Blending;
            }

            inferredMode = bestMode;
            inferredPalette = bestPalette;
        }

        return found;
    }

    private static double CalculateBgraDistance(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length < 4)
        {
            return double.MaxValue;
        }

        long sum = 0;
        var pixels = 0;

        for (var i = 0; i <= length - 4; i += 4)
        {
            sum += Math.Abs(left[i] - right[i]);
            sum += Math.Abs(left[i + 1] - right[i + 1]);
            sum += Math.Abs(left[i + 2] - right[i + 2]);
            pixels++;
        }

        if (pixels == 0)
        {
            return double.MaxValue;
        }

        return sum / (pixels * 3.0);
    }

    private static double[] ComputeBgraLumaPlane(byte[] pixels, int width, int height)
    {
        var pixelCount = width * height;
        var luma = new double[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            var pixelOffset = i * 4;
            luma[i] = (0.114 * pixels[pixelOffset]) +
                      (0.587 * pixels[pixelOffset + 1]) +
                      (0.299 * pixels[pixelOffset + 2]);
        }

        return luma;
    }

    private static double[] ComputeTemperatureLumaPlane(double[,] temperatures, int width, int height)
    {
        var pixelCount = width * height;
        var luma = new double[pixelCount];

        var min = double.MaxValue;
        var max = double.MinValue;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = temperatures[y, x];
                if (t < min)
                {
                    min = t;
                }

                if (t > max)
                {
                    max = t;
                }
            }
        }

        var range = Math.Max(max - min, 1e-6);
        var idx = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var normalized = (temperatures[y, x] - min) / range;
                luma[idx++] = Math.Clamp(normalized * 255.0, 0.0, 255.0);
            }
        }

        return luma;
    }

    private static double CalculatePearsonCorrelation(double[] left, double[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0.0;
        }

        var leftMean = 0.0;
        var rightMean = 0.0;

        for (var i = 0; i < length; i++)
        {
            leftMean += left[i];
            rightMean += right[i];
        }

        leftMean /= length;
        rightMean /= length;

        var covariance = 0.0;
        var leftVariance = 0.0;
        var rightVariance = 0.0;

        for (var i = 0; i < length; i++)
        {
            var ld = left[i] - leftMean;
            var rd = right[i] - rightMean;
            covariance += ld * rd;
            leftVariance += ld * ld;
            rightVariance += rd * rd;
        }

        var denominator = Math.Sqrt(leftVariance * rightVariance);
        if (denominator < 1e-9)
        {
            return 0.0;
        }

        return covariance / denominator;
    }

    private static double CalculateHighFrequencyEnergy(double[] luma, int width, int height)
    {
        if (luma.Length != width * height || width < 3 || height < 3)
        {
            return 0.0;
        }

        var sum = 0.0;
        var samples = 0;

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                var smooth = (
                    luma[idx] +
                    luma[idx - 1] +
                    luma[idx + 1] +
                    luma[idx - width] +
                    luma[idx + width]) / 5.0;

                sum += Math.Abs(luma[idx] - smooth);
                samples++;
            }
        }

        return samples == 0 ? 0.0 : sum / samples;
    }

    private static double[] ComputeLumaEdgeEnergyMap(byte[] pixels, int width, int height)
    {
        var pixelCount = width * height;
        var luma = new double[pixelCount];
        var edges = new double[pixelCount];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = y * width + x;
                var i = pixelIndex * 4;
                luma[pixelIndex] = (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
            }
        }

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                var gx = luma[idx + 1] - luma[idx - 1];
                var gy = luma[idx + width] - luma[idx - width];
                edges[idx] = Math.Abs(gx) + Math.Abs(gy);
            }
        }

        return edges;
    }

    private static double CalculateEdgeDistance(double[] leftEdgeMap, byte[] rightPixels, int width, int height)
    {
        if (leftEdgeMap.Length != width * height)
        {
            return double.MaxValue;
        }

        var rightEdgeMap = ComputeLumaEdgeEnergyMap(rightPixels, width, height);
        var samples = 0;
        var sum = 0.0;

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                sum += Math.Abs(leftEdgeMap[idx] - rightEdgeMap[idx]);
                samples++;
            }
        }

        if (samples == 0)
        {
            return double.MaxValue;
        }

        return sum / samples;
    }

    private static bool ShouldInferCaptureModeFromPixels(global::ThermixStudio.Core.ImageViewMode? metadataMode)
    {
        if (!metadataMode.HasValue)
        {
            return true;
        }

        return metadataMode.Value is not (
            global::ThermixStudio.Core.ImageViewMode.PiP or
            global::ThermixStudio.Core.ImageViewMode.Visible);
    }

    private bool TryBuildRenderedThermalLumaPlane(ThermalImageData image, ThermalPalette palette, out double[]? luma)
    {
        luma = null;
        if (!TryRenderThermalPixelsViaPipeline(image, palette, LevelMinC, LevelMaxC, out var thermalPixels, out _, out _))
        {
            return false;
        }

        luma = ComputeBgraLumaPlane(thermalPixels, image.Width, image.Height);
        return true;
    }

}
