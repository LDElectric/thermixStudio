using System.IO;
using MetadataExtractor;
using OpenCvSharp;
using ThermixStudio.App.Services.Thermal;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services;

/// <summary>
/// Orquestrador de análise térmica.
/// Delega parsing de formatos, extração de metadados, conversão radiométrica
/// e extração de imagem visível para módulos especializados.
/// </summary>
public sealed class ThermalAnalysisService : IThermalAnalysisService
{
    private readonly IExifToolService _exifTool;

    public ThermalAnalysisService(IExifToolService exifTool)
    {
        _exifTool = exifTool;
    }

    // ─── Load (orquestrador) ──────────────────────────────────────────────

    public async Task<ThermalImageData> LoadImageAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(imagePath).ToLowerInvariant();

        // Delegar parsers de formato
        if (extension == ".csv")
        {
            var csvData = CsvThermalParser.LoadCsvTemperatureMatrix(imagePath);
            csvData.SourceFormat = "CSV";
            csvData.IsRadiometricLikely = true;
            csvData.Metadata.Notes = "Matriz de temperatura importada de CSV.";
            return csvData;
        }

        if (extension == ".is2")
            return FlukeIs2Parser.Load(imagePath);

        if (extension is ".irg" or ".rjpeg")
            return InfiRayThermalParser.Load(imagePath);

        // FLIR / genérico: extrair metadados
        var metadata = RadiometricMetadataExtractor.ExtractMetadata(imagePath);
        metadata.EmbeddedPaletteBgra ??= await TryExtractEmbeddedPaletteBgraAsync(imagePath, cancellationToken).ConfigureAwait(false);

