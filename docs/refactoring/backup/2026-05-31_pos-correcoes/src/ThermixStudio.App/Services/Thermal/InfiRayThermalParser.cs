using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using OpenCvSharp;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services.Thermal;

/// <summary>
/// Parser para arquivos InfiRay (.irg / .rjpeg) e formatos OEM similares.
/// Estratégia: tenta JPEG embutido com OpenCvSharp + fallback 16-bit raw.
/// </summary>
internal static class InfiRayThermalParser
{
    private const double MinTempFallback = 20.0;
    private const double MaxTempFallback = 120.0;

    public static ThermalImageData Load(string imagePath)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        var data = new ThermalImageData
        {
            SourceFormat = ext.TrimStart('.').ToUpperInvariant(),
            Metadata = new RadiometricMetadata { Manufacturer = "InfiRay", Detector = "InfiRay" }
        };

        try
        {
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
            FlukeIs2Parser.InferDimensions(total, ref width, ref height);

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
