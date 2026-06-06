using System.Drawing;
using System.IO;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Core.Thermal;

namespace ThermixStudio.App.Services;

/// <summary>
/// Fachada única para detecção e modulação de modos/paletas usando exclusivamente
/// <see cref="ThermalRenderEngine"/>, <see cref="ThermalPaletteEngine"/> e <see cref="ThermalModeEngine"/>.
/// </summary>
public sealed class ThermalViewPipeline : IThermalViewPipeline
{
    private readonly IThermalRenderEngine _renderEngine;
    private readonly IThermalPaletteEngine _paletteEngine;
    private readonly IThermalModeEngine _modeEngine;
    private readonly IThermalAnalysisService _analysisService;
    private readonly IThermalModeDetectionService _modeDetectionService;

    public ThermalViewPipeline(
        IThermalRenderEngine renderEngine,
        IThermalPaletteEngine paletteEngine,
        IThermalModeEngine modeEngine,
        IThermalAnalysisService analysisService,
        IThermalModeDetectionService modeDetectionService)
    {
        _renderEngine = renderEngine;
        _paletteEngine = paletteEngine;
        _modeEngine = modeEngine;
        _analysisService = analysisService;
        _modeDetectionService = modeDetectionService;
    }

    public async Task PrepareThermogramAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            _renderEngine.SetEmbeddedPalette(null);
            return;
        }

        var embedded = await _analysisService.TryExtractEmbeddedPaletteAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);
        _renderEngine.SetEmbeddedPalette(embedded);
    }

    /// <summary>
    /// Pré-aquece o cache de LUTs das paletas mais comuns (Iron, Rainbow, Grayscale)
    /// em background para que a primeira renderização seja instantânea.
    /// </summary>
    public async Task PreWarmPalettesAsync(CancellationToken cancellationToken = default)
    {
        // Carrega as paletas mais usadas primeiro; as demais carregam sob demanda
        await _paletteEngine.LoadLutAsync("Iron", cancellationToken).ConfigureAwait(false);
    }

    public Task<ImageViewMode?> DetectCaptureModeFromMetadataAsync(string imagePath, CancellationToken cancellationToken = default)
        => _modeDetectionService.DetectFromFileAsync(imagePath, cancellationToken);

    public async Task<string?> DetectPaletteFromFileAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        using var bitmap = new Bitmap(imagePath);
        return await _paletteEngine.DetectPaletteAsync(bitmap, cancellationToken).ConfigureAwait(false);
    }

    public ThermalRenderResult RenderRadiometric(ThermalImageData image, ThermalRenderParameters parameters)
        => _renderEngine.Render(image, parameters);

    public Task<byte[]> RenderRadiometricWithPaletteAsync(
        ThermalImageData image,
        string paletteName,
        double? levelMinC,
        double? levelMaxC,
        CancellationToken cancellationToken = default)
    {
        var profile = RenderProfile.FromMetadata(
            image.Metadata,
            levelMinC ?? 0,
            levelMaxC ?? 100);
        return RenderRadiometricWithProfileAsync(image, paletteName, profile, cancellationToken);
    }

    /// <summary>
    /// Renderização radiométrica com <see cref="RenderProfile"/> por imagem.
    /// Se <see cref="ThermalImageData.CalibratedLut"/> estiver disponível,
    /// aplica Histogram Matching (Hypothesis C) após o render da paleta.
    /// </summary>
    public async Task<byte[]> RenderRadiometricWithProfileAsync(
        ThermalImageData image,
        string paletteName,
        RenderProfile profile,
        CancellationToken cancellationToken = default)
    {
        var pixels = await _paletteEngine.RenderWithProfileAsync(
            image.Temperatures,
            image.Width,
            image.Height,
            paletteName,
            profile,
            image.Metadata,
            cancellationToken).ConfigureAwait(false);

        // Hypothesis C: LUT com range Planck completo + interpolação 4096 bins
        // Apply() usa o range atual dos sliders, interpolando linearmente
        // entre os 4096 bins → gradientes suaves sem banding
        if (image.CalibratedLut is not null)
        {
            double sliderMin = profile.LevelMinC;
            double sliderMax = profile.LevelMaxC;
            image.CalibratedLut.Apply(image.Temperatures, pixels,
                image.Width, image.Height, sliderMin, sliderMax);
        }

        return pixels;
    }

    public byte[] RemapCapturedFrame(Bitmap originalFrame, string sourcePaletteName, string targetPaletteName)
    {
        using var remapped = _paletteEngine.ProcessSmartHD(originalFrame, sourcePaletteName, targetPaletteName);
        return CopyBitmapToBgra(remapped);
    }

    public byte[] ComposeViewMode(
        ImageViewMode mode,
        byte[] thermalPixels,
        int width,
        int height,
        byte[]? visiblePixels,
        double intensity,
        double pipScale,
        ThermalImageData? thermalData = null)
        => _modeEngine.RenderMode(mode, thermalPixels, width, height, visiblePixels, intensity, pipScale, thermalData);

    public bool ModeRequiresVisible(ImageViewMode mode) => _modeEngine.ModeRequiresVisible(mode);

    public byte[] OverlayCameraUI(byte[] finalPixels, byte[] originalPixels, int width, int height, ImageViewMode mode = ImageViewMode.Thermal, string? paletteName = null, double? scaleMinC = null, double? scaleMaxC = null, double? spotTemperatureC = null, double? maxTemperatureC = null, double? minTemperatureC = null, bool? spotIsApproximate = null, bool preferOriginalTemperatureText = false, string? spotLabel = null, double? spotNormX = null, double? spotNormY = null)
        => finalPixels;

    private static byte[] CopyBitmapToBgra(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
