using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Parser para arquivos Fluke no formato IS2 (container ZIP).
/// Extrai a matriz de temperaturas e metadados do XML de calibração interno.
/// </summary>
internal static class FlukeIs2Parser
{
    private const double MinTempFallback = 20.0;
    private const double MaxTempFallback = 120.0;

    public static ThermalImageData Load(string imagePath)
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
            double minTemp = MinTempFallback, maxTemp = MaxTempFallback;
            string rawEntryName = string.Empty;
            string? visEntryName = null;

            if (xmlEntry is not null)
            {
                using var xmlStream = xmlEntry.Open();
                var doc = XDocument.Load(xmlStream);

                var elements = doc.Descendants().ToDictionary(
                    e => e.Name.LocalName,
                    e => e.Value,
                    StringComparer.OrdinalIgnoreCase);

                width = TryGetIntXml(elements, "Width", "IRWidth", "imageWidth");
                height = TryGetIntXml(elements, "Height", "IRHeight", "imageHeight");
                minTemp = TryGetDoubleXml(elements, "MinObjectTemp", "MinTemp", "TempMin") ?? MinTempFallback;
                maxTemp = TryGetDoubleXml(elements, "MaxObjectTemp", "MaxTemp", "TempMax") ?? MaxTempFallback;
                data.Metadata.Emissivity = TryGetDoubleXml(elements, "Emissivity");
                data.Metadata.AmbientTemperatureC = TryGetDoubleXml(elements, "AmbientTemp", "AtmosphericTemp");
                data.Metadata.ObjectDistanceM = TryGetDoubleXml(elements, "ObjectDistance", "Distance");
                data.Metadata.CameraModel = TryGetStringXml(elements, "Camera", "Model", "CameraModel") ?? "Fluke";

                rawEntryName = TryGetStringXml(elements, "IRFileName", "RawFileName") ?? string.Empty;
                visEntryName = TryGetStringXml(elements, "ColorFileName", "VisFileName", "VisibleFileName");
            }

            // 2) Localizar entrada raw (16-bit IR)
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
                InferDimensions(totalPixels, ref width, ref height);

            if (width == 0 || height == 0 || width * height * 2 > rawBytes.Length)
                throw new InvalidOperationException($"Dimensões IS2 inválidas: {width}×{height}.");

            // 3) Converter raw 16-bit → temperatura em °C
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
                var visDir = Path.GetDirectoryName(imagePath)!;
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

    internal static void InferDimensions(int totalPixels, ref int width, ref int height)
    {
        int[][] common = [[640, 480], [320, 240], [320, 256], [160, 120], [384, 288], [1024, 768]];
        foreach (var dim in common)
        {
            if (dim[0] * dim[1] == totalPixels)
            {
                width = dim[0];
                height = dim[1];
                return;
            }
        }
        width = (int)Math.Sqrt(totalPixels);
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
}
