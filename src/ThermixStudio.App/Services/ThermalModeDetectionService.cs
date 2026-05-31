using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services;

/// <summary>
/// Detecção centralizada de modo de captura térmica (EXIF e metadados carregados).
/// </summary>
public sealed class ThermalModeDetectionService : IThermalModeDetectionService
{
    private readonly IExifToolService _exifTool;

    public ThermalModeDetectionService(IExifToolService exifTool)
    {
        _exifTool = exifTool;
    }

    public async Task<ImageViewMode?> DetectFromFileAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var fromExifTool = await _exifTool.TryDetectModeAsync(imagePath, cancellationToken).ConfigureAwait(false);
        if (fromExifTool.HasValue)
        {
            return fromExifTool;
        }

        var metadataJson = await _exifTool.TryGetMetadataJsonNumericAsync(imagePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            var root = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(metadataJson);
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            var first = root[0];
            if (first.TryGetProperty("ThermalImageType", out var modeProp) &&
                modeProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return ExifModeMapper.MapThermalImageType(modeProp.GetString());
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public ImageViewMode? DetectFromMetadata(RadiometricMetadata metadata)
        => metadata.DetectedViewMode;
}
