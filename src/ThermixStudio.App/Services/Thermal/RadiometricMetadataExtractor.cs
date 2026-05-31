using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MetadataExtractor;
using ThermixStudio.Core;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Extrator de metadados radiométricos via MetadataExtractor (EXIF/XMP) 
/// e ExifTool (JSON numérico).
/// </summary>
internal static class RadiometricMetadataExtractor
{
    public static RadiometricMetadata ExtractMetadata(string imagePath)
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
                        metadata.CameraModel = description;

                    if (name.Contains("Make", StringComparison.OrdinalIgnoreCase) && metadata.Manufacturer == "Unknown")
                        metadata.Manufacturer = description;

                    if (name.Contains("Emissivity", StringComparison.OrdinalIgnoreCase))
                        metadata.Emissivity ??= TryParseDouble(description);

                    if (name.Contains("Humidity", StringComparison.OrdinalIgnoreCase))
                        metadata.RelativeHumidity ??= TryParseDouble(description);

                    if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                        metadata.ObjectDistanceM ??= TryParseDouble(description);

                    if (name.Contains("Reflected", StringComparison.OrdinalIgnoreCase))
                        metadata.ReflectedTemperatureC ??= TryParseDouble(description);

                    if (name.Contains("Ambient", StringComparison.OrdinalIgnoreCase))
                        metadata.AmbientTemperatureC ??= TryParseDouble(description);

                    var isSpotTemperatureTag =
                        name.Contains("Tspot", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tspor", StringComparison.OrdinalIgnoreCase) ||
                        (name.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains("Temp", StringComparison.OrdinalIgnoreCase));
                    if (isSpotTemperatureTag)
                        metadata.SpotTemperatureC ??= TryParseDouble(description);

                    var spotCoordinate = TryParseDouble(description);
                    if (spotCoordinate.HasValue &&
                        name.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("X", StringComparison.OrdinalIgnoreCase) &&
                        spotCoordinate.Value >= 0.0 && spotCoordinate.Value <= 1.0)
                        metadata.SpotNormalizedX ??= spotCoordinate.Value;

                    if (spotCoordinate.HasValue &&
                        name.Contains("Spot", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Y", StringComparison.OrdinalIgnoreCase) &&
                        spotCoordinate.Value >= 0.0 && spotCoordinate.Value <= 1.0)
                        metadata.SpotNormalizedY ??= spotCoordinate.Value;

                    if (name.Contains("Image Temperature Min", StringComparison.OrdinalIgnoreCase))
                    {
                        var scaleMin = TryParseDouble(description);
                        if (scaleMin.HasValue)
                            metadata.PaletteScaleMinC ??= NormalizeExifTemperatureToCelsius(scaleMin.Value);
                    }

                    if (name.Contains("Image Temperature Max", StringComparison.OrdinalIgnoreCase))
                    {
                        var scaleMax = TryParseDouble(description);
                        if (scaleMax.HasValue)
                            metadata.PaletteScaleMaxC ??= NormalizeExifTemperatureToCelsius(scaleMax.Value);
                    }

                    if (name.Contains("Palette Name", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(metadata.PaletteName))
                        metadata.PaletteName = description;

                    if (name.Contains("Above Color", StringComparison.OrdinalIgnoreCase))
                        metadata.PaletteAboveColorYCrCb ??= TryParseColorTriplet(description);
                    if (name.Contains("Below Color", StringComparison.OrdinalIgnoreCase))
                        metadata.PaletteBelowColorYCrCb ??= TryParseColorTriplet(description);
                    if (name.Contains("Overflow Color", StringComparison.OrdinalIgnoreCase))
                        metadata.PaletteOverflowColorYCrCb ??= TryParseColorTriplet(description);
                    if (name.Contains("Underflow Color", StringComparison.OrdinalIgnoreCase))
                        metadata.PaletteUnderflowColorYCrCb ??= TryParseColorTriplet(description);

                    if (directory.Name.Contains("FLIR", StringComparison.OrdinalIgnoreCase))
                        metadata.Detector = "FLIR";
                    if (directory.Name.Contains("Hikvision", StringComparison.OrdinalIgnoreCase)
                        || directory.Name.Contains("HikMicro", StringComparison.OrdinalIgnoreCase))
                        metadata.Detector = "Hikvision";
                }
            }
        }
        catch
        {
            metadata.Notes = "Nao foi possivel extrair metadados EXIF/XMP do arquivo.";
        }

