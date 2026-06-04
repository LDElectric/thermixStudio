using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.Services;

/// <summary>
/// Motor de composição de modos térmicos (MSX, Blending, PiP, Visible, Thermal).
/// Detecção de modo: <see cref="IThermalModeDetectionService"/>.
/// UI FLIR: <see cref="FlirCameraUiOverlay"/>.
/// </summary>
public sealed class ThermalModeEngine : IThermalModeEngine
{
    public byte[] RenderMode(
        ImageViewMode mode,
        byte[] thermalPixels, int width, int height,
        byte[]? visiblePixels,
        double intensity, double pipScale,
        ThermalImageData? thermalData = null)
    {
        bool hasVisible = visiblePixels is not null && visiblePixels.Length == width * height * 4;

        return mode switch
        {
            ImageViewMode.Thermal => ComposeThermalPure(thermalPixels),
            ImageViewMode.Visible when hasVisible => ComposeVisiblePure(visiblePixels!),
            ImageViewMode.Visible when !hasVisible => ComposeThermalPure(thermalPixels),
            ImageViewMode.Blending when hasVisible => ComposeBlendingAlphaLinear(thermalPixels, visiblePixels!, width, height, intensity),
            ImageViewMode.Blending when !hasVisible => ComposeThermalPure(thermalPixels),
            ImageViewMode.PiP when hasVisible => ComposePictureInPicture(thermalPixels, visiblePixels!, width, height, pipScale),
            ImageViewMode.PiP when !hasVisible => ComposeThermalPure(thermalPixels),
            ImageViewMode.Msx when hasVisible => ComposeMsx(thermalPixels, visiblePixels!, width, height, intensity),
            ImageViewMode.Msx when !hasVisible => ComposeThermalPure(thermalPixels),
            _ => thermalPixels
        };
    }

    public bool ModeRequiresVisible(ImageViewMode mode) =>
        mode is ImageViewMode.Visible or ImageViewMode.Fusion or ImageViewMode.Blending or ImageViewMode.PiP or ImageViewMode.Msx;

    private static byte[] ComposeThermalPure(byte[] thermalPixels)
    {
        // Retorna referência direta — o caller (UpdateDisplayImage) não modifica o array retornado
        return thermalPixels;
    }

    private static byte[] ComposeVisiblePure(byte[] visiblePixels)
    {
        // Retorna referência direta — o caller (UpdateDisplayImage) não modifica o array retornado
        return visiblePixels;
    }

