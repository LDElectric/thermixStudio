using System.IO;
using MetadataExtractor;
using OpenCvSharp;
using ThermixStudio.App.Services.Thermal;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

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
}
