namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Mapeia valores EXIF FLIR:ThermalImageType para <see cref="ImageViewMode"/>.
/// Consolidado de ExifToolService, ThermalAnalysisService e ThermalModeDetectionService.
/// </summary>
public static class ExifModeMapper
{
    public static ImageViewMode? MapThermalImageType(string? exifMode)
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
}
