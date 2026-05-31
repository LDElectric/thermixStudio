using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using OpenCvSharp;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Extrator de imagem visível de termogramas FLIR.
/// Cobre: Python sidecar, ExifTool, FlirFffParser, e cache/enhance OpenCV.
/// </summary>
internal static class FlirVisibleImageExtractor
{
    /// <summary>
    /// Tenta extrair a imagem visível associada ao termograma e popula
    /// <c>metadata.VisibleImagePath</c> com o caminho do arquivo de cache.
    /// </summary>
    public static async Task TryExtractVisibleImageAsync(
        string imagePath,
        RadiometricMetadata metadata,
        IExifToolService exifTool,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var targetDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermixStudio",
                "visible-cache");

            Directory.CreateDirectory(targetDirectory);

            var baseName = Path.GetFileNameWithoutExtension(imagePath);
            var rawPath = Path.Combine(targetDirectory, $"{baseName}_visible_raw.jpg");
            var outputPath = Path.Combine(targetDirectory, $"{baseName}_visible.jpg");
            var sourceTimestamp = File.GetLastWriteTimeUtc(imagePath);
            var outputValid = IsDecodableImageFile(outputPath);
            var rawValid = IsDecodableImageFile(rawPath);
            var needsUpdate = !outputValid || File.GetLastWriteTimeUtc(outputPath) < sourceTimestamp;

            if (!outputValid && File.Exists(outputPath))
                File.Delete(outputPath);

            if (!rawValid && File.Exists(rawPath))
                File.Delete(rawPath);

            if (needsUpdate)
            {
                var pythonVisiblePath = TryExtractVisibleImageWithPython(imagePath);
                if (!string.IsNullOrWhiteSpace(pythonVisiblePath) && File.Exists(pythonVisiblePath))
                {
                    metadata.VisibleImagePath = pythonVisiblePath;
                    return;
                }

                byte[]? visibleBytes = FlirFffParser.TryExtractVisibleJpeg(imagePath);
                visibleBytes ??= await exifTool.TryExtractVisibleImageAsync(imagePath, cancellationToken).ConfigureAwait(false);

                if (visibleBytes is not null && visibleBytes.Length > 0 && IsDecodableImageBytes(visibleBytes))
                {
                    File.WriteAllBytes(rawPath, visibleBytes);

                    var enhancedVisible = EnhanceVisibleJpeg(visibleBytes);
                    if (IsDecodableImageBytes(enhancedVisible))
                        File.WriteAllBytes(outputPath, enhancedVisible);
                    else
                        File.WriteAllBytes(outputPath, visibleBytes);
                }
            }

