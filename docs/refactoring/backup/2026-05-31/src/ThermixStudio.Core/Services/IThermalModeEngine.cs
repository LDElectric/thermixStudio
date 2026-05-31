using ThermixStudio.Core;

namespace ThermixStudio.Core.Services;

/// <summary>
/// Interface para motor de composição de modos térmicos.
/// Extrai modos_CS como motor independente para renderização de:
/// - Térmica Pura
/// - MSX (com detecção Laplaciana de bordas)
/// - Blending (Alpha linear)
/// - PiP (50% central)
/// - Luz Visível Pura
/// - Fusion (intervalo de temperatura)
/// </summary>
public interface IThermalModeEngine
{
    /// <summary>
    /// Renderiza modo específico a partir de pixels BGRA termais e possivelmente visíveis.
    /// </summary>
    /// <param name="mode">Modo a renderizar</param>
    /// <param name="thermalPixels">Pixels BGRA da imagem térmica renderizada (32-bit)</param>
    /// <param name="width">Largura em pixels</param>
    /// <param name="height">Altura em pixels</param>
    /// <param name="visiblePixels">Pixels BGRA da imagem visível (null se não disponível)</param>
    /// <param name="intensity">Intensidade do efeito (0..1) para MSX/Blending</param>
    /// <param name="pipScale">Escala da janela PiP (0..1)</param>
    /// <param name="thermalData">Dados térmicos adicionais (para Fusion)</param>
    /// <returns>Pixels BGRA renderizados no modo solicitado</returns>
    byte[] RenderMode(
        ImageViewMode mode,
        byte[] thermalPixels,
        int width, int height,
        byte[]? visiblePixels,
        double intensity,
        double pipScale,
        ThermalImageData? thermalData = null);

    /// <summary>
    /// Detecta o modo original da câmera a partir dos metadados EXIF.
    /// Usa ExifTool internamente.
    /// </summary>
    Task<ImageViewMode?> TryDetectOriginalModeAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Valida se o modo requer imagem visível pareada.
    /// Se requer e visível não está disponível, fallback para Thermal.
    /// </summary>
    bool ModeRequiresVisible(ImageViewMode mode);

    /// <summary>
    /// Sobrepõe os elementos de UI da câmera original (logo, barra de escala, crosshair,
    /// caixas de temperatura) sobre a imagem já renderizada no modo ativo.
    /// Usa detecção por luminância + saturação para identificar pixels de UI.
    /// </summary>
    /// <param name="finalPixels">Pixels BGRA da imagem renderizada (destino)</param>
    /// <param name="originalPixels">Pixels BGRA da imagem original da câmera (fonte de UI)</param>
    /// <param name="width">Largura em pixels</param>
    /// <param name="height">Altura em pixels</param>
    /// <param name="mode">Modo de visualização ativo (influencia quais elementos são copiados)</param>
    /// <param name="scaleLut">LUT da paleta ativa para redesenhar a barra de escala com as cores corretas (null = copia original)</param>
    /// <param name="copyOriginalScaleBar">Se true e scaleLut é null, copia a barra de escala original pixel a pixel</param>
    /// <returns>Array BGRA com UI da câmera sobreposta</returns>
    byte[] OverlayCameraUI(
        byte[] finalPixels,
        byte[] originalPixels,
        int width,
        int height,
        ImageViewMode mode = ImageViewMode.Thermal,
        ThermalPaletteLutData? scaleLut = null,
        bool copyOriginalScaleBar = true,
        double? scaleMinC = null,
        double? scaleMaxC = null,
        double? spotTemperatureC = null,
        double? maxTemperatureC = null,
        double? minTemperatureC = null,
        bool? spotIsApproximate = null,
        bool preferOriginalTemperatureText = false);
}
