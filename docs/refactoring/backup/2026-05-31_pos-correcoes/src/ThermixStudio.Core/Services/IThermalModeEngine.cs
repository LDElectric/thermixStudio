using System.Drawing;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.Core.Services;

/// <summary>
/// Interface para motor de composição de modos térmicos.
/// </summary>
public interface IThermalModeEngine
{
    byte[] RenderMode(
        ImageViewMode mode,
        byte[] thermalPixels,
        int width, int height,
        byte[]? visiblePixels,
        double intensity,
        double pipScale,
        ThermalImageData? thermalData = null);

    bool ModeRequiresVisible(ImageViewMode mode);
}