        Mat? thermalSource = null;
        try
        {
            thermalSource = await TryExtractRawThermalMatWithExifToolAsync(imagePath, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            thermalSource = null;
        }

        thermalSource ??= Cv2.ImRead(imagePath, ImreadModes.AnyDepth | ImreadModes.Grayscale);
        if (thermalSource.Empty())
            throw new InvalidOperationException("Nao foi possivel carregar a imagem termografica.");

        using (thermalSource)
        {
            var data = new ThermalImageData
            {
                Width = thermalSource.Width,
                Height = thermalSource.Height,
                Temperatures = new double[thermalSource.Height, thermalSource.Width],
                RawValues = thermalSource.ElemSize() > 1 ? ExtractRawValues(thermalSource) : null,
                SourceFormat = extension.TrimStart('.').ToUpperInvariant(),
                Metadata = metadata,
                IsRadiometricLikely = RadiometricConverter.HasPlanckCalibration(metadata)
            };

            if (thermalSource.ElemSize() > 1)
            {
                bool loaded = false;

                if (RadiometricConverter.HasPlanckCalibration(metadata))
                {
                    loaded = RadiometricConverter.TryLoadFromUShortWithPlanck(thermalSource, data, metadata);
                    if (loaded)
                    {
                        data.IsRadiometricLikely = true;
                        data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                            ? "Convertido com calibracao Planck extraida do EXIF/ExifTool."
                            : data.Metadata.Notes;
                    }
                }

                if (!loaded && metadata.PaletteScaleMinC.HasValue && metadata.PaletteScaleMaxC.HasValue)
                {
                    RadiometricConverter.LoadFromUShortWithActualScale(thermalSource, data, metadata);
                    data.IsRadiometricLikely = true;
                    data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                        ? "Temperaturas estimadas por escala visual FLIR extraida do EXIF."
                        : data.Metadata.Notes;
                    loaded = true;
                }

                if (!loaded)
                {
                    RadiometricConverter.LoadFromUShortFallback(thermalSource, data);
                    data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                        ? "Sem dados Planck validos; temperatura estimada por escala relativa."
                        : data.Metadata.Notes;
                }
            }
            else
            {
                RadiometricConverter.LoadFromByteFallback(thermalSource, data);
                data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                    ? "Imagem de 8 bits; temperatura estimada por intensidade."
                    : data.Metadata.Notes;
            }

            // ── SpotTempC via Planck+e no pixel do retículo ──
            // Quando MakerNote spotK=0 (center-reticle sem spot ativo),
            // calcula a temperatura do pixel central usando a matriz já convertida.
            if (!data.Metadata.SpotTemperatureC.HasValue &&
                data.Metadata.SpotNormalizedX.HasValue &&
                data.Metadata.SpotNormalizedY.HasValue &&
                data.Width > 0 && data.Height > 0)
            {
                int spotX = (int)Math.Round(data.Metadata.SpotNormalizedX.Value * (data.Width - 1));
                int spotY = (int)Math.Round(data.Metadata.SpotNormalizedY.Value * (data.Height - 1));
                spotX = Math.Clamp(spotX, 0, data.Width - 1);
                spotY = Math.Clamp(spotY, 0, data.Height - 1);
                var tempC = data.Temperatures[spotY, spotX];
                if (!double.IsNaN(tempC) && tempC > -100 && tempC < 2000)
                {
                    data.Metadata.SpotTemperatureC = tempC;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Planck+e] SpotTempC={tempC:F2}C at pixel ({spotX},{spotY})");
                }
            }

            // ── Cálculo Planck do Level/Span via RawValueMedian/Range ──
            // RawValueMedian e RawValueRange codificam a janela Level/Span
            // configurada na câmera no momento da captura (todos os modos).
            // Converter raw → °C via Planck inverso recupera o Level/Span
            // com precisão de ±2°C, eliminando a necessidade de pixel-analysis.
            if (data.Metadata.RawValueMedian.HasValue &&
                data.Metadata.RawValueRange.HasValue &&
                data.Metadata.PlanckR1.HasValue &&
                data.Metadata.PlanckR2.HasValue &&
                data.Metadata.PlanckB.HasValue &&
                data.Metadata.PlanckF.HasValue &&
                data.Metadata.PlanckO.HasValue)
            {
                double rawMin = data.Metadata.RawValueMedian.Value
                                - data.Metadata.RawValueRange.Value / 2.0;
                double rawMax = data.Metadata.RawValueMedian.Value
                                + data.Metadata.RawValueRange.Value / 2.0;

                data.Metadata.RawLevelMin = RawSignalToCelsius(rawMin, data.Metadata);
                data.Metadata.RawLevelMax = RawSignalToCelsius(rawMax, data.Metadata);

                System.Diagnostics.Debug.WriteLine(
                    $"[RawLevel] {data.Metadata.RawLevelMin:F1}°C to {data.Metadata.RawLevelMax:F1}°C " +
                    $"(from RawValueMedian={data.Metadata.RawValueMedian} Range={data.Metadata.RawValueRange})");
            }

            // Hypothesis C: construir LUT do JPEG original se for FLIR radiométrico
            // Level/Span agora derivado de RawValueMedian/Range via Planck (sem pixel-analysis)
            if (data.IsRadiometricLikely &&
                string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                data.Width > 0 && data.Height > 0)
            {
                try
                {
                    using var jpegBmp = new System.Drawing.Bitmap(imagePath);
                    if (jpegBmp.Width == data.Width && jpegBmp.Height == data.Height)
                    {
                        var jpegRect = new System.Drawing.Rectangle(0, 0, data.Width, data.Height);
                        var jpegData = jpegBmp.LockBits(jpegRect,
                            System.Drawing.Imaging.ImageLockMode.ReadOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        var jpegPixels = new byte[jpegData.Stride * data.Height];
                        System.Runtime.InteropServices.Marshal.Copy(jpegData.Scan0, jpegPixels, 0, jpegPixels.Length);
                        jpegBmp.UnlockBits(jpegData);

                        // ── Construção da LUT com range visual (anti-clipping) ──
                        // Usa RawLevelMin/Max (Planck via RawValueMedian/Range) como
                        // MIN/MAX da LUT. Isso garante que a cor preta (bin 0) mapeie
                        // exatamente onde a câmera clipou, sem os ~500ms de pixel-analysis.
                        // Fallback: PaletteScale do EXIF → range da matriz.
                        double minC = data.Metadata.RawLevelMin
                            ?? data.Metadata.PaletteScaleMinC
                            ?? data.Temperatures.Cast<double>().Min();
                        double maxC = data.Metadata.RawLevelMax
                            ?? data.Metadata.PaletteScaleMaxC
                            ?? data.Temperatures.Cast<double>().Max();
                        if (maxC <= minC) maxC = minC + 0.01;

                        data.CalibratedLut = TemperatureColorLut.Build(
                            data.Temperatures, jpegPixels,
                            data.Width, data.Height,
                            minC, maxC,
                            samplingMinC: minC, samplingMaxC: maxC);

                        System.Diagnostics.Debug.WriteLine(
                            $"[CALIB-LUT] Construída com range RawLevel: {minC:F1}~{maxC:F1}°C");

                        // ── Camada 2: Extração do ToneProfile (Curve256) do JPEG ──
                        // Reverse-lookup: correlaciona cores do JPEG com a paleta embarcada
                        // para extrair a curva tonal que a câmera aplicou.
                        if (metadata.EmbeddedPaletteBgra is { Length: 256 * 4 })
                        {
                            try
                            {
                                metadata.ToneProfile = ThermalToneExtractor.Extract(
                                    data.Temperatures, jpegPixels,
                                    data.Width, data.Height,
                                    metadata.EmbeddedPaletteBgra,
                                    OverlayMask.FlirE8xt,
                                    planckR1: metadata.PlanckR1,
                                    planckR2: metadata.PlanckR2,
                                    planckB: metadata.PlanckB,
                                    planckF: metadata.PlanckF,
                                    planckO: metadata.PlanckO,
                                    levelMinC: minC,
                                    levelMaxC: maxC);
                            }
                            catch (Exception toneEx)
                            {
                                metadata.ToneProfile = null;
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ToneExtractor] SKIP: {toneEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CALIB-LUT] SKIP: JPEG dimensões ({jpegBmp.Width}x{jpegBmp.Height}) ≠ matriz ({data.Width}x{data.Height})");
                    }
                }
                catch (Exception ex)
                {
                    // LUT é otimização; falha silenciosa não quebra o pipeline
                    data.CalibratedLut = null;
                    data.CalibratedPalette = null;
                    System.Diagnostics.Debug.WriteLine(
                        $"[CALIB-LUT] SKIP: falha ao carregar JPEG — {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CALIB-LUT] SKIP: IsRadiometric={data.IsRadiometricLikely} ext={extension} size={data.Width}x{data.Height}");
            }

            return data;
        }
    }

    public double GetTemperatureAt(ThermalImageData image, int x, int y)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        return image.Temperatures[y, x];
    }

    public ThermalStatistics GetAreaStatistics(ThermalImageData image, int x, int y, int width, int height)
    {
        var startX = Math.Clamp(x, 0, image.Width - 1);
        var startY = Math.Clamp(y, 0, image.Height - 1);
        var endX = Math.Clamp(startX + width, 0, image.Width);
        var endY = Math.Clamp(startY + height, 0, image.Height);
        return CalculateStats(image, startX, startY, endX, endY);
    }

    public LineProfileResult GetHorizontalLineProfile(ThermalImageData image, int y)
    {
        var row = Math.Clamp(y, 0, image.Height - 1);
        var values = new double[image.Width];
        for (var x = 0; x < image.Width; x++)
            values[x] = image.Temperatures[row, x];

        return new LineProfileResult
        {
            Temperatures = values,
            Statistics = new ThermalStatistics
            {
                Tmin = values.Min(),
                Tmax = values.Max(),
                Tavg = values.Average()
            }
        };
    }

    public ThermalStatistics GetIsothermStatistics(ThermalImageData image, double thresholdC)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;

        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var value = image.Temperatures[y, x];
                if (value < thresholdC) continue;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                count++;
            }

