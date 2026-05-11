using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Buffers.Binary;
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

        var metadata = ExtractMetadata(imagePath);
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
                SourceFormat = extension.TrimStart('.').ToUpperInvariant(),
                Metadata = metadata,
                IsRadiometricLikely = metadata.PlanckR1.HasValue && metadata.PlanckR2.HasValue && metadata.PlanckB.HasValue
            };

            if (thermalSource.ElemSize() > 1)
            {
                if (HasPlanckCalibration(metadata))
                {
                    LoadFromUShortWithPlanck(thermalSource, data, metadata);
                    data.IsRadiometricLikely = true;
                    data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                        ? "Convertido com calibracao Planck extraida do EXIF/ExifTool."
                        : data.Metadata.Notes;
                }
                else
                {
                    LoadFromUShortFallback(thermalSource, data);
                    data.Metadata.Notes = string.IsNullOrWhiteSpace(data.Metadata.Notes)
                        ? "Sem coeficientes Planck; temperatura estimada por escala relativa."
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

                    if (name.Contains("Palette", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(metadata.PaletteName))
                    {
                        metadata.PaletteName = description;
                    }

                    if (directory.Name.Contains("FLIR", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata.Detector = "FLIR";
                    }
                }
            }
        }
        catch
        {
            metadata.Notes = "Nao foi possivel extrair metadados EXIF/XMP do arquivo.";
        }

        if (metadata.Detector == "Unknown" && (metadata.CameraModel.Contains("FLIR", StringComparison.OrdinalIgnoreCase) || metadata.Manufacturer.Contains("FLIR", StringComparison.OrdinalIgnoreCase)))
        {
            metadata.Detector = "FLIR";
        }

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
            metadata.PlanckR1 ??= ReadDouble(first, "PlanckR1");
            metadata.PlanckR2 ??= ReadDouble(first, "PlanckR2");
            metadata.PlanckB ??= ReadDouble(first, "PlanckB");
            metadata.PlanckF ??= ReadDouble(first, "PlanckF");
            metadata.PlanckO ??= ReadDouble(first, "PlanckO");
            metadata.PaletteName ??= ReadString(first, "Palette") ?? ReadString(first, "PaletteName");

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

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return prop.GetString();
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

        return null;
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
}
