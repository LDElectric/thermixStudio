using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Buffers.Binary;
using System.Xml.Linq;
using MetadataExtractor;
using OpenCvSharp;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services;

public sealed class ThermalAnalysisService : IThermalAnalysisService
{
    private const double MinTempFallback = 20.0;
    private const double MaxTempFallback = 120.0;

    public Task<ThermalImageData> LoadImageAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(imagePath).ToLowerInvariant();

        if (extension == ".csv")
        {
            var csvData = LoadCsvTemperatureMatrix(imagePath);
            csvData.SourceFormat = "CSV";
            csvData.IsRadiometricLikely = true;
            csvData.Metadata.Notes = "Matriz de temperatura importada de CSV.";
            return Task.FromResult(csvData);
        }

        if (extension == ".is2")
        {
            return Task.FromResult(LoadFlukeIs2(imagePath));
        }

        if (extension is ".irg" or ".rjpeg")
        {
            return Task.FromResult(LoadInfiRayFile(imagePath));
        }

        var metadata = ExtractMetadata(imagePath);
        metadata.EmbeddedPaletteBgra ??= TryExtractEmbeddedPaletteBgra(imagePath);
        Mat? thermalSource = null;

        try
        {
            thermalSource = TryExtractRawThermalMatWithExifTool(imagePath, metadata);
        }
        catch
        {
            thermalSource = null;
        }