        if (count == 0) return new ThermalStatistics();
        return new ThermalStatistics { Tmin = min, Tmax = max, Tavg = sum / count };
    }

    public async Task<byte[]?> TryExtractEmbeddedPaletteAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await TryExtractEmbeddedPaletteBgraAsync(imagePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> TryExtractEmbeddedPaletteBgraAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_exifTool.IsExifToolAvailable())
        {
            var paletteRaw = await _exifTool.TryExtractPaletteBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            var converted = FlirPaletteConverter.ConvertEmbeddedPaletteToBgraLut(paletteRaw);
            if (converted is not null) return converted;
        }
        return FlirFffParser.TryExtractEmbeddedPaletteBgra(imagePath);
    }

    private async Task<Mat?> TryExtractRawThermalMatWithExifToolAsync(
        string imagePath, RadiometricMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metadata.VisibleImagePath))
            await FlirVisibleImageExtractor.TryExtractVisibleImageAsync(imagePath, metadata, _exifTool, cancellationToken).ConfigureAwait(false);

        if (!_exifTool.IsExifToolAvailable()) return null;

        var metadataJson = await _exifTool.TryGetMetadataJsonNumericAsync(imagePath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(metadataJson))
            RadiometricMetadataExtractor.TryApplyExifToolMetadata(metadataJson, metadata);

        // ── FLIR_0x0009: fallback binário se o JSON não tiver a tag ──
        if (metadata.FusionParams is null)
        {
            var fusionRaw = await _exifTool.TryExtractFusionParamsAsync(imagePath, cancellationToken).ConfigureAwait(false);
            if (fusionRaw is { Length: >= 8 })
                metadata.FusionParams = FlirFusionParams.Decode(fusionRaw);
        }

        var rawBytes = await _exifTool.TryExtractRawThermalAsync(imagePath, cancellationToken).ConfigureAwait(false);
        if (rawBytes is null || rawBytes.Length == 0) return null;

        return Cv2.ImDecode(rawBytes, ImreadModes.AnyDepth | ImreadModes.Grayscale);
    }

    public ThermalCameraBrand DetectCameraBrand(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".is2") return ThermalCameraBrand.Fluke;
            if (ext == ".irg") return ThermalCameraBrand.InfiRay;
            if (ext == ".rjpeg") return ThermalCameraBrand.InfiRay;

            var directories = ImageMetadataReader.ReadMetadata(filePath);
            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    if (!tag.Name.Equals("Make", StringComparison.OrdinalIgnoreCase) &&
                        !tag.Name.Equals("Model", StringComparison.OrdinalIgnoreCase)) continue;
                    var val = (tag.Description ?? string.Empty).ToUpperInvariant();
                    if (val.Contains("FLIR")) return ThermalCameraBrand.Flir;
                    if (val.Contains("FLUKE")) return ThermalCameraBrand.Fluke;
                    if (val.Contains("HIKVISION") || val.Contains("HIKMICRO")) return ThermalCameraBrand.Hikvision;
                    if (val.Contains("INFIRAY")) return ThermalCameraBrand.InfiRay;
                    if (val.Contains("GUIDE")) return ThermalCameraBrand.Guide;
                    if (val.Contains("BOSCH")) return ThermalCameraBrand.Bosch;
                    if (val.Contains("SEEK")) return ThermalCameraBrand.Seek;
                    if (val.Contains("TESTO")) return ThermalCameraBrand.Testo;
                }
                if (dir.Name.Contains("FLIR", StringComparison.OrdinalIgnoreCase))
                    return ThermalCameraBrand.Flir;
            }
        }
        catch { }

        return ThermalCameraBrand.Unknown;
    }

    private static ushort[,] ExtractRawValues(Mat source)
    {
        var values = new ushort[source.Height, source.Width];
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                values[y, x] = source.At<ushort>(y, x);
        return values;
    }

    private static ThermalStatistics CalculateStats(ThermalImageData image, int startX, int startY, int endX, int endY)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;

        for (var row = startY; row < endY; row++)
            for (var col = startX; col < endX; col++)
            {
                var value = image.Temperatures[row, col];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                count++;
            }

        if (count == 0) return new ThermalStatistics();
        return new ThermalStatistics { Tmin = min, Tmax = max, Tavg = sum / count };
    }

    /// <summary>
    /// Converte um valor RAW (sinal do detector) para °C usando a fórmula de Planck
    /// inversa com correção de emissividade e temperatura refletida.
    /// Réplica da lógica de ConvertRawToTemperatures do ThermalPaletteEngine
    /// para um único valor escalar.
    /// </summary>
    private static double RawSignalToCelsius(double raw, RadiometricMetadata m)
    {
        var r1 = m.PlanckR1!.Value;
        var r2 = m.PlanckR2!.Value;
        var b  = m.PlanckB!.Value;
        var f  = m.PlanckF!.Value;
        var o  = m.PlanckO!.Value;
        var emissivity = Math.Clamp(m.Emissivity ?? 1.0, 0.01, 1.0);
        var trefl = m.ReflectedTemperatureC ?? 20.0;
        var treflK = trefl + 273.15;
        var rawRefl = r1 / (r2 * (Math.Exp(b / treflK) - f)) - o;
        var rawObj = (raw - (1.0 - emissivity) * rawRefl) / emissivity;
        var correctedRaw = Math.Max(1.0, rawObj);
        var denominator = r2 * (correctedRaw + o);
        var lnInput = (r1 / Math.Max(denominator, 0.000001)) + f;
        if (lnInput <= 0) lnInput = 1.000001;
        var tempK = b / Math.Log(lnInput);
        return tempK - 273.15;
    }
}
