using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.Core.Services;

/// <summary>
/// Sobrepõe elementos de UI da câmera FLIR sobre imagem renderizada.
/// </summary>
public interface IFlirCameraUiOverlay
{
    byte[] Overlay(
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
