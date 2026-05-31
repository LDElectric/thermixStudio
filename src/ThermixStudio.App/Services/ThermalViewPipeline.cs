using System.Drawing;
using System.IO;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

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
    private readonly IFlirCameraUiOverlay _cameraUiOverlay;

    public ThermalViewPipeline(
        IThermalRenderEngine renderEngine,
        IThermalPaletteEngine paletteEngine,
        IThermalModeEngine modeEngine,
        IThermalAnalysisService analysisService,
        IThermalModeDetectionService modeDetectionService,
        IFlirCameraUiOverlay cameraUiOverlay)
    {
        _renderEngine = renderEngine;
        _paletteEngine = paletteEngine;
        _modeEngine = modeEngine;
        _analysisService = analysisService;
        _modeDetectionService = modeDetectionService;
        _cameraUiOverlay = cameraUiOverlay;
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
        => _paletteEngine.RenderThermalWithPaletteAsync(
            image.Temperatures,
            image.Width,
            image.Height,
            paletteName,
            levelMinC,
            levelMaxC,
            image.Metadata,
            cancellationToken);

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

    public byte[] OverlayCameraUI(
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
        string? spotLabel = null)
    {
        ThermalPaletteLutData? scaleLut = null;
        bool copyOriginalScaleBar = mode != ImageViewMode.Visible;

        if (mode != ImageViewMode.Visible &&
            !string.IsNullOrWhiteSpace(paletteName) &&
            !paletteName.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            scaleLut = _paletteEngine.LoadLutAsync(paletteName)
                .GetAwaiter()
                .GetResult();
            copyOriginalScaleBar = false;
        }

        return _cameraUiOverlay.Overlay(
            finalPixels,
            originalPixels,
            width,
            height,
            mode,
            scaleLut,
            copyOriginalScaleBar,
            scaleMinC,
            scaleMaxC,
            spotTemperatureC,
            maxTemperatureC,
            minTemperatureC,
            spotIsApproximate,
            preferOriginalTemperatureText,
            spotLabel);
    }

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
