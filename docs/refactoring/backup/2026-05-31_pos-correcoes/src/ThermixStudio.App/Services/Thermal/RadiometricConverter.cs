using System.Buffers.Binary;
using OpenCvSharp;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Conversor radiométrico: aplica calibração Planck, escala visual FLIR
/// e fallbacks de temperatura a partir de matriz 16‑bit ou 8‑bit.
/// </summary>
internal static class RadiometricConverter
{
    private const double MinTempFallback = 20.0;
    private const double MaxTempFallback = 120.0;
    private const int MaxAllowedErrors = 10;

    public static bool HasPlanckCalibration(RadiometricMetadata metadata)
    {
        return metadata.PlanckR1.HasValue
            && metadata.PlanckR2.HasValue
            && metadata.PlanckB.HasValue
            && metadata.PlanckF.HasValue
            && metadata.PlanckO.HasValue;
    }

    /// <summary>
    /// Tenta converter via Planck com byte-swap adaptativo.
    /// Retorna false se os valores forem inválidos (fallback necessário).
    /// </summary>
    public static bool TryLoadFromUShortWithPlanck(Mat source, ThermalImageData destination, RadiometricMetadata metadata)
    {
        destination.Metadata.Emissivity = Math.Clamp(metadata.Emissivity ?? 0.95, 0.01, 1.0);

        if (TryLoadPlanckCandidate(source, destination, metadata, useByteSwap: false))
            return true;

        using var swapped = ByteSwapUShortMat(source);
        if (TryLoadPlanckCandidate(swapped, destination, metadata, useByteSwap: true))
        {
            destination.Metadata.Notes = string.IsNullOrWhiteSpace(destination.Metadata.Notes)
                ? "RAW térmico interpretado com byte-swap antes da conversão Planck."
                : destination.Metadata.Notes;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Carrega temperaturas usando a escala real extraída dos metadados FLIR
    /// (ImageTemperatureMin/Max). Fallback para escala relativa se não houver dados.
    /// </summary>
    public static void LoadFromUShortWithActualScale(Mat source, ThermalImageData destination, RadiometricMetadata metadata)
    {
        if (!metadata.PaletteScaleMinC.HasValue || !metadata.PaletteScaleMaxC.HasValue)
        {
            LoadFromUShortFallback(source, destination);
            return;
        }

        var scaleMinC = metadata.PaletteScaleMinC.Value;
        var scaleMaxC = metadata.PaletteScaleMaxC.Value;

        ushort pixelMin = ushort.MaxValue;
        ushort pixelMax = ushort.MinValue;

        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                if (value < pixelMin) pixelMin = value;
                if (value > pixelMax) pixelMax = value;
            }

        var pixelRange = Math.Max(1, pixelMax - pixelMin);
        var tempRange = Math.Max(0.01, scaleMaxC - scaleMinC);

        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var pixelValue = source.At<ushort>(y, x);
                var normalized = (pixelValue - pixelMin) / (double)pixelRange;
                destination.Temperatures[y, x] = scaleMinC + (normalized * tempRange);
            }
    }

    public static void LoadFromUShortFallback(Mat source, ThermalImageData destination)
    {
        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;

        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                if (value < min) min = value;
                if (value > max) max = value;
            }

        var range = Math.Max(1, max - min);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                var normalized = (value - min) / (double)range;
                destination.Temperatures[y, x] = MinTempFallback + normalized * (MaxTempFallback - MinTempFallback);
            }
    }

    public static void LoadFromByteFallback(Mat source, ThermalImageData destination)
    {
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var intensity = source.At<byte>(y, x);
                destination.Temperatures[y, x] = MinTempFallback + (intensity / 255.0) * (MaxTempFallback - MinTempFallback);
            }
    }

    // ─── Planck internals ─────────────────────────────────────────────────

    private static bool TryLoadPlanckCandidate(Mat source, ThermalImageData destination, RadiometricMetadata metadata, bool useByteSwap)
    {
        var r1 = metadata.PlanckR1!.Value;
        var r2 = metadata.PlanckR2!.Value;
        var b = metadata.PlanckB!.Value;
        var f = metadata.PlanckF!.Value;
        var o = metadata.PlanckO!.Value;
        var emissivity = Math.Clamp(metadata.Emissivity ?? 1.0, 0.01, 1.0);

        var trefl = metadata.ReflectedTemperatureC ?? 20.0;
        var treflK = trefl + 273.15;
        var rawRefl = r1 / (r2 * (Math.Exp(b / treflK) - f)) - o;

        double minTemp = double.MaxValue;
        double maxTemp = double.MinValue;
        int errorCount = 0;
        var candidate = new double[source.Height, source.Width];

        try
        {
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    try
                    {
                        var raw = source.At<ushort>(y, x);
                        var rawObj = (raw - (1.0 - emissivity) * rawRefl) / emissivity;
                        var correctedRaw = Math.Max(1.0, rawObj);
                        var denominator = r2 * (correctedRaw + o);
                        var lnInput = (r1 / Math.Max(denominator, 0.000001)) + f;

                        if (lnInput <= 0)
                        {
                            errorCount++;
                            if (errorCount > MaxAllowedErrors) return false;
                            continue;
                        }

                        var tempK = b / Math.Log(Math.Max(lnInput, 1.000001));
                        var tempC = tempK - 273.15;

                        if (double.IsNaN(tempC) || double.IsInfinity(tempC))
                        {
                            errorCount++;
                            if (errorCount > MaxAllowedErrors) return false;
                            continue;
                        }

                        candidate[y, x] = tempC;
                        minTemp = Math.Min(minTemp, tempC);
                        maxTemp = Math.Max(maxTemp, tempC);
                    }
                    catch
                    {
                        errorCount++;
                        if (errorCount > MaxAllowedErrors) return false;
                    }
                }
            }

            if (minTemp < -100 || maxTemp > 200)
                return false;

            if (errorCount > MaxAllowedErrors / 2)
                return false;

            Buffer.BlockCopy(candidate, 0, destination.Temperatures, 0, Buffer.ByteLength(candidate));
            if (useByteSwap)
            {
                destination.Metadata.Notes = string.IsNullOrWhiteSpace(destination.Metadata.Notes)
                    ? "Conversao Planck aplicada com byte-swap do RAW termico."
                    : destination.Metadata.Notes;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Mat ByteSwapUShortMat(Mat source)
    {
        var swapped = source.Clone();
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                swapped.Set(y, x, BinaryPrimitives.ReverseEndianness(value));
            }
        return swapped;
    }
}
