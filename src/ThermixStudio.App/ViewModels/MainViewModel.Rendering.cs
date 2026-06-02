using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThermixStudio.App.Services;
using ThermixStudio.Core;
using ThermixStudio.Core.Thermal;
using CommunityToolkit.Mvvm.Input;

namespace ThermixStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    // ── Cache de pixels BGRA (evita re-leitura de disco a cada re-render) ──
    private string? _cachedOriginalPath;
    private byte[]? _cachedOriginalBgra;
    private string? _cachedVisiblePath;
    private byte[]? _cachedVisibleBgra;
    private (int w, int h) _cachedVisibleAlignedSize;

    // ── Cache do min/max da matriz de temperaturas ──
    private double _cachedMatrixMinC = double.MaxValue;
    private double _cachedMatrixMaxC = double.MinValue;

    // ── Flag para exportação de render limpo (sem overlay/logo/escala) ──
    public static bool SuppressOverlay { get; set; }

    // ── LUT Temperatura→Cor — construída 1x, aplicada todo frame ──
    private TemperatureColorLut? _temperatureLut;

    private void ClearPerThermogramCaches()
    {
        _cachedOriginalPath = null;
        _cachedOriginalBgra = null;
        _cachedVisiblePath = null;
        _cachedVisibleBgra = null;
        _cachedVisibleAlignedSize = default;
        _cachedMatrixMinC = double.MaxValue;
        _cachedMatrixMaxC = double.MinValue;
        _temperatureLut = null;
    }

    private void UpdateDisplayImage()
    {
        if (_loadedImage is null) return;

        var width = _loadedImage.Width;
        var height = _loadedImage.Height;

        double appliedMin = LevelMinC;
        double appliedMax = LevelMaxC;

        if (AutoScaleEnabled)
        {
            var (autoMin, autoMax) = GetPreferredThermalRange(_loadedImage);
            appliedMin = autoMin;
            appliedMax = autoMax;

            if (_autoAdjustRegion.HasValue)
            {
                var regionRange = GetRegionRange(_loadedImage, _autoAdjustRegion.Value);
                appliedMin = regionRange.min;
                appliedMax = regionRange.max;
            }

            LevelMinC = appliedMin;
            LevelMaxC = appliedMax;
        }

        byte[] thermalPixels = Array.Empty<byte>();
        if (!TryRenderThermalPixelsViaPipeline(_loadedImage, SelectedPalette, appliedMin, appliedMax, out thermalPixels, out appliedMin, out appliedMax))
        {
            Debug.WriteLine($"[PALETTE_ERROR] Falha ao renderizar paleta {SelectedPalette}; tentando Grayscale.");
            LogToFile($"[PALETTE_ERROR] Falha ao renderizar paleta {SelectedPalette}; tentando Grayscale.");
            TryRenderThermalPixelsViaPipeline(_loadedImage, ThermalPalette.Grayscale, appliedMin, appliedMax, out thermalPixels, out appliedMin, out appliedMax);
        }

        var result = new ThermalRenderResult
        {
            Width = width,
            Height = height,
            BgraPixels = thermalPixels,
            AppliedMinC = appliedMin,
            AppliedMaxC = appliedMax
        };
        var displayScale = GetDisplayedScaleRange(_loadedImage, result.AppliedMinC, result.AppliedMaxC, AutoScaleEnabled);

        var hasOriginal = TryLoadOriginalCameraBgraPixels(width, height, out var originalPixels);
        var hasVisible = TryLoadVisibleBgraPixels(width, height, out var visiblePixels);

        // ══════════════════════════════════════════════════════════════
        // TemperatureColorLut: mapeia TEMPERATURA → COR direto do original.
        // Universal — funciona para qualquer cena, qualquer termograma.
        // Construído 1x, aplicado instantaneamente todo frame.
        // ══════════════════════════════════════════════════════════════
        if (hasOriginal && originalPixels is not null &&
            ImageViewMode != ImageViewMode.Original &&
            ImageViewMode != ImageViewMode.Visible &&
            _loadedImage?.Temperatures is not null)
        {
            if (_temperatureLut == null)
            {
                double buildMin = LevelMinC;
                double buildMax = LevelMaxC;
                if (buildMax <= buildMin) { buildMin = _loadedImage.Temperatures.Cast<double>().Min(); buildMax = _loadedImage.Temperatures.Cast<double>().Max(); }

                _temperatureLut = TemperatureColorLut.Build(
                    _loadedImage.Temperatures, originalPixels, width, height,
                    buildMin, buildMax, numBins: 256, cropMargin: 0.08);
                Debug.WriteLine("[TEMP-LUT] Construida para esta imagem.");
            }

            // Aplica LUT temperatura→cor (substitui thermalPixels)
            for (int i = 3; i < thermalPixels.Length; i += 4)
                thermalPixels[i] = 255; // alpha
            _temperatureLut.Apply(_loadedImage.Temperatures, thermalPixels, width, height);
        }

        var spotTemperature = GetSpotTemperature(_loadedImage, hasOriginal ? originalPixels : null, displayScale);
        var spotIsApproximate = hasOriginal && originalPixels is not null
            ? DetectSpotApproximationMarker(originalPixels, width, height)
            : (bool?)null;

        if (!hasVisible && !string.IsNullOrWhiteSpace(CurrentImagePath))
        {
            var resolvedVisiblePath = NormalizeVisibleImagePath(
                AutoDetectVisiblePairPath(CurrentImagePath),
                CurrentImagePath);

            if (!string.IsNullOrWhiteSpace(resolvedVisiblePath))
            {
                PairedVisibleImagePath = resolvedVisiblePath;
                hasVisible = TryLoadVisibleBgraPixels(width, height, out visiblePixels);
            }
        }

        var modeRequiresVisible = ImageViewMode is ImageViewMode.Visible or ImageViewMode.Fusion or ImageViewMode.Blending or ImageViewMode.PiP or ImageViewMode.Msx;
        if (modeRequiresVisible && !hasVisible && TryEnsureVisiblePairOnDemand())
        {
            hasVisible = TryLoadVisibleBgraPixels(width, height, out visiblePixels);
        }

        if (ImageViewMode == ImageViewMode.Original && hasOriginal)
        {
            DisplayImage = BuildBitmap(new ThermalRenderResult
            {
                Width = width,
                Height = height,
                BgraPixels = originalPixels!,
                AppliedMinC = result.AppliedMinC,
                AppliedMaxC = result.AppliedMaxC
            });
            CurrentScaleLabel = "Escala: original da camera";
            return;
        }

        if (ImageViewMode == ImageViewMode.Visible && hasVisible)
        {
            var visibleFinalPixels = visiblePixels!;
            if (hasOriginal && !SuppressOverlay)
            {
                visibleFinalPixels = _viewPipeline.OverlayCameraUI(
                    visibleFinalPixels,
                    originalPixels ?? Array.Empty<byte>(),
                    width,
                    height,
                    global::ThermixStudio.Core.ImageViewMode.Visible,
                    SelectedPalette.ToString(),
                    displayScale.min,
                    displayScale.max,
                    spotTemperature,
                    null,
                    null,
                    spotIsApproximate,
                    false);
            }

            DisplayImage = BuildBitmap(new ThermalRenderResult
            {
                Width = width,
                Height = height,
                BgraPixels = visibleFinalPixels,
                AppliedMinC = result.AppliedMinC,
                AppliedMaxC = result.AppliedMaxC
            });
            CurrentScaleLabel = "Escala: camera digital";
            return;
        }

        byte[] finalPixels = thermalPixels;
        if (hasVisible)
        {
            finalPixels = ImageViewMode switch
            {
                ImageViewMode.Fusion => ComposeFusion(thermalPixels, visiblePixels!, _loadedImage, IsothermThresholdC, IsothermUpperThresholdC),
                ImageViewMode.Blending => RenderComposedMode(ImageViewMode.Blending, thermalPixels, width, height, visiblePixels!),
                ImageViewMode.PiP => RenderComposedMode(ImageViewMode.PiP, thermalPixels, width, height, visiblePixels!),
                ImageViewMode.Msx => RenderComposedMode(ImageViewMode.Msx, thermalPixels, width, height, visiblePixels!),
                _ => finalPixels
            };
        }

        if (hasOriginal && originalPixels is not null && !SuppressOverlay)
        {
            // Prefixo do spot — validado com dados reais (FLIR0060, FLIR0065)
            // ~ (til) só aparece se DetectSpotApproximationMarker encontrar o marcador na imagem original
            // máx./mín. só aparecem se a escala se estender além da cena real
            string? spotLabel = _loadedImage.Metadata.SpotLabel;
            bool spotApprox = spotIsApproximate ?? false;
            if (string.IsNullOrWhiteSpace(spotLabel) && spotTemperature.HasValue)
            {
                    // Computed once per thermogram and cached
                    if (_cachedMatrixMinC == double.MaxValue)
                    {
                        double cMin = double.MaxValue, cMax = double.MinValue;
                        for (int y = 0; y < _loadedImage.Height; y++)
                            for (int x = 0; x < _loadedImage.Width; x++)
                            {
                                var t = _loadedImage.Temperatures[y, x];
                                if (t < cMin) cMin = t;
                                if (t > cMax) cMax = t;
                            }
                        _cachedMatrixMinC = cMin;
                        _cachedMatrixMaxC = cMax;
                    }
                    double matMin = _cachedMatrixMinC;
                    double matMax = _cachedMatrixMaxC;

                // Comparar quanto cada extremo da escala se estende além da cena real.
                // O lado que se estende MAIS define o prefixo (evita empate de threshold fixo).
                double deltaMax = displayScale.max - matMax; // positivo = escala acima da cena
                double deltaMin = matMin - displayScale.min; // positivo = escala abaixo da cena

                if (deltaMax > deltaMin && deltaMax > 0.05)
                    spotLabel = "máx.";
                else if (deltaMin > deltaMax && deltaMin > 0.05)
                    spotLabel = "min.";
                // ~ (aproximado) NÃO é inferido — só aparece se detectado na imagem original
                // ou definido explicitamente no metadata (SpotLabel = "~")
            }

            finalPixels = _viewPipeline.OverlayCameraUI(
                finalPixels,
                originalPixels,
                width,
                height,
                MapToCoreImageViewMode(ImageViewMode),
                SelectedPalette.ToString(),
                displayScale.min,
                displayScale.max,
                spotTemperature,
                null,
                null,
                spotApprox,
                false,
                spotLabel: spotLabel,
                spotNormX: _loadedImage.Metadata.SpotNormalizedX,
                spotNormY: _loadedImage.Metadata.SpotNormalizedY);
        }

        CurrentScaleLabel = FormatVisibleScaleLabel(_loadedImage, displayScale.min, displayScale.max);
        DisplayImage = BuildBitmap(new ThermalRenderResult
        {
            Width = width,
            Height = height,
            BgraPixels = finalPixels,
            AppliedMinC = result.AppliedMinC,
            AppliedMaxC = result.AppliedMaxC
        });
    }

    private static ImageSource BuildBitmap(ThermalRenderResult render)
    {
        var wb = new WriteableBitmap(render.Width, render.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, render.Width, render.Height), render.BgraPixels, render.Width * 4, 0);
        // Não congela (Freeze) — sempre usado na UI thread; evita alocação extra
        return wb;
    }

    private bool TryRenderThermalPixelsViaPipeline(ThermalImageData image, ThermalPalette palette, double levelMinC, double levelMaxC, out byte[] thermalPixels, out double appliedMinC, out double appliedMaxC)
    {
        thermalPixels = Array.Empty<byte>();
        appliedMinC = levelMinC;
        appliedMaxC = levelMaxC;

        try
        {
            if (palette == ThermalPalette.Original)
            {
                var radiometric = _viewPipeline.RenderRadiometric(image, new ThermalRenderParameters
                {
                    AutoScale = false,
                    LevelMinC = levelMinC,
                    LevelMaxC = levelMaxC,
                    Palette = ThermalPalette.Original
                });
                thermalPixels = radiometric.BgraPixels;
                appliedMinC = radiometric.AppliedMinC;
                appliedMaxC = radiometric.AppliedMaxC;
                return thermalPixels.Length > 0;
            }

            // Usa renderização síncrona via Task.Run para não bloquear a UI com async-over-sync
            var profile = RenderProfile.FromMetadata(image.Metadata, levelMinC, levelMaxC);
            thermalPixels = Task.Run(() => _viewPipeline.RenderRadiometricWithProfileAsync(
                image,
                palette.ToString(),
                profile)).GetAwaiter().GetResult();
            return thermalPixels.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadOriginalCameraBgraPixels(int width, int height, out byte[]? pixels)
    {
        if (_cachedOriginalPath == CurrentImagePath &&
            _cachedOriginalBgra is not null &&
            _cachedOriginalBgra.Length == width * height * 4)
        {
            pixels = _cachedOriginalBgra;
            return true;
        }
        var ok = TryLoadImageBgraPixels(CurrentImagePath, width, height, out pixels);
        if (ok && pixels is not null)
        {
            _cachedOriginalPath = CurrentImagePath;
            _cachedOriginalBgra = pixels;
        }
        return ok;
    }

    private bool TryLoadVisibleBgraPixels(int width, int height, out byte[]? pixels)
    {
            if (_cachedVisiblePath == PairedVisibleImagePath &&
                _cachedVisibleBgra is not null &&
                _cachedVisibleAlignedSize == (width, height))
            {
                pixels = _cachedVisibleBgra;
                return true;
            }

            if (TryLoadImageBgraPixelsAtNative(PairedVisibleImagePath, out var rawVisiblePixels, out var sourceWidth, out var sourceHeight)
                && rawVisiblePixels is not null)
        {
            pixels = AlignVisibleToThermalFOV(
                rawVisiblePixels,
                sourceWidth,
                sourceHeight,
                width,
                height,
                _loadedImage?.Metadata);
                _cachedVisiblePath = PairedVisibleImagePath;
                _cachedVisibleBgra = pixels;
                _cachedVisibleAlignedSize = (width, height);
            return true;
        }

        pixels = null;
        return false;
    }

    private static bool TryLoadImageBgraPixelsAtNative(string? imagePath, out byte[]? pixels, out int width, out int height)
    {
        pixels = null;
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return false;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            width = formatted.PixelWidth;
            height = formatted.PixelHeight;
            var buffer = new byte[width * height * 4];
            formatted.CopyPixels(buffer, width * 4, 0);
            pixels = buffer;
            return true;
        }
        catch { return false; }
    }

    private static bool TryLoadImageBgraPixels(string? imagePath, int width, int height, out byte[]? pixels)
    {
        pixels = null;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return false;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            BitmapSource source = bitmap;
            if (source.PixelWidth != width || source.PixelHeight != height)
            {
                source = new TransformedBitmap(source, new ScaleTransform(
                    width / (double)source.PixelWidth,
                    height / (double)source.PixelHeight));
            }

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var buffer = new byte[width * height * 4];
            formatted.CopyPixels(buffer, width * 4, 0);
            pixels = buffer;
            return true;
        }
        catch { return false; }
    }

    private static byte[] AlignVisibleToThermalFOV(
        byte[] inputPixels,
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        RadiometricMetadata? metadata)
    {
        var real2Ir = metadata?.Real2IR ?? 1.0;
        if (real2Ir <= 0.01 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return ResizeBgraNearest(inputPixels, sourceWidth, sourceHeight, outputWidth, outputHeight);
        }

        var offsetX = metadata?.OffsetX ?? 0;
        var offsetY = metadata?.OffsetY ?? 0;
        var scaledWidth = sourceWidth * real2Ir;
        var scaledHeight = sourceHeight * real2Ir;
        var cropX = (scaledWidth / 2.0) + (offsetX * real2Ir) - (sourceWidth / 2.0);
        var cropY = (scaledHeight / 2.0) + (offsetY * real2Ir) - (sourceHeight / 2.0);

        var outputPixels = new byte[outputWidth * outputHeight * 4];
        var sampleStepX = sourceWidth / (double)outputWidth;
        var sampleStepY = sourceHeight / (double)outputHeight;

        for (var y = 0; y < outputHeight; y++)
        {
            var scaledY = cropY + ((y + 0.5) * sampleStepY);
            var sourceY = (scaledY / real2Ir) - 0.5;

            for (var x = 0; x < outputWidth; x++)
            {
                var scaledX = cropX + ((x + 0.5) * sampleStepX);
                var sourceX = (scaledX / real2Ir) - 0.5;
                var dstIdx = ((y * outputWidth) + x) * 4;

                SampleBgraBilinear(inputPixels, sourceWidth, sourceHeight, sourceX, sourceY, outputPixels, dstIdx);
            }
        }

        return outputPixels;
    }

    private static byte[] ResizeBgraNearest(byte[] inputPixels, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var outputPixels = new byte[outputWidth * outputHeight * 4];
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return outputPixels;
        }

        for (var y = 0; y < outputHeight; y++)
        {
            var sy = Math.Clamp((int)Math.Round(y * (sourceHeight - 1) / (double)Math.Max(1, outputHeight - 1)), 0, sourceHeight - 1);
            for (var x = 0; x < outputWidth; x++)
            {
                var sx = Math.Clamp((int)Math.Round(x * (sourceWidth - 1) / (double)Math.Max(1, outputWidth - 1)), 0, sourceWidth - 1);
                var srcIdx = ((sy * sourceWidth) + sx) * 4;
                var dstIdx = ((y * outputWidth) + x) * 4;
                outputPixels[dstIdx] = inputPixels[srcIdx];
                outputPixels[dstIdx + 1] = inputPixels[srcIdx + 1];
                outputPixels[dstIdx + 2] = inputPixels[srcIdx + 2];
                outputPixels[dstIdx + 3] = 255;
            }
        }

        return outputPixels;
    }

    private static void SampleBgraBilinear(
        byte[] inputPixels,
        int sourceWidth,
        int sourceHeight,
        double sourceX,
        double sourceY,
        byte[] outputPixels,
        int dstIdx)
    {
        sourceX = Math.Clamp(sourceX, 0, sourceWidth - 1);
        sourceY = Math.Clamp(sourceY, 0, sourceHeight - 1);

        var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
        var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
        var x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
        var y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
        var tx = sourceX - x0;
        var ty = sourceY - y0;

        var i00 = ((y0 * sourceWidth) + x0) * 4;
        var i10 = ((y0 * sourceWidth) + x1) * 4;
        var i01 = ((y1 * sourceWidth) + x0) * 4;
        var i11 = ((y1 * sourceWidth) + x1) * 4;

        for (var c = 0; c < 3; c++)
        {
            var top = inputPixels[i00 + c] + ((inputPixels[i10 + c] - inputPixels[i00 + c]) * tx);
            var bottom = inputPixels[i01 + c] + ((inputPixels[i11 + c] - inputPixels[i01 + c]) * tx);
            outputPixels[dstIdx + c] = (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * ty)), 0, 255);
        }

        outputPixels[dstIdx + 3] = 255;
    }

    private static byte[] AlignVisibleToThermalFOV(byte[] inputPixels, int width, int height)
    {
        double real2Ir = 1.2895218;
        int offsetX = -6;
        int offsetY = 9;

        byte[] outputPixels = new byte[width * height * 4];
        double centerX = width / 2.0;
        double centerY = height / 2.0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Fórmula de mapeamento: do Térmico para o Óptico
                int vx = (int)Math.Round((x - centerX) / real2Ir + centerX + offsetX);
                int vy = (int)Math.Round((y - centerY) / real2Ir + centerY + offsetY);

                if (vx >= 0 && vx < width && vy >= 0 && vy < height)
                {
                    int srcIdx = (vy * width + vx) * 4;
                    int dstIdx = (y * width + x) * 4;
                    outputPixels[dstIdx] = inputPixels[srcIdx];
                    outputPixels[dstIdx + 1] = inputPixels[srcIdx + 1];
                    outputPixels[dstIdx + 2] = inputPixels[srcIdx + 2];
                    outputPixels[dstIdx + 3] = 255;
                }
            }
        }
        return outputPixels;
    }

    private bool TryLoadImageSource(string? path, out ImageSource? source)
    {
        source = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { source = new BitmapImage(new Uri(path)); return true; }
        catch { return false; }
    }

    private byte[] RenderComposedMode(ImageViewMode mode, byte[] thermal, int width, int height, byte[] visible)
    {
        return _viewPipeline.ComposeViewMode(
            MapToCoreImageViewMode(mode),
            thermal,
            width,
            height,
            visible,
            Math.Clamp(mode == ImageViewMode.Blending ? BlendFactor : MsxStrength, 0.0, 1.0),
            Math.Clamp(PipScale, 0.1, 0.8),
            _loadedImage);
    }



    private static byte[] ComposeFusion(byte[] thermal, byte[] visible, ThermalImageData image, double lowerLimitC, double upperLimitC)
    {
        var output = new byte[thermal.Length];
        var lower = Math.Min(lowerLimitC, upperLimitC);
        var upper = Math.Max(lowerLimitC, upperLimitC);
        var idx = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                var useThermal = t >= lower && t <= upper;
                output[idx] = useThermal ? thermal[idx] : visible[idx];
                output[idx + 1] = useThermal ? thermal[idx + 1] : visible[idx + 1];
                output[idx + 2] = useThermal ? thermal[idx + 2] : visible[idx + 2];
                output[idx + 3] = 255;
                idx += 4;
            }
        }
        return output;
    }

    private static (double min, double max) GetPreferredThermalRange(ThermalImageData image)
    {
        // ══════════════════════════════════════════════════════════════
        // TESTE: VisualScale DESLIGADO — pula direto para PaletteScale.
        // VisualScale é inferido (OCR) e pode corromper a normalização.
        // ══════════════════════════════════════════════════════════════
        // if (image.Metadata.VisualScaleMinC.HasValue && image.Metadata.VisualScaleMaxC.HasValue)
        // {
        //     var visualMin = image.Metadata.VisualScaleMinC.Value;
        //     var visualMax = image.Metadata.VisualScaleMaxC.Value;
        //     if (double.IsFinite(visualMin) && double.IsFinite(visualMax) && visualMax > visualMin)
        //     {
        //         return (visualMin, visualMax);
        //     }
        // }

        if (image.Metadata.PaletteScaleMinC.HasValue && image.Metadata.PaletteScaleMaxC.HasValue)
        {
            var min = image.Metadata.PaletteScaleMinC.Value;
            var max = image.Metadata.PaletteScaleMaxC.Value;
            if (double.IsFinite(min) && double.IsFinite(max) && max > min)
            {
                return (min, max);
            }
        }

        return GetTemperatureMatrixRange(image);
    }

    [RelayCommand]
    private void IncrementLevelMin()
    {
        AutoScaleEnabled = false;
        LevelMinC += 0.1;
    }

    [RelayCommand]
    private void DecrementLevelMin()
    {
        AutoScaleEnabled = false;
        LevelMinC -= 0.1;
    }

    [RelayCommand]
    private void IncrementLevelMax()
    {
        AutoScaleEnabled = false;
        LevelMaxC += 0.1;
    }

    [RelayCommand]
    private void DecrementLevelMax()
    {
        AutoScaleEnabled = false;
        LevelMaxC -= 0.1;
    }

    private static (double min, double max) GetDisplayedScaleRange(
        ThermalImageData image,
        double renderMinC,
        double renderMaxC,
        bool preferCameraScale)
    {
        if (preferCameraScale &&
            image.Metadata.VisualScaleMinC.HasValue &&
            image.Metadata.VisualScaleMaxC.HasValue)
        {
            var visualMin = image.Metadata.VisualScaleMinC.Value;
            var visualMax = image.Metadata.VisualScaleMaxC.Value;
            if (double.IsFinite(visualMin) && double.IsFinite(visualMax) && visualMax > visualMin)
            {
                return (visualMin, visualMax);
            }
        }

        if (preferCameraScale &&
            image.Metadata.PaletteScaleMinC.HasValue &&
            image.Metadata.PaletteScaleMaxC.HasValue)
        {
            var min = image.Metadata.PaletteScaleMinC.Value;
            var max = image.Metadata.PaletteScaleMaxC.Value;
            if (double.IsFinite(min) && double.IsFinite(max) && max > min)
            {
                return (min, max);
            }
        }

        return (renderMinC, renderMaxC);
    }

    private static (double min, double max) GetTemperatureMatrixRange(ThermalImageData image)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }
        return (min, max);
    }

    private void SetScaleSliderLimits(ThermalImageData image)
    {
        var (matrixMin, matrixMax) = GetTemperatureMatrixRange(image);
        if (!double.IsFinite(matrixMin) || !double.IsFinite(matrixMax) || matrixMax <= matrixMin)
        {
            ThermalScaleFloorC = 0.0;
            ThermalScaleCeilingC = 100.0;
            return;
        }

        var floor = matrixMin;
        var ceiling = matrixMax;
        if (image.Metadata.PaletteScaleMinC.HasValue)
        {
            floor = Math.Min(floor, image.Metadata.PaletteScaleMinC.Value);
        }
        if (image.Metadata.PaletteScaleMaxC.HasValue)
        {
            ceiling = Math.Max(ceiling, image.Metadata.PaletteScaleMaxC.Value);
        }
        if (image.Metadata.VisualScaleMinC.HasValue)
        {
            floor = Math.Min(floor, image.Metadata.VisualScaleMinC.Value);
        }
        if (image.Metadata.VisualScaleMaxC.HasValue)
        {
            ceiling = Math.Max(ceiling, image.Metadata.VisualScaleMaxC.Value);
        }

        var padding = Math.Max(1.0, (ceiling - floor) * 0.08);
        ThermalScaleFloorC = Math.Floor((floor - padding) * 10.0) / 10.0;
        ThermalScaleCeilingC = Math.Ceiling((ceiling + padding) * 10.0) / 10.0;
    }

    private static string FormatVisibleScaleLabel(ThermalImageData image, double renderMinC, double renderMaxC)
    {
        var source = image.Metadata.VisualScaleSource switch
        {
            VisualScaleSource.BurnedInScale => "camera",
            VisualScaleSource.VisualFitToReference => "ajuste visual",
            VisualScaleSource.ExifImageTemperature => "EXIF",
            VisualScaleSource.MatrixRange => "matriz",
            VisualScaleSource.Manual => "manual",
            _ => "render"
        };
        return $"Escala visual ({source}): {renderMinC:F1} C - {renderMaxC:F1} C";
    }

    private static double? GetSpotTemperature(ThermalImageData image, byte[]? originalPixels, (double min, double max) displayScale)
    {
        if (image.Metadata.SpotTemperatureC.HasValue)
        {
            return image.Metadata.SpotTemperatureC.Value;
        }

        if (TryGetMetadataSpotCenter(image, out var metadataSpotX, out var metadataSpotY))
        {
            return GetFlirReticleSpotTemperature(image, metadataSpotX, metadataSpotY);
        }

        if (IsFlirCamera(image.Metadata) &&
            image.Metadata.PaletteScaleMinC.HasValue &&
            image.Metadata.PaletteScaleMaxC.HasValue)
        {
            if (TryDetectFlirReticleCenter(originalPixels, image.Width, image.Height, out var reticleX, out var reticleY))
            {
                return GetFlirReticleSpotTemperature(image, reticleX, reticleY);
            }

            return GetFlirReticleSpotTemperature(image, image.Width / 2.0, image.Height / 2.0);
        }

        var x = Math.Clamp(image.Width / 2, 0, image.Width - 1);
        var y = Math.Clamp(image.Height / 2, 0, image.Height - 1);
        return image.Temperatures[y, x];
    }

    private static bool TryGetMetadataSpotCenter(ThermalImageData image, out double x, out double y)
    {
        x = image.Width / 2.0;
        y = image.Height / 2.0;
        if (!image.Metadata.SpotNormalizedX.HasValue || !image.Metadata.SpotNormalizedY.HasValue)
        {
            return false;
        }

        var nx = image.Metadata.SpotNormalizedX.Value;
        var ny = image.Metadata.SpotNormalizedY.Value;
        if (!double.IsFinite(nx) || !double.IsFinite(ny))
        {
            return false;
        }

        x = Math.Clamp(nx, 0.0, 1.0) * Math.Max(0, image.Width - 1);
        y = Math.Clamp(ny, 0.0, 1.0) * Math.Max(0, image.Height - 1);
        return true;
    }

    private static bool IsFlirCamera(RadiometricMetadata metadata)
    {
        return metadata.Detector.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
            metadata.CameraModel.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
            metadata.Manufacturer.Contains("FLIR", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Temperatura no pixel exato da mira (centro do retículo).
    /// Usa mediana 3×3 para suavizar ruído sem distorcer o valor.
    /// </summary>
    private static double? GetFlirReticleSpotTemperature(ThermalImageData image, double centerX, double centerY)
    {
        int cx = Math.Clamp((int)Math.Round(centerX), 0, image.Width - 1);
        int cy = Math.Clamp((int)Math.Round(centerY), 0, image.Height - 1);

        // Mediana 3×3 para reduzir ruído do sensor
        var values = new List<double>(9);
        for (int y = Math.Max(0, cy - 1); y <= Math.Min(image.Height - 1, cy + 1); y++)
            for (int x = Math.Max(0, cx - 1); x <= Math.Min(image.Width - 1, cx + 1); x++)
            {
                var v = image.Temperatures[y, x];
                if (double.IsFinite(v)) values.Add(v);
            }

        if (values.Count == 0) return null;
        values.Sort();
        return values[values.Count / 2]; // mediana
    }

    private static bool TryDetectFlirReticleCenter(byte[]? originalPixels, int width, int height, out double centerX, out double centerY)
    {
        // Delega para implementação canônica (FlirCameraUiOverlay)
        return FlirCameraUiOverlay.TryDetectReticleCenter(originalPixels, width, height, out centerX, out centerY);
    }

    private static bool DetectSpotApproximationMarker(byte[] originalPixels, int width, int height)
    {
        if (originalPixels.Length != width * height * 4 || width <= 0 || height <= 0)
        {
            return false;
        }

        var sx = width / 320.0;
        var sy = height / 240.0;
        var x1 = Math.Clamp((int)Math.Round(4 * sx), 0, width - 1);
        var x2 = Math.Clamp((int)Math.Round(9 * sx), 0, width - 1);
        var y1 = Math.Clamp((int)Math.Round(8 * sy), 0, height - 1);
        var y2 = Math.Clamp((int)Math.Round(18 * sy), 0, height - 1);

        var brightCount = 0;
        var rowHits = new HashSet<int>();
        for (var y = y1; y <= y2; y++)
        {
            for (var x = x1; x <= x2; x++)
            {
                var idx = ((y * width) + x) * 4;
                var b = originalPixels[idx];
                var g = originalPixels[idx + 1];
                var r = originalPixels[idx + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var brightness = (r + g + b) / 3;

                if (brightness > 150 && max - min < 100)
                {
                    brightCount++;
                    rowHits.Add(y);
                }
            }
        }

        return brightCount >= 3 && rowHits.Count >= 2;
    }

    private static (double min, double max) GetRegionRange(ThermalImageData image, (double startX, double startY, double endX, double endY) region)
    {
        var x1 = Math.Clamp((int)Math.Round(region.startX * (image.Width - 1)), 0, image.Width - 1);
        var y1 = Math.Clamp((int)Math.Round(region.startY * (image.Height - 1)), 0, image.Height - 1);
        var x2 = Math.Clamp((int)Math.Round(region.endX * (image.Width - 1)), 0, image.Width - 1);
        var y2 = Math.Clamp((int)Math.Round(region.endY * (image.Height - 1)), 0, image.Height - 1);
        var minX = Math.Min(x1, x2);
        var maxX = Math.Max(x1, x2);
        var minY = Math.Min(y1, y2);
        var maxY = Math.Max(y1, y2);
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var t = image.Temperatures[y, x];
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }
        return (min, max);
    }

    private string? AutoDetectVisiblePairPath(string thermalPath)
    {
        var dir = Path.GetDirectoryName(thermalPath);
        var name = Path.GetFileNameWithoutExtension(thermalPath);
        var extensions = new[] { ".jpg", ".jpeg", ".png" };
        foreach (var ext in extensions)
        {
            var path = Path.Combine(dir ?? string.Empty, name + "_v" + ext);
            if (File.Exists(path)) return path;
            path = Path.Combine(dir ?? string.Empty, name + "V" + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private string NormalizeVisibleImagePath(string? visiblePath, string thermalPath)
    {
        if (string.IsNullOrWhiteSpace(visiblePath)) return string.Empty;
        if (Path.IsPathRooted(visiblePath) && File.Exists(visiblePath)) return visiblePath;
        var dir = Path.GetDirectoryName(thermalPath);
        var combined = Path.Combine(dir ?? string.Empty, Path.GetFileName(visiblePath));
        return File.Exists(combined) ? combined : string.Empty;
    }

    private bool TryEnsureVisiblePairOnDemand()
    {
        if (string.IsNullOrWhiteSpace(CurrentImagePath)) return false;
        var path = AutoDetectVisiblePairPath(CurrentImagePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PairedVisibleImagePath = path;
            return true;
        }
        return false;
    }

    private static ThermalPalette ResolvePaletteFromMetadata(RadiometricMetadata metadata)
    {
        if (metadata.DetectedPalette.HasValue) return metadata.DetectedPalette.Value;
        if (!string.IsNullOrWhiteSpace(metadata.PaletteName) && Enum.TryParse<ThermalPalette>(metadata.PaletteName, true, out var palette)) return palette;
        return ThermalPalette.Iron;
    }

    private static ThermalPalette MapPaletteFromMetadata(string? paletteName)
    {
        if (string.IsNullOrWhiteSpace(paletteName)) return ThermalPalette.Iron;
        if (Enum.TryParse<ThermalPalette>(paletteName, true, out var palette)) return palette;
        return ThermalPalette.Iron;
    }

    private static ThermalPalette NormalizeSupportedPalette(ThermalPalette palette)
    {
        if (palette == ThermalPalette.Original) return ThermalPalette.Iron;
        return palette;
    }

    private static string GetViewModeDisplay(ImageViewMode mode) => mode switch
    {
        ImageViewMode.Original => "Original da Câmera",
        ImageViewMode.Thermal => "Térmico",
        ImageViewMode.Visible => "Digital (Visível)",
        ImageViewMode.Fusion => "Fusão Térmica",
        ImageViewMode.Blending => "Sobreposição (Blending)",
        ImageViewMode.PiP => "Picture-in-Picture",
        ImageViewMode.Msx => "MSX (Contornos)",
        _ => mode.ToString()
    };
}
