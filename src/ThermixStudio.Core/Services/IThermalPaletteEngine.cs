using System.Drawing;
using ThermixStudio.Core;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.Core.Services;

/// <summary>
/// Classe interna: LUT (Look-Up Table) de paleta térmica.
/// 256 cores RGB em formato JSON.
/// </summary>
public sealed class ThermalPaletteLutData
{
    public string Name { get; set; } = string.Empty;
    public List<int[]> Rgb { get; set; } = new(); // Lista de [R, G, B] triplets
}

/// <summary>
/// Interface para motor de renderização de paletas térmicas.
/// Extrai ThermalCS com algoritmo ProcessSmartHD para remapeamento inteligente
/// preservando elementos de UI da câmera.
/// </summary>
public interface IThermalPaletteEngine
{
    /// <summary>
    /// Carrega LUT de arquivo JSON.
    /// Paletas suportadas: Iron, Rainbow, Grayscale (e Hotmetal, Arctic opcionais)
    /// </summary>
    /// <param name="paletteName">Nome da paleta ("Iron", "Rainbow", "Grayscale", etc)</param>
    Task<ThermalPaletteLutData?> LoadLutAsync(string paletteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtém uma LUT já carregada do cache (síncrono, sem I/O).
    /// Retorna null se a LUT ainda não foi carregada via <see cref="LoadLutAsync"/>.
    /// </summary>
    ThermalPaletteLutData? GetCachedLut(string paletteName);

    /// <summary>
    /// Detecta automaticamente a paleta original de uma imagem térmica.
    /// Usa amostragem aleatória (500 pixels) + distância Euclidiana.
    /// </summary>
    Task<string?> DetectPaletteAsync(Bitmap originalImage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remapeia pixels entre duas paletas usando o algoritmo ProcessSmartHD.
    /// Preserva elementos de UI (bordas, logos, barras).
    /// </summary>
    /// <param name="sourceImage">Imagem com paleta original</param>
    /// <param name="sourcePaletteName">Nome da paleta de origem ("Iron", "Rainbow", etc)</param>
    /// <param name="targetPaletteName">Nome da paleta de destino</param>
    /// <returns>Bitmap remapeado com paleta de destino</returns>
    Bitmap ProcessSmartHD(
        Bitmap sourceImage, 
        string sourcePaletteName, 
        string targetPaletteName);

    /// <summary>
    /// Renderiza matriz térmica com paleta específica (pixel-by-pixel).
    /// </summary>
    /// <param name="temperatures">Matriz de temperaturas em Celsius</param>
    /// <param name="width">Largura</param>
    /// <param name="height">Altura</param>
    /// <param name="paletteName">Nome da paleta</param>
    /// <param name="levelMinC">Temperatura mínima para escala (null = auto)</param>
    /// <param name="levelMaxC">Temperatura máxima para escala (null = auto)</param>
    /// <returns>Pixels BGRA renderizados</returns>
    [Obsolete("Use RenderWithProfileAsync com RenderProfile.FromMetadata()")]
    Task<byte[]> RenderThermalWithPaletteAsync(
        double[,] temperatures,
        int width, int height,
        string paletteName,
        double? levelMinC = null,
        double? levelMaxC = null,
        RadiometricMetadata? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renderiza matriz térmica usando um <see cref="Thermal.RenderProfile"/> por imagem.
    /// O perfil controla TODAS as transformações: Planck, stretch, whiteboost e limit colors.
    /// Elimina constantes globais — cada termograma define seu próprio pipeline.
    /// </summary>
    Task<byte[]> RenderWithProfileAsync(
        double[,] temperatures,
        int width, int height,
        string paletteName,
        RenderProfile profile,
        RadiometricMetadata? metadata = null,
        CancellationToken cancellationToken = default);
}
