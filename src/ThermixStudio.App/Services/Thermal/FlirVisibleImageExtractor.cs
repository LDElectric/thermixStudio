using System.IO;
using OpenCvSharp;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Extrator de imagem visivel de termogramas FLIR.
/// 100% C# — FlirFffParser + ExifTool + cache/enhance OpenCV.
/// </summary>
internal static class FlirVisibleImageExtractor
{
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
                // Prioridade 1: C# nativo (FlirFffParser)
                byte[]? visibleBytes = FlirFffParser.TryExtractVisibleJpeg(imagePath);
                // Prioridade 2: ExifTool
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

    private static void ApplyClaheOnLuminanceInPlace(Mat bgr, double clipLimit, Size tileGridSize)
    {
        try
        {
            using var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
            var channels = Cv2.Split(lab);
            if (channels.Length < 3) return;

            using var clahe = Cv2.CreateCLAHE(clipLimit, tileGridSize);
            using var lClahe = new Mat();
            clahe.Apply(channels[0], lClahe);
            lClahe.CopyTo(channels[0]);

            using var merged = new Mat();
            Cv2.Merge(channels, merged);
            Cv2.CvtColor(merged, bgr, ColorConversionCodes.Lab2BGR);

            foreach (var c in channels) c.Dispose();
        }
        catch { /* best-effort */ }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static bool IsDecodableImageFile(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var img = Cv2.ImRead(path, ImreadModes.Color);
            return !img.Empty();
        }
        catch { return false; }
    }

    private static bool IsDecodableImageBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return false;
        try
        {
            using var img = Cv2.ImDecode(bytes, ImreadModes.Color);
            return !img.Empty();
        }
        catch { return false; }
    }
}