        thermalSource ??= Cv2.ImRead(imagePath, ImreadModes.AnyDepth | ImreadModes.Grayscale);
        if (thermalSource.Empty())
        {
            throw new InvalidOperationException("Nao foi possivel carregar a imagem termografica.");
        }

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
                IsRadiometricLikely = metadata.PlanckR1.HasValue && metadata.PlanckR2.HasValue && metadata.PlanckB.HasValue
            };

            if (thermalSource.ElemSize() > 1)
            {
                // HIERARQUIA DE PRIORIDADE (mais preciso → menos preciso):
                // 1. Se tem Planck válido → usar temperatura radiométrica real
                // 2. Se tem apenas escala visual FLIR (ImageTemperatureMin/Max) → usar escala relativa
                // 3. Se não tem nada → usar fallback relativo
                
                bool loaded = false;

                // Prioridade 1: Planck. Os JPGs FLIR podem exigir byte-swap do RAW;
                // TryLoadFromUShortWithPlanck resolve isso internamente.
                if (HasPlanckCalibration(metadata))
                {
                    bool planckSuccess = TryLoadFromUShortWithPlanck(thermalSource, data, metadata);
                    if (planckSuccess)
                    {
                        data.IsRadiometricLikely = true;
                        data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                            ? "Convertido com calibracao Planck extraida do EXIF/ExifTool."
                            : data.Metadata.Notes;
                        loaded = true;
                    }
                }

                // Prioridade 2: usar apenas a escala visual da câmera como fallback relativo.
                if (!loaded && metadata.PaletteScaleMinC.HasValue && metadata.PaletteScaleMaxC.HasValue)
                {
                    LoadFromUShortWithActualScale(thermalSource, data, metadata);
                    data.IsRadiometricLikely = true;
                    data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                        ? "Temperaturas estimadas por escala visual FLIR extraida do EXIF."
                        : data.Metadata.Notes;
                    loaded = true;
                }

                // Prioridade 3: Fallback relativo (último recurso)
                if (!loaded)
                {
                    LoadFromUShortFallback(thermalSource, data);
                    data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                        ? "Sem dados Planck validos; temperatura estimada por escala relativa."
                        : data.Metadata.Notes;
                }
            }
            else
            {
                LoadFromByteFallback(thermalSource, data);
                data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                    ? "Imagem de 8 bits; temperatura estimada por intensidade."
                    : data.Metadata.Notes;
            }

            return Task.FromResult(data);
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
        {
            values[x] = image.Temperatures[row, x];
        }

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
        {
            for (var x = 0; x < image.Width; x++)
            {
                var value = image.Temperatures[y, x];
                if (value < thresholdC)
                {
                    continue;
                }

                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                count++;
            }
        }

        if (count == 0)
        {
            return new ThermalStatistics();
        }

        return new ThermalStatistics
        {
            Tmin = min,
            Tmax = max,
            Tavg = sum / count
        };
    }

    /// <summary>
    /// Extrai a paleta de cores embutida FLIR da imagem JPEG termográfica.
    /// Retorna array BGRA (256 × 4 bytes) ou null se não puder ser extraída.
    /// </summary>
    public Task<byte[]?> TryExtractEmbeddedPaletteAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryExtractEmbeddedPaletteBgra(imagePath));
    }

    private static byte[]? TryExtractEmbeddedPaletteBgra(string imagePath)
    {
        // 1) Prefer exiftool extraction: robust across FLIR models and palette sizes (e.g., 224 colors).
        var exifToolExe = ResolveExifToolPath();
        if (!string.IsNullOrWhiteSpace(exifToolExe))
        {
            var paletteRaw = RunProcessCaptureBinary(exifToolExe, $"-b -Palette \"{imagePath}\"");
            var converted = ConvertEmbeddedPaletteToBgraLut(paletteRaw);
            if (converted is not null)
            {
                return converted;
            }
        }

        // 2) Fallback to direct FLIR APP1 parsing when exiftool is unavailable.
        return TryExtractEmbeddedPaletteFromFlirApp1(imagePath);
    }

    private static ushort[,] ExtractRawValues(Mat source)
    {
        var values = new ushort[source.Height, source.Width];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                values[y, x] = source.At<ushort>(y, x);
            }
        }

        return values;
    }

    private static byte[]? ConvertEmbeddedPaletteToBgraLut(byte[]? paletteRaw)
    {
        if (paletteRaw is null || paletteRaw.Length < 3)
        {
            return null;
        }

        // Some sources may already provide BGRA 256x4.
        if (paletteRaw.Length == 256 * 4)
        {
            return paletteRaw;
        }

        // FLIR palette comes as YCbCr triplets (e.g., 224*3 = 672 bytes).
        // The exiftool -b -Palette output for FLIR cameras is in YCbCr (BT.601), NOT plain RGB.
        // Verified: "Paleta Cinzento" has Y=16..235, Cb=128, Cr=128 (neutral chroma = true grey).
        if (paletteRaw.Length % 3 != 0)
        {
            return null;
        }

        var colorCount = paletteRaw.Length / 3;
        if (colorCount < 16)
        {
            return null;
        }

        var bgra = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            // Interpolate between source colors (e.g., 224 colors -> 256)
            var srcIndex = (int)Math.Round(i * (colorCount - 1) / 255.0);
            var baseSrc = srcIndex * 3;

            // Convert YCbCr (BT.601) → RGB
            // NOTE: FLIR stores palette as [Y, Cr, Cb] — Cr comes BEFORE Cb (opposite of standard JPEG)
            int y  = paletteRaw[baseSrc];
            int cr = paletteRaw[baseSrc + 1]; // FLIR: byte1 = Cr (red-difference)
            int cb = paletteRaw[baseSrc + 2]; // FLIR: byte2 = Cb (blue-difference)
            var r = Math.Clamp((int)(y + 1.402  * (cr - 128)), 0, 255);
            var g = Math.Clamp((int)(y - 0.344  * (cb - 128) - 0.714 * (cr - 128)), 0, 255);
            var b = Math.Clamp((int)(y + 1.772  * (cb - 128)), 0, 255);

            // Store as BGRA (Blue, Green, Red, Alpha)
            var dst = i * 4;
            bgra[dst]     = (byte)b;
            bgra[dst + 1] = (byte)g;
            bgra[dst + 2] = (byte)r;
            bgra[dst + 3] = 255;
        }

        return bgra;
    }

    private static ThermalImageData LoadCsvTemperatureMatrix(string path)
    {
        var lines = File.ReadAllLines(path)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (lines.Length == 0)
        {
            throw new InvalidOperationException("CSV vazio para importacao termica.");
        }

        var separators = new[] { ';', ',', '\t' };
        var rows = new List<double[]>();
        foreach (var line in lines)
        {
            var parts = line.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var row = parts.Select(ParseDoubleFlexible).ToArray();
            if (row.Length > 0)
            {
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("CSV sem dados numericos validos.");
        }

        var width = rows.Min(r => r.Length);
        var height = rows.Count;
        var matrix = new double[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                matrix[y, x] = rows[y][x];
            }
        }

        return new ThermalImageData
        {
            Width = width,
            Height = height,
            Temperatures = matrix
        };
    }

    private static double ParseDoubleFlexible(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedInvariant))
        {
            return parsedInvariant;
        }

        var normalized = value.Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNormalized))
        {
            return parsedNormalized;
        }

        throw new InvalidOperationException($"Valor de temperatura invalido no CSV: {value}");
    }

    private static RadiometricMetadata ExtractMetadata(string imagePath)
    {
        var metadata = new RadiometricMetadata();

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(imagePath);
            foreach (var directory in directories)
            {
                foreach (var tag in directory.Tags)
                {
                    var name = tag.Name;
                    var description = tag.Description ?? string.Empty;

                    if (name.Contains("Model", StringComparison.OrdinalIgnoreCase) && metadata.CameraModel == "Unknown")
                    {
                        metadata.CameraModel = description;
                    }

                    if (name.Contains("Make", StringComparison.OrdinalIgnoreCase) && metadata.Manufacturer == "Unknown")
                    {
                        metadata.Manufacturer = description;
                    }

                    if (name.Contains("Emissivity", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Emissivity ??= TryParseDouble(description);
                    }

                    if (name.Contains("Humidity", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.RelativeHumidity ??= TryParseDouble(description);
                    }

                    if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.ObjectDistanceM ??= TryParseDouble(description);
                    }

                    if (name.Contains("Reflected", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.ReflectedTemperatureC ??= TryParseDouble(description);
                    }

                    if (name.Contains("Ambient", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.AmbientTemperatureC ??= TryParseDouble(description);
                    }

                    var isSpotTemperatureTag =
                        name.Contains("Tspot", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tspor", StringComparison.OrdinalIgnoreCase) ||
                        (name.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains("Temp", StringComparison.OrdinalIgnoreCase));

                    if (isSpotTemperatureTag)
                    {
                        metadata.SpotTemperatureC ??= TryParseDouble(description);
                    }

                    var spotCoordinate = TryParseDouble(description);
                    if (spotCoordinate.HasValue &&
                        name.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("X", StringComparison.OrdinalIgnoreCase) &&
                        spotCoordinate.Value >= 0.0 &&
                        spotCoordinate.Value <= 1.0)
                    {
                        metadata.SpotNormalizedX ??= spotCoordinate.Value;
                    }

                    if (spotCoordinate.HasValue &&
                        name.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Y", StringComparison.OrdinalIgnoreCase) &&
                        spotCoordinate.Value >= 0.0 &&
                        spotCoordinate.Value <= 1.0)
                    {
                        metadata.SpotNormalizedY ??= spotCoordinate.Value;
                    }

                    if (name.Contains("Image Temperature Min", StringComparison.OrdinalIgnoreCase))
                    {
                        var scaleMin = TryParseDouble(description);
                        if (scaleMin.HasValue)
                        {
                            metadata.PaletteScaleMinC ??= NormalizeExifTemperatureToCelsius(scaleMin.Value);
                        }
                    }

                    if (name.Contains("Image Temperature Max", StringComparison.OrdinalIgnoreCase))
                    {
                        var scaleMax = TryParseDouble(description);
                        if (scaleMax.HasValue)
                        {
                            metadata.PaletteScaleMaxC ??= NormalizeExifTemperatureToCelsius(scaleMax.Value);
                        }
                    }

                    if (name.Contains("Palette Name", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(metadata.PaletteName))
                    {
                        metadata.PaletteName = description;
                    }

                    if (name.Contains("Above Color", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.PaletteAboveColorYCrCb ??= TryParseColorTriplet(description);
                    }

                    if (name.Contains("Below Color", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.PaletteBelowColorYCrCb ??= TryParseColorTriplet(description);
                    }

                    if (name.Contains("Overflow Color", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.PaletteOverflowColorYCrCb ??= TryParseColorTriplet(description);
                    }

                    if (name.Contains("Underflow Color", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.PaletteUnderflowColorYCrCb ??= TryParseColorTriplet(description);
                    }

                    if (directory.Name.Contains("FLIR", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Detector = "FLIR";
                    }

                    // Hikvision / HikMicro embute metadados em tags proprietárias
                    if (directory.Name.Contains("Hikvision", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Contains("HikMicro", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Detector = "Hikvision";
                    }
                }
            }
        }
        catch
        {
            metadata.Notes = "Nao foi possivel extrair metadados EXIF/XMP do arquivo.";
        }

        if (metadata.Detector == "Unknown")
        {
            var make  = metadata.Manufacturer.ToUpperInvariant();
            var model = metadata.CameraModel.ToUpperInvariant();
            var combined = make + " " + model;

            if (combined.Contains("FLIR"))          metadata.Detector = "FLIR";
            else if (combined.Contains("FLUKE"))    metadata.Detector = "Fluke";
            else if (combined.Contains("HIKVISION") || combined.Contains("HIKMICRO")) metadata.Detector = "Hikvision";
            else if (combined.Contains("INFIRAY") || combined.Contains("IRG"))        metadata.Detector = "InfiRay";
            else if (combined.Contains("GUIDE"))    metadata.Detector = "Guide";
            else if (combined.Contains("BOSCH"))    metadata.Detector = "Bosch";
            else if (combined.Contains("SEEK"))     metadata.Detector = "Seek";
            else if (combined.Contains("TESTO"))    metadata.Detector = "Testo";
        }

        TryApplyFlirApp1AlignmentMetadata(imagePath, metadata);

        return metadata;
    }

    private static Mat? TryExtractRawThermalMatWithExifTool(string imagePath, RadiometricMetadata metadata)
    {
        var exifToolExe = ResolveExifToolPath();

        if (string.IsNullOrWhiteSpace(metadata.VisibleImagePath))
        {
            TryExtractVisibleImage(imagePath, metadata, exifToolExe);
        }

        if (string.IsNullOrWhiteSpace(exifToolExe))
        {
            return null;
        }

        var metadataJson = RunProcessCapture(exifToolExe, $"-j -n \"{imagePath}\"");
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            TryApplyExifToolMetadata(metadataJson, metadata);
        }

        var rawBytes = RunProcessCaptureBinary(exifToolExe, $"-b -RawThermalImage \"{imagePath}\"");
        if (rawBytes is null || rawBytes.Length == 0)
        {
            return null;
        }

        return Cv2.ImDecode(rawBytes, ImreadModes.AnyDepth | ImreadModes.Grayscale);
    }

    private static void TryApplyExifToolMetadata(string metadataJson, RadiometricMetadata metadata)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(metadataJson);
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return;
            }

            var first = root[0];

            metadata.CameraModel = ReadString(first, "Model") ?? metadata.CameraModel;
            metadata.Manufacturer = ReadString(first, "Make") ?? metadata.Manufacturer;
            metadata.Emissivity ??= ReadDouble(first, "Emissivity");
            metadata.ObjectDistanceM ??= ReadDouble(first, "ObjectDistance");
            metadata.RelativeHumidity ??= ReadDouble(first, "RelativeHumidity");
            metadata.ReflectedTemperatureC ??= ReadDouble(first, "ReflectedApparentTemperature");
            metadata.AmbientTemperatureC ??= ReadDouble(first, "AtmosphericTemperature");
            metadata.SpotTemperatureC ??= ReadFirstDouble(first,
                "SpotTemperature",
                "SpotMeterValue",
                "Spot1Temperature",
                "Spot1Value",
                "Tspot",
                "Tspor");
            var imageWidth = ReadFirstDouble(first, "RawThermalImageWidth", "ImageWidth", "ExifImageWidth");
            var imageHeight = ReadFirstDouble(first, "RawThermalImageHeight", "ImageHeight", "ExifImageHeight");
            metadata.SpotNormalizedX ??= NormalizeSpotCoordinate(
                ReadFirstDouble(first, "SpotX", "Spot1X", "SpotMeterX", "SpotMeterX1", "TspotX"),
                imageWidth);
            metadata.SpotNormalizedY ??= NormalizeSpotCoordinate(
                ReadFirstDouble(first, "SpotY", "Spot1Y", "SpotMeterY", "SpotMeterY1", "TspotY"),
                imageHeight);
            metadata.PlanckR1 ??= ReadDouble(first, "PlanckR1");
            metadata.PlanckR2 ??= ReadDouble(first, "PlanckR2");
            metadata.PlanckB ??= ReadDouble(first, "PlanckB");
            metadata.PlanckF ??= ReadDouble(first, "PlanckF");
            metadata.PlanckO ??= ReadDouble(first, "PlanckO");
            metadata.DetectedViewMode ??= MapExifModeToEnum(ReadString(first, "ThermalImageType"));
            metadata.PaletteName ??= ReadString(first, "PaletteName") ?? ReadString(first, "Palette");
            metadata.PaletteColors ??= ReadInt(first, "PaletteColors");
            metadata.PaletteFileName ??= ReadString(first, "PaletteFileName");
            metadata.PaletteMethod ??= ReadInt(first, "PaletteMethod");
            metadata.PaletteStretch ??= ReadInt(first, "PaletteStretch");
            metadata.PaletteAboveColorYCrCb ??= ReadColorTriplet(first, "AboveColor");
            metadata.PaletteBelowColorYCrCb ??= ReadColorTriplet(first, "BelowColor");
            metadata.PaletteOverflowColorYCrCb ??= ReadColorTriplet(first, "OverflowColor");
            metadata.PaletteUnderflowColorYCrCb ??= ReadColorTriplet(first, "UnderflowColor");
            metadata.Isotherm1ColorYCrCb ??= ReadColorTriplet(first, "Isotherm1Color");
            metadata.Isotherm2ColorYCrCb ??= ReadColorTriplet(first, "Isotherm2Color");
            
            // Mapear nome da paleta do EXIF para enum ThermalPalette
            if (metadata.DetectedPalette is null && !string.IsNullOrWhiteSpace(metadata.PaletteName))
            {
                metadata.DetectedPalette = MapExifPaletteNameToEnum(metadata.PaletteName);
            }

            // Campos adicionais — exibição no painel e escala real da barra lateral
            metadata.DateTimeOriginal  ??= ReadString(first, "DateTimeOriginal") ?? ReadString(first, "CreateDate");
            metadata.CameraSerialNumber ??= ReadStringOrNumber(first, "CameraSerialNumber");
            metadata.FieldOfView        ??= ReadDouble(first, "FieldOfView");
            metadata.IRWindowTemperatureC ??= ReadDouble(first, "IRWindowTemperature");
            metadata.IRWindowTransmission ??= ReadDouble(first, "IRWindowTransmission");
            metadata.Real2IR ??= ReadDouble(first, "Real2IR");
            metadata.OffsetX ??= ReadInt(first, "OffsetX");
            metadata.OffsetY ??= ReadInt(first, "OffsetY");
            metadata.PiPX1 ??= ReadInt(first, "PiPX1");
            metadata.PiPX2 ??= ReadInt(first, "PiPX2");
            metadata.PiPY1 ??= ReadInt(first, "PiPY1");
            metadata.PiPY2 ??= ReadInt(first, "PiPY2");

            // ImageTemperatureMin/Max estão em Kelvin — converter para Celsius
            var rawScaleMin = ReadDouble(first, "ImageTemperatureMin");
            var rawScaleMax = ReadDouble(first, "ImageTemperatureMax");
            metadata.ImageTemperatureMinK ??= rawScaleMin.HasValue ? (int)Math.Round(rawScaleMin.Value) : null;
            metadata.ImageTemperatureMaxK ??= rawScaleMax.HasValue ? (int)Math.Round(rawScaleMax.Value) : null;
            if (rawScaleMin.HasValue && metadata.PaletteScaleMinC is null)
                metadata.PaletteScaleMinC = NormalizeExifTemperatureToCelsius(rawScaleMin.Value);
            if (rawScaleMax.HasValue && metadata.PaletteScaleMaxC is null)
                metadata.PaletteScaleMaxC = NormalizeExifTemperatureToCelsius(rawScaleMax.Value);

            metadata.RawValueRangeMin ??= ReadInt(first, "RawValueRangeMin");
            metadata.RawValueRangeMax ??= ReadInt(first, "RawValueRangeMax");
            metadata.RawValueMedian ??= ReadInt(first, "RawValueMedian");
            metadata.RawValueRange ??= ReadInt(first, "RawValueRange");
            metadata.UnknownTemperature ??= ReadDouble(first, "UnknownTemperature");

            if (!string.IsNullOrWhiteSpace(metadata.CameraModel) && metadata.CameraModel.Contains("FLIR", StringComparison.OrdinalIgnoreCase))
            {
                metadata.Detector = "FLIR";
            }

        }
        catch
        {
            // Keep prior metadata when exiftool JSON parsing fails.
        }
    }

    /// <summary>
    /// Mapeia o nome da paleta extraído do EXIF (-PaletteName) para o enum ThermalPalette.
    /// Retorna null se o nome não for reconhecido.
    /// </summary>
    private static ThermalPalette? MapExifPaletteNameToEnum(string? exifPaletteName)
    {
        if (string.IsNullOrWhiteSpace(exifPaletteName))
            return null;

        return exifPaletteName.ToLowerInvariant() switch
        {
            "iron"       => ThermalPalette.Iron,
            "ironbow"    => ThermalPalette.Iron,
            "gray"       => ThermalPalette.Grayscale,
            "grey"       => ThermalPalette.Grayscale,
            "grayscale"  => ThermalPalette.Grayscale,
            "rainbow"    => ThermalPalette.Rainbow,
            "hotmetal"   => ThermalPalette.Hotmetal,
            "hotmetal 2" => ThermalPalette.Hotmetal,
            "arctic"     => ThermalPalette.Arctic,
            "lava"       => ThermalPalette.Arctic,  // fallback próximo
            _            => null
        };
    }

    private static ImageViewMode? MapExifModeToEnum(string? exifMode)
    {
        if (string.IsNullOrWhiteSpace(exifMode))
        {
            return null;
        }

        var mode = exifMode.Trim().ToLowerInvariant();
        if (mode.Contains("multi-spectral") || mode.Contains("msx"))
        {
            return ImageViewMode.Msx;
        }

        if (mode.Contains("visual"))
        {
            return ImageViewMode.Visible;
        }

        if (mode.Contains("pip") || mode.Contains("picture-in-picture"))
        {
            return ImageViewMode.PiP;
        }

        if (mode.Contains("fusion") || mode.Contains("blended") || mode.Contains("blend"))
        {
            return ImageViewMode.Blending;
        }

        return ImageViewMode.Thermal;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return prop.GetString();
    }

    private static string? ReadStringOrNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        var value = ReadDouble(element, propertyName);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    private static int[]? ReadColorTriplet(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            return TryParseColorTriplet(prop.GetString());
        }

        if (prop.ValueKind == JsonValueKind.Array && prop.GetArrayLength() >= 3)
        {
            var color = new int[3];
            for (var i = 0; i < 3; i++)
            {
                var item = prop[i];
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var numeric))
                {
                    color[i] = Math.Clamp(numeric, 0, 255);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.String &&
                    int.TryParse(item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textNumeric))
                {
                    color[i] = Math.Clamp(textNumeric, 0, 255);
                    continue;
                }

                return null;
            }

            return color;
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var textNumeric))
        {
            return textNumeric;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            return TryParseDouble(prop.GetString());
        }

        return null;
    }

    private static double? ReadFirstDouble(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadDouble(element, propertyName);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static double? NormalizeSpotCoordinate(double? value, double? extent)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return null;
        }

        var coordinate = value.Value;
        if (coordinate >= 0.0 && coordinate <= 1.0)
        {
            return coordinate;
        }

        if (extent.HasValue && double.IsFinite(extent.Value) && extent.Value > 1.0)
        {
            return Math.Clamp(coordinate / Math.Max(1.0, extent.Value - 1.0), 0.0, 1.0);
        }

        return null;
    }

    private static double NormalizeExifTemperatureToCelsius(double value)
        => value > 200.0 ? value - 273.15 : value;

    private static void TryApplyFlirApp1AlignmentMetadata(string imagePath, RadiometricMetadata metadata)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(imagePath);
        }
        catch
        {
            return;
        }

        var chunks = new SortedDictionary<byte, byte[]>();
        var index = 0;

        while (index + 4 < bytes.Length)
        {
            if (bytes[index] != 0xFF || bytes[index + 1] != 0xE1)
            {
                index++;
                continue;
            }

            var segmentLength = (bytes[index + 2] << 8) | bytes[index + 3];
            if (segmentLength < 10)
            {
                break;
            }

            var segmentEnd = index + 2 + segmentLength;
            if (segmentEnd > bytes.Length)
            {
                break;
            }

            var contentStart = index + 4;
            var isFlirChunk = contentStart + 8 <= bytes.Length
                && bytes[contentStart] == (byte)'F'
                && bytes[contentStart + 1] == (byte)'L'
                && bytes[contentStart + 2] == (byte)'I'
                && bytes[contentStart + 3] == (byte)'R'
                && bytes[contentStart + 4] == 0x00;

            if (isFlirChunk)
            {
                var chunkNumber = bytes[contentStart + 6];
                var payloadStart = contentStart + 8;
                var payloadLength = segmentEnd - payloadStart;
                if (payloadLength > 0)
                {
                    var payload = new byte[payloadLength];
                    Buffer.BlockCopy(bytes, payloadStart, payload, 0, payloadLength);
                    chunks[chunkNumber] = payload;
                }
            }

            index = segmentEnd;
        }

        if (chunks.Count == 0)
        {
            return;
        }

        using var fffStream = new MemoryStream();
        foreach (var chunk in chunks.Values)
        {
            fffStream.Write(chunk, 0, chunk.Length);
        }

        var fff = fffStream.ToArray();
        if (fff.Length < 64)
        {
            return;
        }

        var hasValidSignature = fff[0] == (byte)'F' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00
            || fff[0] == (byte)'A' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00;
        if (!hasValidSignature)
        {
            return;
        }

        var recordDirectoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(24, 4));
        var recordCount = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(28, 4));

        if (recordDirectoryOffset <= 0 || recordCount <= 0)
        {
            return;
        }

        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var entryOffset = recordDirectoryOffset + (recordIndex * 32);
            if (entryOffset + 20 > fff.Length)
            {
                break;
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(fff.AsSpan(entryOffset, 2));
            var recordOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 12, 4));
            var recordLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 16, 4));

            if (recordType == 0x0022)
            {
                if (recordOffset >= 0 && recordLength >= 18 && recordOffset + recordLength <= fff.Length)
                {
                    var paletteRecord = fff.AsSpan(recordOffset, recordLength);
                    metadata.PaletteAboveColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 6);
                    metadata.PaletteBelowColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 9);
                    metadata.PaletteOverflowColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 12);
                    metadata.PaletteUnderflowColorYCrCb ??= ReadYCrCbTriplet(paletteRecord, 15);
                    metadata.Detector = "FLIR";
                }

                continue;
            }

            if (recordType != 0x002a)
            {
                continue;
            }

            if (recordOffset < 0 || recordLength < 16 || recordOffset + recordLength > fff.Length)
            {
                continue;
            }

            var record = fff.AsSpan(recordOffset, recordLength);
            metadata.Real2IR ??= BitConverter.ToSingle(record[..4]);
            metadata.OffsetX ??= BinaryPrimitives.ReadInt16LittleEndian(record.Slice(4, 2));
            metadata.OffsetY ??= BinaryPrimitives.ReadInt16LittleEndian(record.Slice(6, 2));
            metadata.PiPX1 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(8, 2));
            metadata.PiPX2 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(10, 2));
            metadata.PiPY1 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(12, 2));
            metadata.PiPY2 ??= BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(14, 2));
            metadata.Detector = "FLIR";
        }
    }

    private static int[]? ReadYCrCbTriplet(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || offset + 3 > source.Length)
        {
            return null;
        }

        return new[] { (int)source[offset], (int)source[offset + 1], (int)source[offset + 2] };
    }

    private static void TryExtractVisibleImage(string imagePath, RadiometricMetadata metadata, string? exifToolExe)
    {
        try
        {
            var targetDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermixStudio",
                "visible-cache");

            System.IO.Directory.CreateDirectory(targetDirectory);

            var baseName = Path.GetFileNameWithoutExtension(imagePath);
            var rawPath = Path.Combine(targetDirectory, $"{baseName}_visible_raw.jpg");
            var outputPath = Path.Combine(targetDirectory, $"{baseName}_visible.jpg");
            var sourceTimestamp = File.GetLastWriteTimeUtc(imagePath);
            var outputValid = IsDecodableImageFile(outputPath);
            var rawValid = IsDecodableImageFile(rawPath);
            var needsUpdate = !outputValid || File.GetLastWriteTimeUtc(outputPath) < sourceTimestamp;

            if (!outputValid && File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            if (!rawValid && File.Exists(rawPath))
            {
                File.Delete(rawPath);
            }

            if (needsUpdate)
            {
                var pythonVisiblePath = TryExtractVisibleImageWithPython(imagePath);
                if (!string.IsNullOrWhiteSpace(pythonVisiblePath) && File.Exists(pythonVisiblePath))
                {
                    metadata.VisibleImagePath = pythonVisiblePath;
                    return;
                }

                byte[]? visibleBytes = TryExtractEmbeddedVisibleFromFlirApp1(imagePath);
                visibleBytes ??= string.IsNullOrWhiteSpace(exifToolExe)
                    ? null
                    : RunProcessCaptureBinary(exifToolExe, $"-b -EmbeddedImage \"{imagePath}\"");

                if (visibleBytes is not null && visibleBytes.Length > 0 && IsDecodableImageBytes(visibleBytes))
                {
                    File.WriteAllBytes(rawPath, visibleBytes);

                    var enhancedVisible = EnhanceVisibleJpeg(visibleBytes);
                    if (IsDecodableImageBytes(enhancedVisible))
                    {
                        File.WriteAllBytes(outputPath, enhancedVisible);
                    }
                    else
                    {
                        File.WriteAllBytes(outputPath, visibleBytes);
                    }
                }
            }

            if (IsDecodableImageFile(outputPath))
            {
                metadata.VisibleImagePath = outputPath;
            }
            else if (IsDecodableImageFile(rawPath))
            {
                metadata.VisibleImagePath = rawPath;
            }
            else
            {
                metadata.VisibleImagePath = null;
            }
        }
        catch
        {
            // Visible image extraction is optional.
        }
    }

    private static string? TryExtractVisibleImageWithPython(string imagePath)
    {
        var scriptPath = ResolvePythonVisibleExtractorScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return null;
        }

        var absoluteImagePath = Path.GetFullPath(imagePath);
        var sidecarCandidates = ResolvePythonSidecarVisibleCandidates(absoluteImagePath).ToArray();
        foreach (var executable in ResolvePythonExecutables())
        {
            var arguments = executable.EndsWith("py.exe", StringComparison.OrdinalIgnoreCase)
                ? $"-3 \"{scriptPath}\" \"{absoluteImagePath}\" --json"
                : $"\"{scriptPath}\" \"{absoluteImagePath}\" --json";

            var output = RunProcessCapture(executable, arguments);
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            var json = TryExtractJsonObject(output);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                var root = JsonSerializer.Deserialize<JsonElement>(json);
                if (!root.TryGetProperty("status", out var status)
                    || !string.Equals(status.GetString(), "sucesso", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root.TryGetProperty("caminho_visivel_original", out var originalPathElement))
                {
                    var originalPath = originalPathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
                    {
                        return originalPath;
                    }
                }

                if (root.TryGetProperty("caminho_visivel", out var visiblePathElement))
                {
                    var visiblePath = visiblePathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(visiblePath) && File.Exists(visiblePath))
                    {
                        return visiblePath;
                    }
                }
            }
            catch
            {
                // Try next invocation.
            }

            foreach (var sidecarPath in sidecarCandidates)
            {
                if (File.Exists(sidecarPath))
                {
                    return sidecarPath;
                }
            }
        }

        foreach (var sidecarPath in sidecarCandidates)
        {
            if (File.Exists(sidecarPath))
            {
                return sidecarPath;
            }
        }

        return null;
    }

    private static IEnumerable<string> ResolvePythonSidecarVisibleCandidates(string imagePath)
    {
        var imageDirectory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrWhiteSpace(imageDirectory))
        {
            yield break;
        }

        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        var candidates = new[]
        {
            Path.Combine(imageDirectory, $"{baseName}_visivel_original.jpg"),
            Path.Combine(imageDirectory, $"{baseName}_visible_original.jpg"),
            Path.Combine(imageDirectory, $"{baseName}_visivel.jpg"),
            Path.Combine(imageDirectory, $"{baseName}_visible.jpg")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> ResolvePythonExecutables()
    {
        var candidates = new List<string>
        {
            "py",
            "python",
            @"C:\\Windows\\py.exe",
            @"C:\\Windows\\System32\\py.exe",
            @"C:\\Python311\\python.exe",
            @"C:\\Python310\\python.exe",
            @"C:\\Python39\\python.exe"
        };

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Launcher", "py.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Python310", "python.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "Python", "Python39", "python.exe"));
            }
        }
        catch
        {
            // Ignore environment probing failures.
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsExecutableAvailable(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool IsExecutableAvailable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        if (Path.IsPathRooted(executable))
        {
            return File.Exists(executable);
        }

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var hasExtension = Path.HasExtension(executable);

            foreach (var folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var basePath = Path.Combine(folder.Trim(), executable);
                if (hasExtension)
                {
                    if (File.Exists(basePath))
                    {
                        return true;
                    }

                    continue;
                }

                foreach (var ext in extensions)
                {
                    var fullPath = basePath + ext;
                    if (File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string? ResolvePythonVisibleExtractorScriptPath()
    {
        // 1) Diretório temporário do usuário (extração lazy de recurso embutido)
        var tempScriptPath = EnsureEmbeddedToolInTemp(
            "ThermixStudio.App.Embedded.extrair_imagens_flir.py",
            "thermix_extrair_imagens_flir.py");
        if (!string.IsNullOrWhiteSpace(tempScriptPath) && File.Exists(tempScriptPath))
        {
            return tempScriptPath;
        }

        var packagedToolPath = Path.Combine(AppContext.BaseDirectory, "tools", "extrair_imagens_flir.py");
        if (File.Exists(packagedToolPath))
        {
            return packagedToolPath;
        }

        var localToolPath = Path.Combine(AppContext.BaseDirectory, "extrair_imagens_flir.py");
        if (File.Exists(localToolPath))
        {
            return localToolPath;
        }

        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "extrair_imagens_flir.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? EnsureEmbeddedToolInTemp(string resourceName, string outputFileName)
    {
        try
        {
            var outputPath = Path.Combine(Path.GetTempPath(), outputFileName);
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                return outputPath;
            }

            using var stream = typeof(ThermalAnalysisService).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.CopyTo(fs);
            return outputPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractJsonObject(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static byte[]? TryExtractEmbeddedVisibleFromFlirApp1(string imagePath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(imagePath);
        }
        catch
        {
            return null;
        }

        var chunks = new SortedDictionary<byte, byte[]>();
        var index = 0;

        while (index + 4 < bytes.Length)
        {
            if (bytes[index] == 0xFF && bytes[index + 1] == 0xE1)
            {
                var segmentLength = (bytes[index + 2] << 8) | bytes[index + 3];
                if (segmentLength < 10)
                {
                    break;
                }

                var segmentEnd = index + 2 + segmentLength;
                if (segmentEnd > bytes.Length)
                {
                    break;
                }

                var contentStart = index + 4;
                var isFlirChunk = contentStart + 8 <= bytes.Length
                    && bytes[contentStart] == (byte)'F'
                    && bytes[contentStart + 1] == (byte)'L'
                    && bytes[contentStart + 2] == (byte)'I'
                    && bytes[contentStart + 3] == (byte)'R'
                    && bytes[contentStart + 4] == 0x00;

                if (isFlirChunk)
                {
                    var chunkNumber = bytes[contentStart + 6];
                    var payloadStart = contentStart + 8;
                    var payloadLength = segmentEnd - payloadStart;
                    if (payloadLength > 0)
                    {
                        var payload = new byte[payloadLength];
                        Buffer.BlockCopy(bytes, payloadStart, payload, 0, payloadLength);
                        chunks[chunkNumber] = payload;
                    }
                }

                index = segmentEnd;
                continue;
            }

            index++;
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        using var fffStream = new MemoryStream();
        foreach (var chunk in chunks.Values)
        {
            fffStream.Write(chunk, 0, chunk.Length);
        }

        var fff = fffStream.ToArray();
        if (fff.Length < 64)
        {
            return null;
        }

        var hasValidSignature = fff[0] == (byte)'F' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00
            || fff[0] == (byte)'A' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00;
        if (!hasValidSignature)
        {
            return null;
        }

        var recordDirectoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(24, 4));
        var recordCount = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(28, 4));

        if (recordDirectoryOffset <= 0 || recordCount <= 0)
        {
            return null;
        }

        for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
        {
            var entryOffset = recordDirectoryOffset + (recordIndex * 32);
            if (entryOffset + 20 > fff.Length)
            {
                break;
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(fff.AsSpan(entryOffset, 2));
            if (recordType != 14)
            {
                continue;
            }

            var recordOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 12, 4));
            var recordLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOffset + 16, 4));
            var jpegOffset = recordOffset + 32;

            if (jpegOffset <= 0 || recordLength <= 0 || jpegOffset + recordLength > fff.Length)
            {
                continue;
            }

            var jpeg = new byte[recordLength];
            Buffer.BlockCopy(fff, jpegOffset, jpeg, 0, recordLength);

            if (jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
            {
                return jpeg;
            }
        }

        // Some FLIR models do not expose the visible image with record type 14.
        // Fallback: search for JPEG signatures directly in the assembled FLIR block.
        var recoveredFromFff = TryExtractLargestJpegBySignature(fff);
        if (recoveredFromFff is not null)
        {
            return recoveredFromFff;
        }

        // Last-chance fallback: search in full source bytes.
        return TryExtractLargestJpegBySignature(bytes);

    }
    private static byte[]? TryExtractLargestJpegBySignature(ReadOnlySpan<byte> source)
    {
        const byte soi0 = 0xFF;
        const byte soi1 = 0xD8;
        const byte eoi0 = 0xFF;
        const byte eoi1 = 0xD9;

        byte[]? best = null;
        var bestScore = -1;
        var i = 0;

        while (i + 1 < source.Length)
        {
            if (source[i] != soi0 || source[i + 1] != soi1)
            {
                i++;
                continue;
            }

            var start = i;
            i += 2;

            while (i + 1 < source.Length)
            {
                if (source[i] == eoi0 && source[i + 1] == eoi1)
                {
                    var end = i + 2;
                    var length = end - start;
                    if (length > 1024)
                    {
                        var candidate = source.Slice(start, length).ToArray();
                        if (TryGetImageScore(candidate, out var score) && score > bestScore)
                        {
                            best = candidate;
                            bestScore = score;
                        }
                        else if (score == bestScore && best is not null && candidate.Length > best.Length)
                        {
                            best = candidate;
                        }
                    }

                    i = end;
                    break;
                }

                i++;
            }
        }

        return best;
    }

    private static bool IsDecodableImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return IsDecodableImageBytes(bytes);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDecodableImageBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return false;
        }

        try
        {
            using var img = Cv2.ImDecode(bytes, ImreadModes.Color);
            return !img.Empty() && img.Width > 0 && img.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetImageScore(byte[] bytes, out int score)
    {
        score = 0;
        try
        {
            using var img = Cv2.ImDecode(bytes, ImreadModes.Color);
            if (img.Empty() || img.Width <= 0 || img.Height <= 0)
            {
                return false;
            }

            score = img.Width * img.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] EnhanceVisibleJpeg(byte[] jpegBytes)
    {
        try
        {
            using var input = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
            if (input.Empty())
            {
                return jpegBytes;
            }

            using var enhanced = new Mat();
            input.CopyTo(enhanced);

            using var gray = new Mat();
            Cv2.CvtColor(enhanced, gray, ColorConversionCodes.BGR2GRAY);
            var mean = Cv2.Mean(gray).Val0;

            if (mean <= 10.0)
            {
                ApplyOffsetGammaInPlace(enhanced, offset: 8.0, gamma: 0.22);
                ApplyClaheOnLuminanceInPlace(enhanced, clipLimit: 2.5, new Size(8, 8));
            }
            else if (mean <= 60.0)
            {
                Cv2.Normalize(enhanced, enhanced, 0, 255, NormTypes.MinMax);
                ApplyClaheOnLuminanceInPlace(enhanced, clipLimit: 3.0, new Size(8, 8));
            }
            else
            {
                ApplyClaheOnLuminanceInPlace(enhanced, clipLimit: 2.0, new Size(8, 8));
            }

            if (Cv2.ImEncode(".jpg", enhanced, out var encoded, new[]
                {
                    (int)ImwriteFlags.JpegQuality,
                    95
                }))
            {
                return encoded;
            }

            return jpegBytes;
        }
        catch
        {
            return jpegBytes;
        }
    }

    private static void ApplyOffsetGammaInPlace(Mat image, double offset, double gamma)
    {
        using var work = new Mat();
        image.ConvertTo(work, MatType.CV_32FC3);

        Cv2.Add(work, new Scalar(offset, offset, offset), work);
        Cv2.Divide(work, new Scalar(255.0 + offset, 255.0 + offset, 255.0 + offset), work);
        Cv2.Pow(work, gamma, work);
        Cv2.Multiply(work, new Scalar(255.0, 255.0, 255.0), work);

        work.ConvertTo(image, MatType.CV_8UC3);
    }

    private static void ApplyClaheOnLuminanceInPlace(Mat image, double clipLimit, Size tileSize)
    {
        using var lab = new Mat();
        Cv2.CvtColor(image, lab, ColorConversionCodes.BGR2Lab);

        using var l = new Mat();
        using var a = new Mat();
        using var b = new Mat();
        Cv2.ExtractChannel(lab, l, 0);
        Cv2.ExtractChannel(lab, a, 1);
        Cv2.ExtractChannel(lab, b, 2);

        using var clahe = Cv2.CreateCLAHE(clipLimit, tileSize);
        using var lEnhanced = new Mat();
        clahe.Apply(l, lEnhanced);

        Cv2.InsertChannel(lEnhanced, lab, 0);
        Cv2.InsertChannel(a, lab, 1);
        Cv2.InsertChannel(b, lab, 2);

        Cv2.CvtColor(lab, image, ColorConversionCodes.Lab2BGR);
    }

    private static string? ResolveExifToolPath()
    {
        var localTool = Path.Combine(AppContext.BaseDirectory, "tools", "exiftool.exe");
        if (File.Exists(localTool))
        {
            return localTool;
        }

        var localRootTool = Path.Combine(AppContext.BaseDirectory, "exiftool.exe");
        if (File.Exists(localRootTool))
        {
            return localRootTool;
        }

        var workspaceTool = Path.Combine(AppContext.BaseDirectory, ".venv", "Lib", "site-packages", "dji_executables", "dji_thermal_sdk_v1.7", "exiftool-12.35.exe");
        if (File.Exists(workspaceTool))
        {
            return workspaceTool;
        }

        var tempEmbeddedExifTool = EnsureEmbeddedToolInTemp(
            "ThermixStudio.App.Embedded.exiftool.exe",
            "thermix_exiftool.exe");
        if (!string.IsNullOrWhiteSpace(tempEmbeddedExifTool) && File.Exists(tempEmbeddedExifTool))
        {
            return tempEmbeddedExifTool;
        }

        return IsExecutableAvailable("exiftool") ? "exiftool" : null;
    }

    private static string? RunProcessCapture(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(6000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                return output;
            }

            return string.IsNullOrWhiteSpace(error) ? null : error;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? RunProcessCaptureBinary(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var memory = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(memory);
            process.WaitForExit(6000);
            return process.ExitCode == 0 ? memory.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasPlanckCalibration(RadiometricMetadata metadata)
    {
        return metadata.PlanckR1.HasValue
            && metadata.PlanckR2.HasValue
            && metadata.PlanckB.HasValue
            && metadata.PlanckF.HasValue
            && metadata.PlanckO.HasValue;
    }

    private static void LoadFromByteFallback(Mat source, ThermalImageData destination)
    {
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var intensity = source.At<byte>(y, x);
                destination.Temperatures[y, x] = MinTempFallback + (intensity / 255.0) * (MaxTempFallback - MinTempFallback);
            }
        }
    }

    private static void LoadFromUShortFallback(Mat source, ThermalImageData destination)
    {
        ushort min = ushort.MaxValue;
        ushort max = ushort.MinValue;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }
        }

        var range = Math.Max(1, max - min);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                var normalized = (value - min) / (double)range;
                destination.Temperatures[y, x] = MinTempFallback + normalized * (MaxTempFallback - MinTempFallback);
            }
        }
    }

    /// <summary>
    /// Carrega temperaturas usando a escala real extraída dos metadados FLIR/ImageTemperatureMin/Max.
    /// Esta é a forma MAIS PRECISA de mapeamento, pois cada imagem tem sua própria escala térmica.
    /// 
    /// Exemplo FLIR E8xt:
    ///   - ImageTemperatureMin: 295K → 21.4°C
    ///   - ImageTemperatureMax: 318K → 44.7°C
    ///   - Pixel 48 (min) → 21.4°C
    ///   - Pixel 65335 (max) → 44.7°C
    /// </summary>
    private static void LoadFromUShortWithActualScale(Mat source, ThermalImageData destination, RadiometricMetadata metadata)
    {
        // Garantir que temos os valores reais
        if (!metadata.PaletteScaleMinC.HasValue || !metadata.PaletteScaleMaxC.HasValue)
        {
            // Fallback se não tiver escala real
            LoadFromUShortFallback(source, destination);
            return;
        }

        var scaleMinC = metadata.PaletteScaleMinC.Value;
        var scaleMaxC = metadata.PaletteScaleMaxC.Value;

        // Encontrar min/max dos pixels nesta imagem específica
        ushort pixelMin = ushort.MaxValue;
        ushort pixelMax = ushort.MinValue;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                if (value < pixelMin)
                    pixelMin = value;
                if (value > pixelMax)
                    pixelMax = value;
            }
        }

        // Garantir range válido
        var pixelRange = Math.Max(1, pixelMax - pixelMin);
        var tempRange = Math.Max(0.01, scaleMaxC - scaleMinC);

        // Mapear cada pixel para a escala REAL da imagem
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixelValue = source.At<ushort>(y, x);
                
                // Normalizar o pixel (0.0 a 1.0)
                var normalized = (pixelValue - pixelMin) / (double)pixelRange;
                
                // Mapear para a escala REAL em °C
                destination.Temperatures[y, x] = scaleMinC + (normalized * tempRange);
            }
        }
    }

    private static void LoadFromUShortWithPlanck(Mat source, ThermalImageData destination, RadiometricMetadata metadata)
    {
        var r1 = metadata.PlanckR1!.Value;
        var r2 = metadata.PlanckR2!.Value;
        var b = metadata.PlanckB!.Value;
        var f = metadata.PlanckF!.Value;
        var o = metadata.PlanckO!.Value;

        var emissivity = Math.Clamp(metadata.Emissivity ?? 0.95, 0.01, 1.0);
        destination.Metadata.Emissivity = emissivity;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var raw = source.At<ushort>(y, x);
                var correctedRaw = Math.Max(1.0, raw / emissivity);
                var denominator = r2 * (correctedRaw + o);
                var lnInput = (r1 / Math.Max(denominator, 0.000001)) + f;
                var tempK = b / Math.Log(Math.Max(lnInput, 1.000001));
                destination.Temperatures[y, x] = tempK - 273.15;
            }
        }
    }

    /// <summary>
    /// Tenta carregar temperatura com calibração Planck.
    /// Se os valores forem inválidos, retorna false para que fallback seja usado.
    /// </summary>
    private static bool TryLoadFromUShortWithPlanck(Mat source, ThermalImageData destination, RadiometricMetadata metadata)
    {
        destination.Metadata.Emissivity = Math.Clamp(metadata.Emissivity ?? 0.95, 0.01, 1.0);

        if (TryLoadPlanckCandidate(source, destination, metadata, useByteSwap: false))
        {
            return true;
        }

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

    private static bool TryLoadPlanckCandidate(Mat source, ThermalImageData destination, RadiometricMetadata metadata, bool useByteSwap)
    {
        var r1 = metadata.PlanckR1!.Value;
        var r2 = metadata.PlanckR2!.Value;
        var b = metadata.PlanckB!.Value;
        var f = metadata.PlanckF!.Value;
        var o = metadata.PlanckO!.Value;
        var emissivity = Math.Clamp(metadata.Emissivity ?? 0.95, 0.01, 1.0);

        double minTemp = double.MaxValue;
        double maxTemp = double.MinValue;
        int errorCount = 0;
        const int MaxAllowedErrors = 10;
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
                        var correctedRaw = Math.Max(1.0, raw / emissivity);
                        var denominator = r2 * (correctedRaw + o);
                        var lnInput = (r1 / Math.Max(denominator, 0.000001)) + f;

                        if (lnInput <= 0)
                        {
                            errorCount++;
                            if (errorCount > MaxAllowedErrors)
                                return false;
                            continue;
                        }

                        var tempK = b / Math.Log(Math.Max(lnInput, 1.000001));
                        var tempC = tempK - 273.15;

                        if (double.IsNaN(tempC) || double.IsInfinity(tempC))
                        {
                            errorCount++;
                            if (errorCount > MaxAllowedErrors)
                                return false;
                            continue;
                        }

                        candidate[y, x] = tempC;
                        minTemp = Math.Min(minTemp, tempC);
                        maxTemp = Math.Max(maxTemp, tempC);
                    }
                    catch
                    {
                        errorCount++;
                        if (errorCount > MaxAllowedErrors)
                            return false;
                    }
                }
            }

            if (minTemp < -100 || maxTemp > 200)
            {
                return false;
            }

            if (errorCount > MaxAllowedErrors / 2)
            {
                return false;
            }

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
        {
            for (var x = 0; x < source.Width; x++)
            {
                var value = source.At<ushort>(y, x);
                swapped.Set(y, x, BinaryPrimitives.ReverseEndianness(value));
            }
        }

        return swapped;
    }

    private static double? TryParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (string.IsNullOrWhiteSpace(digits))
        {
            return null;
        }

        if (double.TryParse(digits.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int[]? TryParseColorTriplet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var matches = System.Text.RegularExpressions.Regex.Matches(value, @"-?\d+");
        if (matches.Count < 3)
        {
            return null;
        }

        var color = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(matches[i].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var component))
            {
                return null;
            }

            color[i] = Math.Clamp(component, 0, 255);
        }

        return color;
    }

    private static ThermalStatistics CalculateStats(ThermalImageData image, int startX, int startY, int endX, int endY)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;

        for (var row = startY; row < endY; row++)
        {
            for (var col = startX; col < endX; col++)
            {
                var value = image.Temperatures[row, col];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                count++;
            }
        }

        if (count == 0)
        {
            return new ThermalStatistics();
        }

        return new ThermalStatistics
        {
            Tmin = min,
            Tmax = max,
            Tavg = sum / count
        };
    }

    /// <summary>
    /// Extrai dados de paleta embutida da imagem FLIR JPG (APP1 / FFF segment).
    /// Paletas FLIR são armazenadas como YCbCr; converte para BGRA.
    /// Retorna 1024 bytes (256 × BGRA) ou null se não encontrada.
    /// </summary>
    private static byte[]? TryExtractEmbeddedPaletteFromFlirApp1(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath)) return null;
            var bytes = File.ReadAllBytes(imagePath);
            if (bytes.Length < 100) return null;

            // Coletar chunks APP1 com header "FLIR\0"
            var chunks = new SortedDictionary<byte, byte[]>();
            var index = 2; // pula SOI
            while (index < bytes.Length - 8)
            {
                if (bytes[index] != 0xFF) { index++; continue; }
                var marker = bytes[index + 1];
                if (index + 3 >= bytes.Length) break;
                var segLen = (bytes[index + 2] << 8) | bytes[index + 3];
                var segEnd = index + 2 + segLen;
                if (segEnd > bytes.Length) break;

                if (marker == 0xE1 && segLen > 12)
                {
                    var cs = index + 4;
                    var isFlir = cs + 8 <= bytes.Length
                        && bytes[cs] == (byte)'F' && bytes[cs + 1] == (byte)'L'
                        && bytes[cs + 2] == (byte)'I' && bytes[cs + 3] == (byte)'R'
                        && bytes[cs + 4] == 0x00;

                    if (isFlir)
                    {
                        var chunkNum = bytes[cs + 6];
                        var payStart = cs + 8;
                        var payLen = segEnd - payStart;
                        if (payLen > 0)
                        {
                            var payload = new byte[payLen];
                            Buffer.BlockCopy(bytes, payStart, payload, 0, payLen);
                            chunks[chunkNum] = payload;
                        }
                    }
                }

                index = segEnd;
            }

            if (chunks.Count == 0) return null;

            using var fffStream = new MemoryStream();
            foreach (var chunk in chunks.Values) fffStream.Write(chunk, 0, chunk.Length);
            var fff = fffStream.ToArray();
            if (fff.Length < 64) return null;

            // Verificar assinatura FFF
            var hasSig = (fff[0] == (byte)'F' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00)
                      || (fff[0] == (byte)'A' && fff[1] == (byte)'F' && fff[2] == (byte)'F' && fff[3] == 0x00);
            if (!hasSig) return null;

            var dirOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(24, 4));
            var recordCount = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(28, 4));
            if (dirOffset <= 0 || recordCount <= 0) return null;

            // Procurar record type 6 (Palette) no diretório FFF
            for (var ri = 0; ri < recordCount; ri++)
            {
                var entryOff = dirOffset + ri * 32;
                if (entryOff + 20 > fff.Length) break;

                var recType = BinaryPrimitives.ReadUInt16BigEndian(fff.AsSpan(entryOff, 2));
                if (recType != 6) continue; // record type 6 = palette

                var recOff = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOff + 12, 4));
                var recLen = (int)BinaryPrimitives.ReadUInt32BigEndian(fff.AsSpan(entryOff + 16, 4));

                if (recOff <= 0 || recLen < 256 * 3 || recOff + recLen > fff.Length) continue;

                // Paleta YCbCr: 256 × 3 bytes
                var bgraLut = new byte[256 * 4];
                for (var i = 0; i < 256; i++)
                {
                    var yy  = fff[recOff + i * 3];
                    // NOTE: FLIR stores palette as [Y, Cr, Cb] — Cr comes BEFORE Cb (opposite of standard JPEG)
                    var cr  = fff[recOff + i * 3 + 1]; // FLIR: byte1 = Cr (red-difference)
                    var cb  = fff[recOff + i * 3 + 2]; // FLIR: byte2 = Cb (blue-difference)

                    // ITU-R BT.601 YCbCr → RGB
                    var r = Math.Clamp(yy + 1.402  * (cr - 128), 0, 255);
                    var g = Math.Clamp(yy - 0.344  * (cb - 128) - 0.714 * (cr - 128), 0, 255);
                    var b = Math.Clamp(yy + 1.772  * (cb - 128), 0, 255);

                    bgraLut[i * 4]     = (byte)b;
                    bgraLut[i * 4 + 1] = (byte)g;
                    bgraLut[i * 4 + 2] = (byte)r;
                    bgraLut[i * 4 + 3] = 255;
                }

                // Validar variação mínima na paleta
                byte minY = 255, maxY = 0;
                for (var i = 0; i < 256; i++) { var v = fff[recOff + i * 3]; if (v < minY) minY = v; if (v > maxY) maxY = v; }
                if (maxY - minY < 20) continue; // paleta trivial, ignorar

                return bgraLut;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Brand Detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Detecta a marca da câmera lendo o EXIF Make/Model via MetadataExtractor.
    /// Para arquivos IS2 o resultado é sempre Fluke. Rápido — lê só o cabeçalho.
    /// </summary>
    public ThermalCameraBrand DetectCameraBrand(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".is2")  return ThermalCameraBrand.Fluke;
            if (ext == ".irg")  return ThermalCameraBrand.InfiRay;
            if (ext == ".rjpeg") return ThermalCameraBrand.InfiRay;

            var directories = ImageMetadataReader.ReadMetadata(filePath);
            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    if (!tag.Name.Equals("Make", StringComparison.OrdinalIgnoreCase) &&
                        !tag.Name.Equals("Model", StringComparison.OrdinalIgnoreCase)) continue;
                    var val = (tag.Description ?? string.Empty).ToUpperInvariant();
                    if (val.Contains("FLIR"))                                  return ThermalCameraBrand.Flir;
                    if (val.Contains("FLUKE"))                                 return ThermalCameraBrand.Fluke;
                    if (val.Contains("HIKVISION") || val.Contains("HIKMICRO")) return ThermalCameraBrand.Hikvision;
                    if (val.Contains("INFIRAY"))                               return ThermalCameraBrand.InfiRay;
                    if (val.Contains("GUIDE"))                                 return ThermalCameraBrand.Guide;
                    if (val.Contains("BOSCH"))                                 return ThermalCameraBrand.Bosch;
                    if (val.Contains("SEEK"))                                  return ThermalCameraBrand.Seek;
                    if (val.Contains("TESTO"))                                 return ThermalCameraBrand.Testo;
                }

                // FLIR embute diretório próprio no APP1
                if (dir.Name.Contains("FLIR", StringComparison.OrdinalIgnoreCase))
                    return ThermalCameraBrand.Flir;
            }
        }
        catch { /* arquivo inválido — retorna Unknown */ }

        return ThermalCameraBrand.Unknown;
    }

    // ─── Fluke IS2 Loader ─────────────────────────────────────────────────────

    /// <summary>
    /// Carrega termograma Fluke no formato IS2 (container ZIP).
    /// Extrai a matriz de temperaturas e metadados do XML de calibração.
    /// </summary>
    private static ThermalImageData LoadFlukeIs2(string imagePath)
    {
        var data = new ThermalImageData
        {
            SourceFormat = "IS2",
            Metadata = new RadiometricMetadata { Manufacturer = "Fluke", Detector = "Fluke" }
        };

        try
        {
            using var archive = ZipFile.OpenRead(imagePath);

            // 1) Localizar XML de calibração (meta.xml, metadata.xml, CameraInformation.xml…)
            var xmlEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            int width = 0, height = 0;
            double minTemp = 20.0, maxTemp = 120.0;
            string rawEntryName = string.Empty;
            string? visEntryName = null;

            if (xmlEntry is not null)
            {
                using var xmlStream = xmlEntry.Open();
                var doc = XDocument.Load(xmlStream);

                // Namespace-ignorante: buscar todos os elementos pelo LocalName
                var elements = doc.Descendants().ToDictionary(
                    e => e.Name.LocalName,
                    e => e.Value,
                    StringComparer.OrdinalIgnoreCase);

                width  = TryGetIntXml(elements, "Width", "IRWidth", "imageWidth");
                height = TryGetIntXml(elements, "Height", "IRHeight", "imageHeight");
                minTemp = TryGetDoubleXml(elements, "MinObjectTemp", "MinTemp", "TempMin") ?? 20.0;
                maxTemp = TryGetDoubleXml(elements, "MaxObjectTemp", "MaxTemp", "TempMax") ?? 120.0;
                data.Metadata.Emissivity = TryGetDoubleXml(elements, "Emissivity");
                data.Metadata.AmbientTemperatureC = TryGetDoubleXml(elements, "AmbientTemp", "AtmosphericTemp");
                data.Metadata.ObjectDistanceM = TryGetDoubleXml(elements, "ObjectDistance", "Distance");
                data.Metadata.CameraModel = TryGetStringXml(elements, "Camera", "Model", "CameraModel") ?? "Fluke";

                rawEntryName = TryGetStringXml(elements, "IRFileName", "RawFileName") ?? string.Empty;
                visEntryName = TryGetStringXml(elements, "ColorFileName", "VisFileName", "VisibleFileName");
            }

            // 2) Localizar entrada raw (16-bit IR) — comum: ir.raw, raw.raw, data.raw
            ZipArchiveEntry? rawEntry = null;
            if (!string.IsNullOrWhiteSpace(rawEntryName))
                rawEntry = archive.GetEntry(rawEntryName);
            rawEntry ??= archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase));

            if (rawEntry is null)
                throw new InvalidOperationException("Arquivo IS2 não contém dados IR (.raw).");

            using var rawStream = rawEntry.Open();
            using var ms = new MemoryStream();
            rawStream.CopyTo(ms);
            var rawBytes = ms.ToArray();

            // Inferir dimensões se o XML não as forneceu
            var totalPixels = rawBytes.Length / 2;
            if (width == 0 || height == 0)
                InferIs2Dimensions(totalPixels, ref width, ref height);

            if (width == 0 || height == 0 || width * height * 2 > rawBytes.Length)
                throw new InvalidOperationException($"Dimensões IS2 inválidas: {width}×{height}.");

            // 3) Converter raw 16-bit → temperatura em °C
            // Fluke IS2: valores em decikelvin (0.1 K) → T_C = raw/10 − 273.15
            // Fallback: interpolação linear pelo range do XML se os valores brutos
            //           estiverem fora da faixa esperada de deciKelvin (1800–5500).
            data.Width = width;
            data.Height = height;
            data.Temperatures = new double[height, width];
            data.IsRadiometricLikely = true;

            ushort rawMin = ushort.MaxValue, rawMax = 0;
            var span = new ReadOnlySpan<byte>(rawBytes, 0, width * height * 2);
            for (var i = 0; i < width * height; i++)
            {
                var raw = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2, 2));
                if (raw < rawMin) rawMin = raw;
                if (raw > rawMax) rawMax = raw;
            }

            // Heurística: se rawMin ≥ 1500 presume-se decikelvin (0 K ≈ 0, 25°C ≈ 2981)
            var useDeciKelvin = rawMin >= 1500 && rawMax <= 8000;

            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var idx = row * width + col;
                    var raw = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(idx * 2, 2));
                    data.Temperatures[row, col] = useDeciKelvin
                        ? raw / 10.0 - 273.15
                        : minTemp + (rawMax > rawMin ? (double)(raw - rawMin) / (rawMax - rawMin) : 0.0) * (maxTemp - minTemp);
                }
            }

            data.Metadata.Notes = useDeciKelvin
                ? "Fluke IS2 — temperatura convertida de decikelvin."
                : "Fluke IS2 — temperatura interpolada pelo range do XML.";

            // 4) Extrair imagem visível embutida (se houver)
            ZipArchiveEntry? visEntry = null;
            if (!string.IsNullOrWhiteSpace(visEntryName))
                visEntry = archive.GetEntry(visEntryName);
            visEntry ??= archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

            if (visEntry is not null)
            {
                var visDir  = Path.GetDirectoryName(imagePath)!;
                var visPath = Path.Combine(visDir, Path.GetFileNameWithoutExtension(imagePath) + "_vis" + Path.GetExtension(visEntry.Name));
                if (!File.Exists(visPath))
                    visEntry.ExtractToFile(visPath, overwrite: false);
                data.Metadata.VisibleImagePath = visPath;
            }
        }
        catch (Exception ex)
        {
            data.Metadata.Notes = $"Erro ao carregar IS2: {ex.Message}";
            if (data.Width == 0 || data.Height == 0)
            {
                data.Width = 1; data.Height = 1;
                data.Temperatures = new double[1, 1];
            }
        }

        return data;
    }

    private static void InferIs2Dimensions(int totalPixels, ref int width, ref int height)
    {
        // Resoluções comuns dos modelos Fluke (largura × altura)
        int[][] common = [[640, 480], [320, 240], [320, 256], [160, 120], [384, 288], [1024, 768]];
        foreach (var dim in common)
        {
            if (dim[0] * dim[1] == totalPixels)
            {
                width  = dim[0];
                height = dim[1];
                return;
            }
        }
        // Fallback: quadrado aproximado
        width  = (int)Math.Sqrt(totalPixels);
        height = totalPixels / Math.Max(1, width);
    }

    private static int TryGetIntXml(Dictionary<string, string> el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetValue(k, out var v) && int.TryParse(v, out var i))
                return i;
        return 0;
    }

    private static double? TryGetDoubleXml(Dictionary<string, string> el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
        return null;
    }

    private static string? TryGetStringXml(Dictionary<string, string> el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    // ─── InfiRay / Guide / Generic OEM Loader ────────────────────────────────

    /// <summary>
    /// Carrega arquivos InfiRay (.irg/.rjpeg) e formatos OEM similares.
    /// Estratégia: tenta JPEG embutido com OpenCvSharp + fallback 16-bit raw.
    /// </summary>
    private static ThermalImageData LoadInfiRayFile(string imagePath)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        var data = new ThermalImageData
        {
            SourceFormat = ext.TrimStart('.').ToUpperInvariant(),
            Metadata = new RadiometricMetadata { Manufacturer = "InfiRay", Detector = "InfiRay" }
        };

        try
        {
            // .rjpeg é um JPEG válido com dados raw appended — lê como JPEG normal
            if (ext == ".rjpeg")
            {
                using var mat = Cv2.ImRead(imagePath, ImreadModes.AnyDepth | ImreadModes.Grayscale);
                if (mat.Empty()) throw new InvalidOperationException("rjpeg vazio.");
                FillDataFromMat(mat, data);
                data.Metadata.Notes = "InfiRay rJPEG — temperatura estimada por escala relativa.";
                return data;
            }

            // .irg: tentar como ZIP (algumas versões são containers)
            if (TryLoadIrgAsZip(imagePath, data)) return data;

            // Fallback: tentar como imagem bruta
            using var mat2 = Cv2.ImRead(imagePath, ImreadModes.AnyDepth | ImreadModes.Grayscale);
            if (!mat2.Empty())
            {
                FillDataFromMat(mat2, data);
                data.Metadata.Notes = "InfiRay IRG — temperatura estimada por escala relativa.";
                return data;
            }

            throw new InvalidOperationException("Não foi possível interpretar o arquivo InfiRay.");
        }
        catch (Exception ex)
        {
            data.Metadata.Notes = $"Erro ao carregar InfiRay: {ex.Message}";
            if (data.Width == 0) { data.Width = 1; data.Height = 1; data.Temperatures = new double[1, 1]; }
        }

        return data;
    }

    private static bool TryLoadIrgAsZip(string imagePath, ThermalImageData data)
    {
        try
        {
            using var archive = ZipFile.OpenRead(imagePath);
            var rawEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase));
            if (rawEntry is null) return false;

            using var ms = new MemoryStream();
            using var s = rawEntry.Open();
            s.CopyTo(ms);
            var rawBytes = ms.ToArray();
            var total = rawBytes.Length / 2;

            var width = 0; var height = 0;
            InferIs2Dimensions(total, ref width, ref height); // mesmas resoluções comuns

            if (width == 0 || height == 0) return false;

            data.Width = width; data.Height = height;
            data.Temperatures = new double[height, width];
            data.IsRadiometricLikely = false;

            var span = new ReadOnlySpan<byte>(rawBytes, 0, width * height * 2);
            ushort rawMin = ushort.MaxValue, rawMax = 0;
            for (var i = 0; i < width * height; i++)
            {
                var v = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2, 2));
                if (v < rawMin) rawMin = v;
                if (v > rawMax) rawMax = v;
            }

            var useDeciK = rawMin >= 1500 && rawMax <= 8000;
            for (var r = 0; r < height; r++)
                for (var c = 0; c < width; c++)
                {
                    var raw = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice((r * width + c) * 2, 2));
                    data.Temperatures[r, c] = useDeciK
                        ? raw / 10.0 - 273.15
                        : MinTempFallback + (rawMax > rawMin ? (double)(raw - rawMin) / (rawMax - rawMin) : 0) * (MaxTempFallback - MinTempFallback);
                }

            data.SourceFormat = "IRG";
            data.Metadata.Notes = "InfiRay IRG (ZIP) — temperatura por heurística decikelvin.";
            return true;
        }
        catch { return false; }
    }

    private static void FillDataFromMat(Mat mat, ThermalImageData data)
    {
        data.Width = mat.Width;
        data.Height = mat.Height;
        data.Temperatures = new double[mat.Height, mat.Width];

        if (mat.ElemSize() > 1)
        {
            for (var r = 0; r < mat.Height; r++)
                for (var c = 0; c < mat.Width; c++)
                    data.Temperatures[r, c] = MinTempFallback +
                        (mat.At<ushort>(r, c) / 65535.0) * (MaxTempFallback - MinTempFallback);
        }
        else
        {
            for (var r = 0; r < mat.Height; r++)
                for (var c = 0; c < mat.Width; c++)
                    data.Temperatures[r, c] = MinTempFallback +
                        (mat.At<byte>(r, c) / 255.0) * (MaxTempFallback - MinTempFallback);
        }
    }
}