        if (metadata.Detector == "Unknown")
        {
            var make = metadata.Manufacturer.ToUpperInvariant();
            var model = metadata.CameraModel.ToUpperInvariant();
            var combined = make + " " + model;

            if (combined.Contains("FLIR")) metadata.Detector = "FLIR";
            else if (combined.Contains("FLUKE")) metadata.Detector = "Fluke";
            else if (combined.Contains("HIKVISION") || combined.Contains("HIKMICRO")) metadata.Detector = "Hikvision";
            else if (combined.Contains("INFIRAY") || combined.Contains("IRG")) metadata.Detector = "InfiRay";
            else if (combined.Contains("GUIDE")) metadata.Detector = "Guide";
            else if (combined.Contains("BOSCH")) metadata.Detector = "Bosch";
            else if (combined.Contains("SEEK")) metadata.Detector = "Seek";
            else if (combined.Contains("TESTO")) metadata.Detector = "Testo";
        }

        FlirFffParser.ApplyAlignmentMetadata(imagePath, metadata);

        return metadata;
    }

    public static void TryApplyExifToolMetadata(string metadataJson, RadiometricMetadata metadata)
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(metadataJson);
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return;

            var first = root[0];

            metadata.CameraModel = ReadString(first, "Model") ?? metadata.CameraModel;
            metadata.Manufacturer = ReadString(first, "Make") ?? metadata.Manufacturer;
            metadata.Emissivity ??= ReadDouble(first, "Emissivity");
            metadata.ObjectDistanceM ??= ReadDouble(first, "ObjectDistance");
            metadata.RelativeHumidity ??= ReadDouble(first, "RelativeHumidity");
            metadata.ReflectedTemperatureC ??= ReadDouble(first, "ReflectedApparentTemperature");
            metadata.AmbientTemperatureC ??= ReadDouble(first, "AtmosphericTemperature");
            metadata.SpotTemperatureC ??= ReadFirstDouble(first,
                "SpotTemperature", "SpotMeterValue", "Spot1Temperature", "Spot1Value", "Tspot", "Tspor");

            var imageWidth = ReadFirstDouble(first, "RawThermalImageWidth", "ImageWidth", "ExifImageWidth");
            var imageHeight = ReadFirstDouble(first, "RawThermalImageHeight", "ImageHeight", "ExifImageHeight");
            metadata.SpotNormalizedX ??= NormalizeSpotCoordinate(
                ReadFirstDouble(first, "SpotX", "Spot1X", "SpotMeterX", "SpotMeterX1", "TspotX"), imageWidth);
            metadata.SpotNormalizedY ??= NormalizeSpotCoordinate(
                ReadFirstDouble(first, "SpotY", "Spot1Y", "SpotMeterY", "SpotMeterY1", "TspotY"), imageHeight);
            metadata.SpotLabel ??= ReadString(first, "Meas1Label");

            metadata.PlanckR1 ??= ReadDouble(first, "PlanckR1");
            metadata.PlanckR2 ??= ReadDouble(first, "PlanckR2");
            metadata.PlanckB ??= ReadDouble(first, "PlanckB");
            metadata.PlanckF ??= ReadDouble(first, "PlanckF");
            metadata.PlanckO ??= ReadDouble(first, "PlanckO");
            metadata.DetectedViewMode ??= ExifModeMapper.MapThermalImageType(ReadString(first, "ThermalImageType"));
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

            if (metadata.DetectedPalette is null && !string.IsNullOrWhiteSpace(metadata.PaletteName))
                metadata.DetectedPalette = MapExifPaletteNameToEnum(metadata.PaletteName);

            metadata.DateTimeOriginal ??= ReadString(first, "DateTimeOriginal") ?? ReadString(first, "CreateDate");
            metadata.CameraSerialNumber ??= ReadStringOrNumber(first, "CameraSerialNumber");
            metadata.FieldOfView ??= ReadDouble(first, "FieldOfView");
            metadata.IRWindowTemperatureC ??= ReadDouble(first, "IRWindowTemperature");
            metadata.IRWindowTransmission ??= ReadDouble(first, "IRWindowTransmission");
            metadata.Real2IR ??= ReadDouble(first, "Real2IR");
            metadata.OffsetX ??= ReadInt(first, "OffsetX");
            metadata.OffsetY ??= ReadInt(first, "OffsetY");
            metadata.PiPX1 ??= ReadInt(first, "PiPX1");
            metadata.PiPX2 ??= ReadInt(first, "PiPX2");
            metadata.PiPY1 ??= ReadInt(first, "PiPY1");
            metadata.PiPY2 ??= ReadInt(first, "PiPY2");
            metadata.CameraTemperatureMinClip ??= ReadDouble(first, "CameraTemperatureMinClip");
            metadata.CameraTemperatureMaxClip ??= ReadDouble(first, "CameraTemperatureMaxClip");

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
                metadata.Detector = "FLIR";
        }
        catch
        {
            // Keep prior metadata when ExifTool JSON parsing fails.
        }
    }

    /// <summary>
    /// Mapeia o nome da paleta extraído do EXIF para o enum ThermalPalette.
    /// </summary>
    public static ThermalPalette? MapExifPaletteNameToEnum(string? exifPaletteName)
    {
        if (string.IsNullOrWhiteSpace(exifPaletteName))
            return null;

        return exifPaletteName.ToLowerInvariant() switch
        {
            "iron" => ThermalPalette.Iron,
            "ironbow" => ThermalPalette.Iron,
            "gray" => ThermalPalette.Grayscale,
            "grey" => ThermalPalette.Grayscale,
            "grayscale" => ThermalPalette.Grayscale,
            "rainbow" => ThermalPalette.Rainbow,
            "hotmetal" => ThermalPalette.Hotmetal,
            "hotmetal 2" => ThermalPalette.Hotmetal,
            "arctic" => ThermalPalette.Arctic,
            "lava" => ThermalPalette.Arctic,
            _ => null
        };
    }

    // ─── JSON helpers ──────────────────────────────────────────────────────

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        return prop.GetString();
    }

    private static string? ReadStringOrNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;
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
            return null;

        if (prop.ValueKind == JsonValueKind.String)
            return TryParseColorTriplet(prop.GetString());

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
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var numeric))
            return numeric;

        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var textNumeric))
            return textNumeric;

        if (prop.ValueKind == JsonValueKind.String)
            return TryParseDouble(prop.GetString());

        return null;
    }

    private static double? ReadFirstDouble(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadDouble(element, propertyName);
            if (value.HasValue) return value;
        }
        return null;
    }

    internal static double? NormalizeSpotCoordinate(double? value, double? extent)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return null;

        var coordinate = value.Value;
        if (coordinate >= 0.0 && coordinate <= 1.0)
            return coordinate;

        if (extent.HasValue && double.IsFinite(extent.Value) && extent.Value > 1.0)
            return Math.Clamp(coordinate / Math.Max(1.0, extent.Value - 1.0), 0.0, 1.0);

        return null;
    }

    internal static double NormalizeExifTemperatureToCelsius(double value)
        => value > 200.0 ? value - 273.15 : value;

    // ─── General parsing helpers ──────────────────────────────────────────

    internal static double? TryParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = new string(value.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (string.IsNullOrWhiteSpace(digits))
            return null;

        if (double.TryParse(digits.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    internal static int[]? TryParseColorTriplet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var matches = Regex.Matches(value, @"-?\d+");
        if (matches.Count < 3)
            return null;

        var color = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(matches[i].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var component))
                return null;
            color[i] = Math.Clamp(component, 0, 255);
        }

        return color;
    }
}
