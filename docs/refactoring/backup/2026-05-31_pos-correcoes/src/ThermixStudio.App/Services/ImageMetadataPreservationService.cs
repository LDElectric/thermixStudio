using System.Diagnostics;
using System.IO;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.Services;

public sealed class ImageMetadataPreservationService : IImageMetadataPreservationService
{
    private readonly IExifToolService _exifToolService;

    public ImageMetadataPreservationService(IExifToolService exifToolService)
    {
        _exifToolService = exifToolService;
    }

    public async Task<bool> CopyOriginalMetadataAsync(
        string originalImagePath,
        string exportedImagePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(originalImagePath) || !File.Exists(exportedImagePath))
        {
            return false;
        }

        var exifTool = _exifToolService.FindExifTool();
        if (string.IsNullOrWhiteSpace(exifTool) || !File.Exists(exifTool))
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exifTool,
                    Arguments = $"-overwrite_original -TagsFromFile \"{originalImagePath}\" -all:all -unsafe \"{exportedImagePath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[METADATA_COPY] ExifTool falhou: {error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[METADATA_COPY] Falha ao preservar metadados: {ex.Message}");
            return false;
        }
    }
}
