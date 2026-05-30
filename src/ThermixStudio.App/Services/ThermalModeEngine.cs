using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.Services;

/// <summary>
/// Motor de composição de modos térmicos extraído de modos_CS.
/// Implementa renderização de:
/// - Térmica Pura
/// - MSX (Laplaciano + edge detection)
/// - Blending (Alpha linear)
/// - PiP (50% central)
/// - Visible (Luz visível pura)
/// - Fusion (Intervalo de temperatura)
/// </summary>
public sealed class ThermalModeEngine : IThermalModeEngine
{
    private readonly IExifToolService _exifTool;

    public ThermalModeEngine(IExifToolService exifTool)
    {
        _exifTool = exifTool;
    }

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

    public async Task<ImageViewMode?> TryDetectOriginalModeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        return await _exifTool.TryDetectModeAsync(imagePath, cancellationToken);
    }

    public bool ModeRequiresVisible(ImageViewMode mode) =>
        mode is ImageViewMode.Visible or ImageViewMode.Fusion or ImageViewMode.Blending or ImageViewMode.PiP or ImageViewMode.Msx;

    #region Composição de Modos (Algoritmos de modos_CS)

    private static byte[] ComposeThermalPure(byte[] thermalPixels)
    {
        return (byte[])thermalPixels.Clone();
    }

    private static byte[] ComposeVisiblePure(byte[] visiblePixels)
    {
        return (byte[])visiblePixels.Clone();
    }

    private static byte[] ComposeBlendingAlphaLinear(
        byte[] thermalPixels, byte[] visiblePixels,
        int width, int height,
        double alpha)
    {
        var resultado = new byte[thermalPixels.Length];

        for (int i = 0; i < thermalPixels.Length; i += 4)
        {
            resultado[i]     = (byte)((thermalPixels[i] * alpha)     + (visiblePixels[i] * (1 - alpha)));     // B
            resultado[i + 1] = (byte)((thermalPixels[i + 1] * alpha) + (visiblePixels[i + 1] * (1 - alpha))); // G
            resultado[i + 2] = (byte)((thermalPixels[i + 2] * alpha) + (visiblePixels[i + 2] * (1 - alpha))); // R
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

    // Copia térmica como base do resultado
    Array.Copy(thermalPixels, resultado, thermalPixels.Length);

    // A correção de paralaxe (FOV + offset) agora é feita na fonte (AlignVisibleToThermalFOV)
    // ao carregar a imagem visível. Portanto, aqui a correspondência é 1:1.
    for (int y = 1; y < height - 1; y++)
    {
        for (int x = 1; x < width - 1; x++)
        {
            int vy = y;
            int vx = x;

            // Índices dos 5 vizinhos na imagem VISÍVEL (paralaxe corrigida)
            int idxC = (vy       * stride) + (vx       * 4);
            int idxT = ((vy - 1) * stride) + (vx       * 4);
            int idxB = ((vy + 1) * stride) + (vx       * 4);
            int idxL = (vy       * stride) + ((vx - 1) * 4);
            int idxR = (vy       * stride) + ((vx + 1) * 4);

            // Luminância dos vizinhos (imagem visível)
            int cLuma = (visiblePixels[idxC] + visiblePixels[idxC + 1] + visiblePixels[idxC + 2]) / 3;
            int tLuma = (visiblePixels[idxT] + visiblePixels[idxT + 1] + visiblePixels[idxT + 2]) / 3;
            int bLuma = (visiblePixels[idxB] + visiblePixels[idxB + 1] + visiblePixels[idxB + 2]) / 3;
            int lLuma = (visiblePixels[idxL] + visiblePixels[idxL + 1] + visiblePixels[idxL + 2]) / 3;
            int rLuma = (visiblePixels[idxR] + visiblePixels[idxR + 1] + visiblePixels[idxR + 2]) / 3;

            // Magnitude do Laplaciano (pode ser positivo ou negativo)
            int laplaciano = (4 * cLuma) - tLuma - bLuma - lLuma - rLuma;

            // MSX da FLIR atua de forma aditiva/emboss: 
            // Bordas de luz criam delineados brancos, sombras criam delineados escuros.
            int realce = (int)(laplaciano * ganhoContorno * 1.15);

            // Aplica no pixel TÉRMICO na posição original (1:1)
            int dest = (y * stride) + (x * 4);
            resultado[dest]     = (byte)Math.Clamp(thermalPixels[dest]     + realce, 0, 255); // B
            resultado[dest + 1] = (byte)Math.Clamp(thermalPixels[dest + 1] + realce, 0, 255); // G
            resultado[dest + 2] = (byte)Math.Clamp(thermalPixels[dest + 2] + realce, 0, 255); // R
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
        // Cria bitmap de resultado iniciado com visível
        using (var visivelBmp = BitmapFromBgra(width, height, visiblePixels))
        using (var termicaBmp = BitmapFromBgra(width, height, thermalPixels))
        using (var resultBmp = new Bitmap(visivelBmp))
        {
            using (var g = Graphics.FromImage(resultBmp))
            {
                // Dimensões da janela PiP
                int pipW = (int)(width * Math.Clamp(pipScale, 0.3, 0.7));
                int pipH = (int)(height * Math.Clamp(pipScale, 0.3, 0.7));
                int pipX = (width - pipW) / 2;
                int pipY = (height - pipH) / 2;

                // Sub-região da térmica (recorte central)
                int cropW = (int)(termicaBmp.Width * Math.Clamp(pipScale, 0.3, 0.7));
                int cropH = (int)(termicaBmp.Height * Math.Clamp(pipScale, 0.3, 0.7));
                int cropX = (termicaBmp.Width - cropW) / 2;
                int cropY = (termicaBmp.Height - cropH) / 2;

                var sourceRect = new Rectangle(cropX, cropY, cropW, cropH);
                var destRect = new Rectangle(pipX, pipY, pipW, pipH);

                g.DrawImage(termicaBmp, destRect, sourceRect, GraphicsUnit.Pixel);

                // Moldura branca
                using (var pen = new Pen(Color.White, 3))
                {
                    g.DrawRectangle(pen, destRect);
                }
            }

            return BgraFromBitmap(resultBmp);
        }
    }

    #endregion

    #region Utilitários de Bitmap

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

    #endregion

    #region Sobreposição de UI da Câmera

    /// <summary>
    /// Sobrepõe os elementos de UI da câmera original sobre a imagem renderizada final,
    /// usando bounding boxes precisos mapeados para cada elemento específico da câmera FLIR.
    ///
    /// Elementos preservados (do pixel original):
    ///   - Temperatura do alvo (~41°C): topo-esquerdo
    ///   - Tmax: topo-direito
    ///   - Tmin: base-direita
    ///   - Logo FLIR: base-esquerda (área restrita para não pegar a cena)
    ///   - Borda da barra de escala: apenas a moldura preta (o gradiente de cor é renderizado separado)
    ///   - Crosshair/mira: centro
    ///
    /// Critério por pixel: baixa saturação (cinza/preto/branco) + muito escuro ou muito claro.
    /// </summary>
    public byte[] OverlayCameraUI(
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
        bool preferOriginalTemperatureText = false)
    {
        if (finalPixels is null || finalPixels.Length != width * height * 4)
            return Array.Empty<byte>();

        var result = (byte[])finalPixels.Clone();
        var useProgrammaticOverlay = true;
        if (useProgrammaticOverlay)
        {
            if (mode != ImageViewMode.Visible && scaleLut is not null && scaleLut.Rgb.Count > 0)
            {
                double sx0 = width / 320.0;
                double sy0 = height / 240.0;
                DrawPaletteScaleBar(
                    result,
                    width,
                    height,
                    scaleLut,
                    (int)(305 * sx0),
                    (int)(30 * sy0),
                    (int)(312 * sx0),
                    (int)(207 * sy0));
            }
            else if (mode != ImageViewMode.Visible && originalPixels is not null && originalPixels.Length == width * height * 4)
            {
                double sx0 = width / 320.0;
                double sy0 = height / 240.0;
                CopyOriginalRectangle(
                    result,
                    originalPixels,
                    width,
                    height,
                    (int)(300 * sx0),
                    (int)(28 * sy0),
                    (int)(318 * sx0),
                    (int)(193 * sy0));
            }

            var reticleX = width / 2.0;
            var reticleY = height / 2.0;
            if (originalPixels is not null &&
                originalPixels.Length == width * height * 4)
            {
                TryDetectFlirReticleCenter(originalPixels, width, height, out reticleX, out reticleY);
            }

            DrawProgrammaticFlirUi(
                result,
                width,
                height,
                mode,
                scaleMinC,
                scaleMaxC,
                spotTemperatureC,
                maxTemperatureC,
                minTemperatureC,
                spotIsApproximate ?? false,
                reticleX,
                reticleY,
                drawReticle: true);

            if (preferOriginalTemperatureText &&
                originalPixels is not null &&
                originalPixels.Length == width * height * 4)
            {
                OverlayOriginalTemperatureTextBoxes(result, originalPixels, width, height, mode);
            }

            if (originalPixels is not null && originalPixels.Length == width * height * 4)
            {
                OverlayFlirLogoOnly(result, originalPixels, width, height);
            }

            return result;
        }

        int stride = width * 4;

        // Thresholds de detecção (calibrados)
        const int darkThreshold   = 60;   // fundos pretos das caixas
        const int brightThreshold = 170;  // textos brancos e logo
        const int maxSaturation   = 40;   // tolerância de cor (cinza + quase-cinza)

        // ── Bounding boxes precisos por elemento (proporcionais à resolução) ──
        // Calibrados para evitar as bordas onde pixels mistos da cena térmica vazam
        double sx = width  / 320.0;
        double sy = height / 240.0;

        bool visibleMode = mode == ImageViewMode.Visible;

        if (!visibleMode)
        {
            if (scaleLut is not null && scaleLut.Rgb.Count > 0)
            {
                DrawPaletteScaleBar(
                    result,
                    width,
                    height,
                    scaleLut,
                    (int)(304 * sx),
                    (int)(30 * sy),
                    (int)(313 * sx),
                    (int)(207 * sy));
            }
            else if (copyOriginalScaleBar)
            {
                CopyOriginalRectangle(
                    result,
                    originalPixels,
                    width,
                    height,
                    (int)(302 * sx),
                    (int)(28 * sy),
                    (int)(316 * sx),
                    (int)(210 * sy));
            }
        }

        var uiBoxes = new (int x1, int y1, int x2, int y2)[]
        {
            // Temperatura do alvo (topo-esquerdo)
            ((int)(  2*sx), (int)(  2*sy), (int)( 96*sx), (int)( 28*sy)),
            // Tmax (topo-direito)
            ((int)(275*sx), (int)(  2*sy), (int)(318*sx), (int)( 28*sy)),
            // Tmin (base-direita)
            ((int)(275*sx), (int)(210*sy), (int)(318*sx), (int)(238*sy)),
            // Logo FLIR (base-esquerda)
            ((int)(  2*sx), (int)(210*sy), (int)(100*sx), (int)(238*sy)),
            // Moldura da barra de escala e números (coluna de borda preta à direita e números)
            ((int)(290*sx), (int)( 25*sy), (int)(318*sx), (int)(215*sy)),
            // Crosshair / mira (centro)
            ((int)(130*sx), (int)(95*sy), (int)(190*sx), (int)(145*sy)),
        };

        for (int i = 0; i < uiBoxes.Length; i++)
        {
            if (visibleMode && (i == 1 || i == 2 || i == 4))
            {
                continue;
            }

            var (bx1, by1, bx2, by2) = uiBoxes[i];
            int clampX2 = Math.Min(bx2, width  - 1);
            int clampY2 = Math.Min(by2, height - 1);

            // O logo FLIR (índice 3) é branco e não possui caixa de fundo preta.
            // Para evitar capturar o cabo (que é escuro), usamos apenas o threshold de brilho alto.
            bool isLogoArea = (i == 3);
            bool isScaleArea = (i == 4);
            bool isCrosshairArea = (i == 5);

            for (int y = Math.Max(by1, 0); y <= clampY2; y++)
            {
                for (int x = Math.Max(bx1, 0); x <= clampX2; x++)
                {
                    if (isScaleArea && scaleLut is not null && IsInsideScaleBarFill(x, y, width, height))
                    {
                        continue;
                    }

                    int idx = (y * stride) + (x * 4);

                    byte ob  = originalPixels[idx];      // B
                    byte og  = originalPixels[idx + 1];  // G
                    byte or_ = originalPixels[idx + 2];  // R

                    int brightness = (or_ + og + ob) / 3;
                    int maxC = Math.Max(or_, Math.Max(og, ob));
                    int minC = Math.Min(or_, Math.Min(og, ob));
                    int sat  = maxC - minC;

                    bool match;
                    if (isLogoArea)
                    {
                        // Apenas elementos brancos no logo (ignora o cabo escuro)
                        match = sat <= 30 && brightness > 180;
                    }
                    else if (isCrosshairArea)
                    {
                        match = IsNearCrosshairLine(x, y, width, height)
                            && sat <= 75
                            && (brightness < 115 || brightness > 135);
                    }
                    else
                    {
                        // Critério geral para as outras áreas (caixas pretas + texto branco)
                        match = sat <= maxSaturation && (brightness < darkThreshold || brightness > brightThreshold);
                    }

                    if (match)
                    {
                        result[idx]     = ob;
                        result[idx + 1] = og;
                        result[idx + 2] = or_;
                        result[idx + 3] = originalPixels[idx + 3];
                    }
                }
            }
        }

        return result;
    }

    private static bool IsNearCrosshairLine(int x, int y, int width, int height)
    {
        double sx = width / 320.0;
        double sy = height / 240.0;
        double cx = 160.0 * sx;
        double cy = 120.0 * sy;

        bool vertical = Math.Abs(x - cx) <= Math.Max(2.0, 2.0 * sx)
            && y >= (int)(102 * sy)
            && y <= (int)(138 * sy);

        bool horizontal = Math.Abs(y - cy) <= Math.Max(2.0, 2.0 * sy)
            && x >= (int)(138 * sx)
            && x <= (int)(182 * sx);

        bool ticks =
            (Math.Abs(x - (146.0 * sx)) <= Math.Max(2.0, 2.0 * sx) && Math.Abs(y - cy) <= Math.Max(4.0, 4.0 * sy)) ||
            (Math.Abs(x - (174.0 * sx)) <= Math.Max(2.0, 2.0 * sx) && Math.Abs(y - cy) <= Math.Max(4.0, 4.0 * sy)) ||
            (Math.Abs(y - (108.0 * sy)) <= Math.Max(2.0, 2.0 * sy) && Math.Abs(x - cx) <= Math.Max(4.0, 4.0 * sx)) ||
            (Math.Abs(y - (132.0 * sy)) <= Math.Max(2.0, 2.0 * sy) && Math.Abs(x - cx) <= Math.Max(4.0, 4.0 * sx));

        return vertical || horizontal || ticks;
    }

    private static void DrawProgrammaticFlirUi(
        byte[] pixels,
        int width,
        int height,
        ImageViewMode mode,
        double? scaleMinC,
        double? scaleMaxC,
        double? spotTemperatureC,
        double? maxTemperatureC,
        double? minTemperatureC,
        bool spotIsApproximate,
        double? reticleCenterX = null,
        double? reticleCenterY = null,
        bool drawReticle = true)
    {
        // ── Fase 1: GDI+ para caixas, bordas e retícula (curvas suaves) ──
        using var bitmap = BitmapFromBgra(width, height, pixels);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        float sx = width / 320f;
        float sy = height / 240f;
        float s = MathF.Max(0.75f, MathF.Min(sx, sy));
        bool visibleMode = mode == ImageViewMode.Visible;

        using var boxBrush = new SolidBrush(Color.Black);
        using var borderPen = new Pen(Color.Black, Math.Max(1f, s));
        using var scalePen = new Pen(Color.Black, Math.Max(1.5f, 1.6f * s));
        using var reticleShadowPen = new Pen(Color.Black, Math.Max(2.4f, 2.8f * s));
        using var reticlePen = new Pen(Color.White, Math.Max(1.4f, 1.65f * s));

        float radius = 1.5f * s;

        // Caixa do spot (topo-esquerda) — apenas a caixa preta, sem texto
        var spotBoxRect = new RectangleF(4 * sx, 4 * sy, 88 * sx, 21 * sy);
        DrawBoxOnly(g, spotBoxRect, boxBrush, borderPen, radius);

        if (!visibleMode)
        {
            // Caixa do Tmax (topo-direita)
            var topBoxRect = new RectangleF(277 * sx, 4 * sy, 39 * sx, 21 * sy);
            DrawBoxOnly(g, topBoxRect, boxBrush, borderPen, radius);

            // Caixa do Tmin (base-direita)
            var bottomBoxRect = new RectangleF(278 * sx, 214 * sy, 38 * sx, 21 * sy);
            DrawBoxOnly(g, bottomBoxRect, boxBrush, borderPen, radius);

            // Borda da barra de escala
            var scaleOuter = new RectangleF(303 * sx, 28 * sy, 12 * sx, 181 * sy);
            g.DrawRectangle(scalePen, scaleOuter.X, scaleOuter.Y, scaleOuter.Width, scaleOuter.Height);
        }

        if (drawReticle)
        {
            var reticleX = (float)(reticleCenterX ?? (160 * sx));
            var reticleY = (float)(reticleCenterY ?? (120 * sy));
            DrawReticle(g, reticleX, reticleY, sx, sy, reticleShadowPen);
            DrawReticle(g, reticleX, reticleY, sx, sy, reticlePen);
        }

        // Converter GDI+ de volta para buffer BGRA
        var rendered = BgraFromBitmap(bitmap);
        Buffer.BlockCopy(rendered, 0, pixels, 0, Math.Min(pixels.Length, rendered.Length));

        // ── Fase 2: Fonte bitmap FLIR — texto pixel-perfect sobre o buffer BGRA ──
        int bitmapScale = Math.Max(1, (int)Math.Round(s));
        var (tr, tg, tb) = FlirBitmapFont.FlirTextColor;

        // Spot temperature (topo-esquerda) — fonte maior
        int spotScale = Math.Max(1, (int)Math.Round(s * 1.4));
        var spotText = FormatTemperatureValue(spotTemperatureC, approximate: spotIsApproximate);
        var unitText = " \u00B0C";
        var fullSpotText = spotText + unitText;
        int spotTextX = (int)(7 * sx);
        int spotTextY = (int)(7 * sy);
        FlirBitmapFont.DrawText(pixels, width, height, fullSpotText, spotTextX, spotTextY, spotScale, tr, tg, tb);

        if (!visibleMode)
        {
            var topScaleValue = scaleMaxC ?? maxTemperatureC;
            var bottomScaleValue = scaleMinC ?? minTemperatureC;

            // Tmax (topo-direita) — fonte menor
            var topText = FormatTemperature(topScaleValue, compact: true);
            int topTextW = FlirBitmapFont.MeasureText(topText, bitmapScale);
            int topTextX = (int)(315 * sx) - topTextW;
            int topTextY = (int)(8 * sy);
            FlirBitmapFont.DrawText(pixels, width, height, topText, topTextX, topTextY, bitmapScale, tr, tg, tb);

            // Tmin (base-direita) — fonte menor
            var bottomText = FormatTemperature(bottomScaleValue, compact: true);
            int bottomTextW = FlirBitmapFont.MeasureText(bottomText, bitmapScale);
            int bottomTextX = (int)(315 * sx) - bottomTextW;
            int bottomTextY = (int)(218 * sy);
            FlirBitmapFont.DrawText(pixels, width, height, bottomText, bottomTextX, bottomTextY, bitmapScale, tr, tg, tb);
        }
    }

    /// <summary>
    /// Desenha apenas a caixa preta com bordas arredondadas (sem texto).
    /// O texto será desenhado pela fonte bitmap na fase 2.
    /// </summary>
    private static void DrawBoxOnly(Graphics g, RectangleF rect, Brush boxBrush, Pen borderPen, float radius)
    {
        if (radius > 0)
        {
            FillRoundedRectangle(g, boxBrush, rect, radius);
            DrawRoundedRectangle(g, borderPen, rect, radius);
        }
        else
        {
            g.FillRectangle(boxBrush, rect);
            g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF bounds, float radius)
    {
        if (radius <= 0)
        {
            g.FillRectangle(brush, bounds);
            return;
        }
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(Graphics g, Pen pen, RectangleF bounds, float radius)
    {
        if (radius <= 0)
        {
            g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            return;
        }
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    private static void DrawTemperatureBox(
        Graphics g,
        RectangleF rect,
        string text,
        Font font,
        Brush boxBrush,
        Pen borderPen,
        Brush textBrush,
        Brush shadowBrush,
        float radius = 0f,
        float textOffsetX = 3f,
        float textOffsetY = 1f,
        bool drawMidlineApproximation = false,
        string? unitText = null)
    {
        if (radius > 0)
        {
            FillRoundedRectangle(g, boxBrush, rect, radius);
            DrawRoundedRectangle(g, borderPen, rect, radius);
        }
        else
        {
            g.FillRectangle(boxBrush, rect);
            g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);
        }
        if (drawMidlineApproximation && text.StartsWith("~", StringComparison.Ordinal))
        {
            DrawSpotTemperatureText(g, text[1..], unitText, font, textBrush, rect.X + textOffsetX, rect.Y + textOffsetY, drawApproximation: true);
        }
        else if (!string.IsNullOrWhiteSpace(unitText))
        {
            DrawSpotTemperatureText(g, text, unitText, font, textBrush, rect.X + textOffsetX, rect.Y + textOffsetY, drawApproximation: false);
        }
        else
        {
            DrawOutlinedText(g, text, font, textBrush, shadowBrush, rect.X + textOffsetX, rect.Y + textOffsetY);
        }
    }

    private static void DrawOutlinedText(Graphics g, string text, Font font, Brush textBrush, Brush shadowBrush, float x, float y)
    {
        using var format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoClip;
        g.DrawString(text, font, textBrush, x, y, format);
    }

    private static void DrawSpotTemperatureText(
        Graphics g,
        string valueText,
        string? unitText,
        Font font,
        Brush textBrush,
        float x,
        float y,
        bool drawApproximation)
    {
        using var format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoClip;

        var cursorX = x;
        if (drawApproximation)
        {
            var tildeFontSize = Math.Max(9f, font.Size * 0.78f);
            using var tildeFont = new Font(font.FontFamily, tildeFontSize, font.Style, GraphicsUnit.Pixel);
            var tildeY = y + (font.Size * 0.20f);
            g.DrawString("~", tildeFont, textBrush, cursorX, tildeY, format);
            var tildeWidth = g.MeasureString("~", tildeFont, PointF.Empty, format).Width;
            cursorX += Math.Max(5f, tildeWidth - 1f);
        }

        g.DrawString(valueText, font, textBrush, cursorX, y, format);
        var valueWidth = g.MeasureString(valueText, font, PointF.Empty, format).Width;

        if (!string.IsNullOrWhiteSpace(unitText))
        {
            var unitFontSize = Math.Max(8f, font.Size * 0.58f);
            using var unitFont = new Font(font.FontFamily, unitFontSize, font.Style, GraphicsUnit.Pixel);
            var unitX = cursorX + valueWidth + Math.Max(3f, font.Size * 0.18f);
            var unitY = y + Math.Max(0f, font.Size * 0.10f);
            g.DrawString(unitText, unitFont, textBrush, unitX, unitY, format);
        }
    }

    private static void DrawReticle(Graphics g, float cx, float cy, float sx, float sy, Pen pen)
    {
        pen.StartCap = System.Drawing.Drawing2D.LineCap.Square;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.Square;

        var radiusX = 7.0f * sx;
        var radiusY = 7.0f * sy;
        var gapX = 9.5f * sx;
        var gapY = 9.5f * sy;
        var armX = 25.0f * sx;
        var armY = 23.0f * sy;

        g.DrawEllipse(pen, cx - radiusX, cy - radiusY, radiusX * 2.0f, radiusY * 2.0f);
        g.DrawLine(pen, cx - armX, cy, cx - gapX, cy);
        g.DrawLine(pen, cx + gapX, cy, cx + armX, cy);
        g.DrawLine(pen, cx, cy - armY, cx, cy - gapY);
        g.DrawLine(pen, cx, cy + gapY, cx, cy + armY);
    }

    private static Font CreateFlirUiFont(float sizePx)
    {
        foreach (var familyName in new[]
        {
            "Arial Narrow",
            "Franklin Gothic Demi Cond",
            "Bahnschrift SemiBold Condensed",
            "Bahnschrift Condensed"
        })
        {
            try
            {
                return new Font(familyName, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                // Try next installed fallback.
            }
        }

        return new Font(FontFamily.GenericSansSerif, sizePx, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static Font CreateFlirSpotFont(float sizePx)
    {
        foreach (var familyName in new[]
        {
            "Arial",
            "Helvetica",
            "Arial Unicode MS",
            "Segoe UI"
        })
        {
            try
            {
                return new Font(familyName, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                // Try next installed fallback.
            }
        }

        return new Font(FontFamily.GenericSansSerif, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static bool TryDetectFlirReticleCenter(byte[]? originalPixels, int width, int height, out double centerX, out double centerY)
    {
        centerX = width / 2.0;
        centerY = height / 2.0;
        if (originalPixels is null || originalPixels.Length != width * height * 4 || width <= 0 || height <= 0)
        {
            return false;
        }

        var sx = width / 320.0;
        var sy = height / 240.0;
        var s = Math.Max(0.75, Math.Min(sx, sy));
        var inner = Math.Max(2, (int)Math.Round(4 * s));
        var outer = Math.Max(inner + 8, (int)Math.Round(20 * s));
        var halfThickness = Math.Max(1, (int)Math.Ceiling(1.5 * s));

        var minX = Math.Clamp((int)Math.Round(20 * sx), 0, width - 1);
        var maxX = Math.Clamp(width - 1 - (int)Math.Round(58 * sx), 0, width - 1);
        var minY = Math.Clamp((int)Math.Round(35 * sy), 0, height - 1);
        var maxY = Math.Clamp(height - 1 - (int)Math.Round(30 * sy), 0, height - 1);
        if (maxX <= minX || maxY <= minY)
        {
            return false;
        }

        var bestScore = 0;
        var bestSamples = 1;
        for (var y = minY + outer; y <= maxY - outer; y++)
        {
            for (var x = minX + outer; x <= maxX - outer; x++)
            {
                var score = 0;
                var samples = 0;

                for (var d = -outer; d <= outer; d++)
                {
                    if (Math.Abs(d) < inner)
                    {
                        continue;
                    }

                    for (var t = -halfThickness; t <= halfThickness; t++)
                    {
                        samples += 2;
                        if (IsReticleOverlayPixel(originalPixels, width, height, x + d, y + t))
                        {
                            score++;
                        }
                        if (IsReticleOverlayPixel(originalPixels, width, height, x + t, y + d))
                        {
                            score++;
                        }
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestSamples = Math.Max(1, samples);
                    centerX = x;
                    centerY = y;
                }
            }
        }

        return bestScore >= Math.Max(18, bestSamples * 0.33);
    }

    private static bool IsReticleOverlayPixel(byte[] pixels, int width, int height, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return false;
        }

        var idx = ((y * width) + x) * 4;
        var b = pixels[idx];
        var g = pixels[idx + 1];
        var r = pixels[idx + 2];
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var brightness = (r + g + b) / 3;
        var saturation = max - min;
        return saturation <= 55 && brightness >= 155;
    }

    private static string FormatTemperatureValue(double? value, bool approximate = false)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return approximate ? "~--.-" : "--.-";
        }

        var prefix = approximate ? "~" : string.Empty;
        var formattedValue = value.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"{prefix}{formattedValue}";
    }

    private static string FormatTemperature(double? value, bool compact = false)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return compact ? "--.-" : "--.- C";
        }

        var formattedValue = value.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return compact ? formattedValue : $"{formattedValue} C";
    }

    private static void OverlayFlirLogoOnly(byte[] result, byte[] originalPixels, int width, int height)
    {
        int stride = width * 4;
        double sx = width / 320.0;
        double sy = height / 240.0;
        int x1 = Math.Clamp((int)(2 * sx), 0, width - 1);
        int y1 = Math.Clamp((int)(216 * sy), 0, height - 1);
        int x2 = Math.Clamp((int)(57 * sx), 0, width - 1);
        int y2 = Math.Clamp((int)(235 * sy), 0, height - 1);

        for (int y = y1; y <= y2; y++)
        {
            for (int x = x1; x <= x2; x++)
            {
                int idx = (y * stride) + (x * 4);
                byte ob = originalPixels[idx];
                byte og = originalPixels[idx + 1];
                byte or_ = originalPixels[idx + 2];
                int brightness = (or_ + og + ob) / 3;
                int maxC = Math.Max(or_, Math.Max(og, ob));
                int minC = Math.Min(or_, Math.Min(og, ob));
                int sat = maxC - minC;

                if (sat <= 35 && brightness > 170)
                {
                    result[idx] = ob;
                    result[idx + 1] = og;
                    result[idx + 2] = or_;
                    result[idx + 3] = originalPixels[idx + 3];
                }
            }
        }
    }

    private static void OverlayOriginalTemperatureTextBoxes(
        byte[] result,
        byte[] originalPixels,
        int width,
        int height,
        ImageViewMode mode)
    {
        double sx = width / 320.0;
        double sy = height / 240.0;

        CopyOriginalUiRectangleMasked(
            result,
            originalPixels,
            width,
            height,
            (int)Math.Round(4 * sx),
            (int)Math.Round(4 * sy),
            (int)Math.Round(92 * sx),
            (int)Math.Round(25 * sy));

        if (mode == ImageViewMode.Visible)
        {
            return;
        }

        CopyOriginalUiRectangleMasked(
            result,
            originalPixels,
            width,
            height,
            (int)Math.Round(277 * sx),
            (int)Math.Round(4 * sy),
            (int)Math.Round(316 * sx),
            (int)Math.Round(25 * sy));

        CopyOriginalUiRectangleMasked(
            result,
            originalPixels,
            width,
            height,
            (int)Math.Round(278 * sx),
            (int)Math.Round(215 * sy),
            (int)Math.Round(316 * sx),
            (int)Math.Round(235 * sy));
    }

    private static void OverlayFlirReticleOnly(
        byte[] result,
        byte[] originalPixels,
        int width,
        int height,
        double centerX,
        double centerY)
    {
        int stride = width * 4;
        double sx = width / 320.0;
        double sy = height / 240.0;
        var x1 = Math.Clamp((int)Math.Round(centerX - (26 * sx)), 0, width - 1);
        var x2 = Math.Clamp((int)Math.Round(centerX + (26 * sx)), 0, width - 1);
        var y1 = Math.Clamp((int)Math.Round(centerY - (22 * sy)), 0, height - 1);
        var y2 = Math.Clamp((int)Math.Round(centerY + (22 * sy)), 0, height - 1);

        for (int y = y1; y <= y2; y++)
        {
            for (int x = x1; x <= x2; x++)
            {
                if (!IsNearReticleShape(x, y, centerX, centerY, sx, sy))
                {
                    continue;
                }

                int idx = (y * stride) + (x * 4);
                byte ob = originalPixels[idx];
                byte og = originalPixels[idx + 1];
                byte or_ = originalPixels[idx + 2];
                int brightness = (or_ + og + ob) / 3;
                int max = Math.Max(or_, Math.Max(og, ob));
                int min = Math.Min(or_, Math.Min(og, ob));
                int sat = max - min;

                if (sat <= 90 && (brightness < 95 || brightness > 135))
                {
                    result[idx] = ob;
                    result[idx + 1] = og;
                    result[idx + 2] = or_;
                    result[idx + 3] = originalPixels[idx + 3];
                }
            }
        }
    }

    private static bool IsNearReticleShape(int x, int y, double cx, double cy, double sx, double sy)
    {
        var horizontal = Math.Abs(y - cy) <= Math.Max(2.0, 2.0 * sy)
            && x >= (int)Math.Round(cx - (24 * sx))
            && x <= (int)Math.Round(cx + (24 * sx))
            && Math.Abs(x - cx) >= Math.Max(2.0, 3.0 * sx);

        var vertical = Math.Abs(x - cx) <= Math.Max(2.0, 2.0 * sx)
            && y >= (int)Math.Round(cy - (20 * sy))
            && y <= (int)Math.Round(cy + (20 * sy))
            && Math.Abs(y - cy) >= Math.Max(2.0, 3.0 * sy);

        var ticks =
            (Math.Abs(x - (cx - (14 * sx))) <= Math.Max(2.0, 2.0 * sx) && Math.Abs(y - cy) <= Math.Max(5.0, 5.0 * sy)) ||
            (Math.Abs(x - (cx + (14 * sx))) <= Math.Max(2.0, 2.0 * sx) && Math.Abs(y - cy) <= Math.Max(5.0, 5.0 * sy)) ||
            (Math.Abs(y - (cy - (14 * sy))) <= Math.Max(2.0, 2.0 * sy) && Math.Abs(x - cx) <= Math.Max(5.0, 5.0 * sx)) ||
            (Math.Abs(y - (cy + (14 * sy))) <= Math.Max(2.0, 2.0 * sy) && Math.Abs(x - cx) <= Math.Max(5.0, 5.0 * sx));

        return horizontal || vertical || ticks;
    }

    private static bool IsInsideScaleBarFill(int x, int y, int width, int height)
    {
        double sx = width / 320.0;
        double sy = height / 240.0;

        return x >= (int)(304 * sx)
            && x <= (int)(313 * sx)
            && y >= (int)(30 * sy)
            && y <= (int)(207 * sy);
    }

    private static void DrawPaletteScaleBar(
        byte[] result,
        int width,
        int height,
        ThermalPaletteLutData lut,
        int x1,
        int y1,
        int x2,
        int y2)
    {
        if (lut.Rgb.Count == 0)
            return;

        int stride = width * 4;
        int startX = Math.Clamp(Math.Min(x1, x2), 0, width - 1);
        int endX = Math.Clamp(Math.Max(x1, x2), 0, width - 1);
        int startY = Math.Clamp(Math.Min(y1, y2), 0, height - 1);
        int endY = Math.Clamp(Math.Max(y1, y2), 0, height - 1);
        double range = Math.Max(1, endY - startY);

        for (int y = startY; y <= endY; y++)
        {
            double normalized = 1.0 - ((y - startY) / range);
            var (r, g, b) = InterpolateLut(lut, normalized);

            for (int x = startX; x <= endX; x++)
            {
                int idx = (y * stride) + (x * 4);
                result[idx] = b;
                result[idx + 1] = g;
                result[idx + 2] = r;
                result[idx + 3] = 255;
            }
        }
    }

    private static (byte r, byte g, byte b) InterpolateLut(ThermalPaletteLutData lut, double normalized)
    {
        double pos = Math.Clamp(normalized, 0.0, 1.0) * (lut.Rgb.Count - 1);
        int lo = Math.Clamp((int)Math.Floor(pos), 0, lut.Rgb.Count - 1);
        int hi = Math.Clamp(lo + 1, 0, lut.Rgb.Count - 1);
        double t = pos - lo;

        var c0 = lut.Rgb[lo];
        var c1 = lut.Rgb[hi];

        return (
            LerpByte(c0[0], c1[0], t),
            LerpByte(c0[1], c1[1], t),
            LerpByte(c0[2], c1[2], t));
    }

    private static byte LerpByte(int a, int b, double t)
        => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);

    private static void CopyOriginalRectangle(
        byte[] result,
        byte[] originalPixels,
        int width,
        int height,
        int x1,
        int y1,
        int x2,
        int y2)
    {
        int stride = width * 4;
        int startX = Math.Clamp(Math.Min(x1, x2), 0, width - 1);
        int endX = Math.Clamp(Math.Max(x1, x2), 0, width - 1);
        int startY = Math.Clamp(Math.Min(y1, y2), 0, height - 1);
        int endY = Math.Clamp(Math.Max(y1, y2), 0, height - 1);

        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                int idx = (y * stride) + (x * 4);
                result[idx] = originalPixels[idx];
                result[idx + 1] = originalPixels[idx + 1];
                result[idx + 2] = originalPixels[idx + 2];
                result[idx + 3] = originalPixels[idx + 3];
            }
        }
    }

    private static void CopyOriginalUiRectangleMasked(
        byte[] result,
        byte[] originalPixels,
        int width,
        int height,
        int x1,
        int y1,
        int x2,
        int y2)
    {
        int stride = width * 4;
        int startX = Math.Clamp(Math.Min(x1, x2), 0, width - 1);
        int endX = Math.Clamp(Math.Max(x1, x2), 0, width - 1);
        int startY = Math.Clamp(Math.Min(y1, y2), 0, height - 1);
        int endY = Math.Clamp(Math.Max(y1, y2), 0, height - 1);

        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                int idx = (y * stride) + (x * 4);
                byte ob = originalPixels[idx];
                byte og = originalPixels[idx + 1];
                byte or_ = originalPixels[idx + 2];

                int brightness = (or_ + og + ob) / 3;
                int max = Math.Max(or_, Math.Max(og, ob));
                int min = Math.Min(or_, Math.Min(og, ob));
                int saturation = max - min;

                if (saturation <= 45 && (brightness <= 72 || brightness >= 165))
                {
                    result[idx] = ob;
                    result[idx + 1] = og;
                    result[idx + 2] = or_;
                    result[idx + 3] = originalPixels[idx + 3];
                }
            }
        }
    }

    #endregion
}