    /// <summary>
    /// Blending que preserva a saturação das cores térmicas usando a luminância
    /// da imagem visível. Em alpha=0: 100% visível. Em alpha=1: 100% térmica.
    /// No meio, as cores da paleta térmica aparecem sobre a estrutura da visível.
    /// </summary>
    private static byte[] ComposeBlendingAlphaLinear(
        byte[] thermalPixels, byte[] visiblePixels,
        int width, int height,
        double alpha)
    {
        var resultado = new byte[thermalPixels.Length];

        for (int i = 0; i < thermalPixels.Length; i += 4)
        {
            // Extrair luminância relativa da visível (Rec.601)
            double visY = visiblePixels[i + 2] * 0.299 + visiblePixels[i + 1] * 0.587 + visiblePixels[i] * 0.114;
            double visU = visiblePixels[i]     - visY; // B - Y (approx Cb)
            double visV = visiblePixels[i + 2] - visY; // R - Y (approx Cr)

            // Crominância da térmica (cores da paleta)
            double thmY = thermalPixels[i + 2] * 0.299 + thermalPixels[i + 1] * 0.587 + thermalPixels[i] * 0.114;
            double thmU = thermalPixels[i]     - thmY;
            double thmV = thermalPixels[i + 2] - thmY;

            // Luminância: blend entre visível e térmica
            double outY = visY * (1.0 - alpha) + thmY * alpha;

            // Crominância: sempre da térmica, com força controlada por alpha
            double outU = thmU * alpha;
            double outV = thmV * alpha;

            // Recompor RGB
            double r = outY + outV;
            double b = outY + outU;
            double g = (outY - 0.299 * r - 0.114 * b) / 0.587;

            resultado[i]     = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
            resultado[i + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
            resultado[i + 2] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
            resultado[i + 3] = 255;
        }

        return resultado;
    }

    private static byte[] ComposeMsx(
        byte[] thermalPixels, byte[] visiblePixels,
        int width, int height,
        double ganhoContorno)
    {
        var resultado = new byte[thermalPixels.Length];
        int stride = width * 4;

        Array.Copy(thermalPixels, resultado, thermalPixels.Length);

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int idxC = (y * stride) + (x * 4);


                int idxT = ((y - 1) * stride) + (x * 4);
                int idxB = ((y + 1) * stride) + (x * 4);
                int idxL = (y * stride) + ((x - 1) * 4);
                int idxR = (y * stride) + ((x + 1) * 4);

                // Rec.601 luma: B=0.114, G=0.587, R=0.299 (BGRA pixel order)
                int cLuma = (int)(visiblePixels[idxC] * 0.114 + visiblePixels[idxC + 1] * 0.587 + visiblePixels[idxC + 2] * 0.299);
                int tLuma = (int)(visiblePixels[idxT] * 0.114 + visiblePixels[idxT + 1] * 0.587 + visiblePixels[idxT + 2] * 0.299);
                int bLuma = (int)(visiblePixels[idxB] * 0.114 + visiblePixels[idxB + 1] * 0.587 + visiblePixels[idxB + 2] * 0.299);
                int lLuma = (int)(visiblePixels[idxL] * 0.114 + visiblePixels[idxL + 1] * 0.587 + visiblePixels[idxL + 2] * 0.299);
                int rLuma = (int)(visiblePixels[idxR] * 0.114 + visiblePixels[idxR + 1] * 0.587 + visiblePixels[idxR + 2] * 0.299);

                int laplaciano = (4 * cLuma) - tLuma - bLuma - lLuma - rLuma;
                // Ganho normalizado: ~0.25 = MSX suave, ~0.60 = MSX padrão FLIR, ~1.0 = máximo
                int realce = (int)(laplaciano * ganhoContorno * 2.5);

                int dest = (y * stride) + (x * 4);
                resultado[dest]     = (byte)Math.Clamp(thermalPixels[dest]     + realce, 0, 255);
                resultado[dest + 1] = (byte)Math.Clamp(thermalPixels[dest + 1] + realce, 0, 255);
                resultado[dest + 2] = (byte)Math.Clamp(thermalPixels[dest + 2] + realce, 0, 255);
                resultado[dest + 3] = 255;
            }
        }

        return resultado;
    }

    private static byte[] ComposePictureInPicture(
        byte[] thermalPixels, byte[] visiblePixels,
        int width, int height,
        double pipScale)
    {
        using var visivelBmp = BitmapFromBgra(width, height, visiblePixels);
        using var termicaBmp = BitmapFromBgra(width, height, thermalPixels);
        using var resultBmp = new Bitmap(visivelBmp);
        using (var g = Graphics.FromImage(resultBmp))
        {
            int pipW = (int)(width * Math.Clamp(pipScale, 0.3, 0.7));
            int pipH = (int)(height * Math.Clamp(pipScale, 0.3, 0.7));
            int pipX = (width - pipW) / 2;
            int pipY = (height - pipH) / 2;

            int cropW = (int)(termicaBmp.Width * Math.Clamp(pipScale, 0.3, 0.7));
            int cropH = (int)(termicaBmp.Height * Math.Clamp(pipScale, 0.3, 0.7));
            int cropX = (termicaBmp.Width - cropW) / 2;
            int cropY = (termicaBmp.Height - cropH) / 2;

            var sourceRect = new Rectangle(cropX, cropY, cropW, cropH);
            var destRect = new Rectangle(pipX, pipY, pipW, pipH);

            g.DrawImage(termicaBmp, destRect, sourceRect, GraphicsUnit.Pixel);

            using var pen = new Pen(Color.White, 3);
            g.DrawRectangle(pen, destRect);
        }

        return BgraFromBitmap(resultBmp);
    }

    private static Bitmap BitmapFromBgra(int width, int height, byte[] bgraData)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        Marshal.Copy(bgraData, 0, bmpData.Scan0, bgraData.Length);
        bmp.UnlockBits(bmpData);

        return bmp;
    }

    private static byte[] BgraFromBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte[] buffer = new byte[bmpData.Stride * bmp.Height];
        Marshal.Copy(bmpData.Scan0, buffer, 0, buffer.Length);
        bmp.UnlockBits(bmpData);

        return buffer;
    }
}
