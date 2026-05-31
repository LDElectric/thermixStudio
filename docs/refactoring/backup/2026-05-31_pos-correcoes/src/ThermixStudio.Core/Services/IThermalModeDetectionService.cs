namespace ThermixStudio.Core.Services;

/// <summary>
/// Ponto único de detecção de modo de captura térmica (EXIF e metadados carregados).
/// </summary>
public interface IThermalModeDetectionService
{
    /// <summary>
    /// Detecta modo a partir do arquivo via ExifTool (tag FLIR:ThermalImageType).
    /// </summary>
    Task<ImageViewMode?> DetectFromFileAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detecta modo a partir de metadados já extraídos (DetectedViewMode ou PaletteName derivado).
    /// </summary>
    ImageViewMode? DetectFromMetadata(RadiometricMetadata metadata);
}
