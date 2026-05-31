using ThermixStudio.Core;

namespace ThermixStudio.Core.Services;

/// <summary>
/// Interface centralizada para operações com ExifTool.
/// Consolida extração de imagens, detecção de modo, detecção de paleta e metadados EXIF.
/// </summary>
public interface IExifToolService
{
    /// <summary>
    /// Detecta o modo de captura a partir da tag FLIR:ThermalImageType.
    /// Retorna null se não conseguir determinar.
    /// </summary>
    Task<ImageViewMode?> TryDetectModeAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extrai a imagem RGB nativa embutida (Digital Photo).
    /// Tenta tags: FLIR:EmbeddedImage, EmbeddedImage, PreviewImage.
    /// Retorna null se nenhuma imagem foi encontrada.
    /// </summary>
    Task<byte[]?> TryExtractVisibleImageAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extrai matriz térmica bruta em 16-bit (RAW).
    /// Retorna null se não disponível.
    /// </summary>
    Task<byte[]?> TryExtractRawThermalAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê o nome da paleta a partir do EXIF.
    /// Exemplos: "Iron", "Rainbow", "Grayscale"
    /// Retorna null se não encontrar.
    /// </summary>
    Task<string?> TryGetPaletteNameAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê todos os metadados EXIF como JSON para processamento adicional.
    /// </summary>
    Task<string?> TryGetAllMetadataJsonAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Procura ExifTool no sistema (PATH, AppContext.BaseDirectory, etc).
    /// Cacheia resultado por sessão.
    /// Retorna caminho completo ou null se não encontrado.
    /// </summary>
    string? FindExifTool();

    /// <summary>
    /// Verifica se ExifTool está disponível no sistema.
    /// </summary>
    bool IsExifToolAvailable();
}