            if (IsDecodableImageFile(outputPath))
                metadata.VisibleImagePath = outputPath;
            else if (IsDecodableImageFile(rawPath))
                metadata.VisibleImagePath = rawPath;
            else
                metadata.VisibleImagePath = null;
        }
        catch
        {
            // Visible image extraction is optional.
        }
    }

    // ─── Python sidecar ───────────────────────────────────────────────────

    private static string? TryExtractVisibleImageWithPython(string imagePath)
    {
        var scriptPath = ResolvePythonVisibleExtractorScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
            return null;

        var absoluteImagePath = Path.GetFullPath(imagePath);
        var sidecarCandidates = ResolvePythonSidecarVisibleCandidates(absoluteImagePath).ToArray();

        foreach (var executable in ResolvePythonExecutables())
        {
            var arguments = executable.EndsWith("py.exe", StringComparison.OrdinalIgnoreCase)
                ? $"-3 \"{scriptPath}\" \"{absoluteImagePath}\" --json"
                : $"\"{scriptPath}\" \"{absoluteImagePath}\" --json";

            var output = RunProcessCapture(executable, arguments);
            if (string.IsNullOrWhiteSpace(output))
                continue;

            var json = TryExtractJsonObject(output);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                var root = JsonSerializer.Deserialize<JsonElement>(json);
                if (!root.TryGetProperty("status", out var status)
                    || !string.Equals(status.GetString(), "sucesso", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (root.TryGetProperty("caminho_visivel_original", out var originalPathElement))
                {
                    var originalPath = originalPathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
                        return originalPath;
                }

                if (root.TryGetProperty("caminho_visivel", out var visiblePathElement))
                {
                    var visiblePath = visiblePathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(visiblePath) && File.Exists(visiblePath))
                        return visiblePath;
                }
            }
            catch
            {
                // Try next invocation.
            }

            foreach (var sidecarPath in sidecarCandidates)
                if (File.Exists(sidecarPath))
                    return sidecarPath;
        }

        foreach (var sidecarPath in sidecarCandidates)
            if (File.Exists(sidecarPath))
                return sidecarPath;

        return null;
    }

    private static IEnumerable<string> ResolvePythonSidecarVisibleCandidates(string imagePath)
    {
        var imageDirectory = Path.GetDirectoryName(imagePath);
        if (string.IsNullOrWhiteSpace(imageDirectory))
            yield break;

        var baseName = Path.GetFileNameWithoutExtension(imagePath);
        var candidates = new[]
        {
            Path.Combine(imageDirectory, $"{baseName}_visivel_original.jpg"),
            Path.Combine(imageDirectory, $"{baseName}_visible_original.jpg"),
            Path.Combine(imageDirectory, $"{baseName}_visivel.jpg"),
            Path.Combine(imageDirectory, $"{baseName}_visible.jpg")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return candidate;
    }

    private static IEnumerable<string> ResolvePythonExecutables()
    {
        var candidates = new List<string>
        {
            "py", "python",
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
        catch { /* Ignore environment probing failures. */ }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsExecutableAvailable(candidate))
                yield return candidate;
    }

    private static bool IsExecutableAvailable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        if (Path.IsPathRooted(executable))
            return File.Exists(executable);

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
                    if (File.Exists(basePath)) return true;
                    continue;
                }

                foreach (var ext in extensions)
                    if (File.Exists(basePath + ext))
                        return true;
            }
        }
        catch { return false; }

        return false;
    }

    private static string? ResolvePythonVisibleExtractorScriptPath()
    {
        var tempScriptPath = EnsureEmbeddedToolInTemp(
            "ThermixStudio.App.Embedded.extrair_imagens_flir.py",
            "thermix_extrair_imagens_flir.py");
        if (!string.IsNullOrWhiteSpace(tempScriptPath) && File.Exists(tempScriptPath))
            return tempScriptPath;

        var packagedToolPath = Path.Combine(AppContext.BaseDirectory, "tools", "extrair_imagens_flir.py");
        if (File.Exists(packagedToolPath)) return packagedToolPath;

        var localToolPath = Path.Combine(AppContext.BaseDirectory, "extrair_imagens_flir.py");
        if (File.Exists(localToolPath)) return localToolPath;

        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "extrair_imagens_flir.py");
                if (File.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        catch { return null; }

        return null;
    }

    private static string? EnsureEmbeddedToolInTemp(string resourceName, string outputFileName)
    {
        try
        {
            var outputPath = Path.Combine(Path.GetTempPath(), outputFileName);
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return outputPath;

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return null;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.CopyTo(fs);
            return outputPath;
        }
        catch { return null; }
    }

    private static string? TryExtractJsonObject(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                return trimmed;
        }
        return null;
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
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(6000);
            if (process.ExitCode != 0) return null;

            if (!string.IsNullOrWhiteSpace(output)) return output;
            return string.IsNullOrWhiteSpace(error) ? null : error;
        }
        catch { return null; }
    }

    // ─── Image enhance ────────────────────────────────────────────────────

    private static byte[] EnhanceVisibleJpeg(byte[] jpegBytes)
    {
        try
        {
            using var input = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
            if (input.Empty()) return jpegBytes;

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
                    (int)ImwriteFlags.JpegQuality, 95
                }))
            {
                return encoded;
            }

            return jpegBytes;
        }
        catch { return jpegBytes; }
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

    // ─── Validation helpers ────────────────────────────────────────────────

    private static bool IsDecodableImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return IsDecodableImageBytes(bytes);
        }
        catch { return false; }
    }

    private static bool IsDecodableImageBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
            return false;
        try
        {
            using var img = Cv2.ImDecode(bytes, ImreadModes.Color);
            return !img.Empty() && img.Width > 0 && img.Height > 0;
        }
        catch { return false; }
    }
}
