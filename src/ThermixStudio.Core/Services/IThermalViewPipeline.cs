using System.Drawing;
using ThermixStudio.Core;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.Core.Services;

/// <summary>
/// Orquestra os três motores de visualização térmica:
/// <see cref="IThermalRenderEngine"/> (paleta FLIR embarcada / radiometria),
/// <see cref="IThermalPaletteEngine"/> (ThermalCS — LUTs e ProcessSmartHD),
/// <see cref="IThermalModeEngine"/> (modos_CS — MSX, PiP, Combinação, etc.).
/// </summary>
public interface IThermalViewPipeline
{
    /// <summary>Carrega LUT embarcada da câmera no <see cref="IThermalRenderEngine"/>.</summary>
    Task PrepareThermogramAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>Pré-aquece o cache de LUTs (Iron + paletas comuns) em background para primeira renderização instantânea.</summary>
    Task PreWarmPalettesAsync(CancellationToken cancellationToken = default);

    /// <summary>Modo de captura via EXIF (PiP, MSX, Visible, …).</summary>
    Task<ImageViewMode?> DetectCaptureModeFromMetadataAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>Detecção de paleta por amostragem de pixels (ThermalCS).</summary>
    Task<string?> DetectPaletteFromFileAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>Renderização radiométrica com LUT embarcada ou fallback (ThermalRenderEngine).</summary>
    ThermalRenderResult RenderRadiometric(ThermalImageData image, ThermalRenderParameters parameters);

    /// <summary>Renderização radiométrica com LUT nomeada (ThermalPaletteEngine).</summary>
    [Obsolete("Use RenderRadiometricWithProfileAsync com RenderProfile.FromMetadata()")]
    Task<byte[]> RenderRadiometricWithPaletteAsync(
        ThermalImageData image,
        string paletteName,
        double? levelMinC,
        double? levelMaxC,
        CancellationToken cancellationToken = default);

    /// <summary>Renderização radiométrica com perfil por imagem (ThermalPaletteEngine).</summary>
    Task<byte[]> RenderRadiometricWithProfileAsync(
        ThermalImageData image,
        string paletteName,
        RenderProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>Remapeia frame capturado entre paletas (ProcessSmartHD / ThermalCS).</summary>
    byte[] RemapCapturedFrame(Bitmap originalFrame, string sourcePaletteName, string targetPaletteName);

    /// <summary>Compõe MSX, PiP, Combinação ou cópia térmica/visível (modos_CS).</summary>
    byte[] ComposeViewMode(
        ImageViewMode mode,
        byte[] thermalPixels,
        int width,
        int height,
        byte[]? visiblePixels,
        double intensity,
        double pipScale,
        ThermalImageData? thermalData = null);

    bool ModeRequiresVisible(ImageViewMode mode);

    /// <summary>
    /// Sobrepõe os elementos de UI da câmera (logo, escala, crosshair, temperaturas)
    /// sobre a imagem final renderizada, usando detecção por luminância.
    /// Quando <paramref name="paletteName"/> é fornecida (e difere de Iron/Original),
    /// a barra de escala é redesenhada com as cores da paleta ativa.
    /// </summary>
    byte[] OverlayCameraUI(
        byte[] finalPixels,
        byte[] originalPixels,
        int width,
        int height,
        ImageViewMode mode = ImageViewMode.Thermal,
        string? paletteName = null,
        double? scaleMinC = null,
        double? scaleMaxC = null,
        double? spotTemperatureC = null,
        double? maxTemperatureC = null,
        double? minTemperatureC = null,
        bool? spotIsApproximate = null,
        bool preferOriginalTemperatureText = false,
        string? spotLabel = null,
        double? spotNormX = null,
        double? spotNormY = null);
}
