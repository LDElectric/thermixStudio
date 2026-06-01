using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.Services;

/// <summary>
/// Sobreposicao de UI FLIR (escala, reticula, caixas de temperatura, logo).
/// Extraido de ThermalModeEngine.
/// </summary>
public sealed class FlirCameraUiOverlay : IFlirCameraUiOverlay
{
    public byte[] Overlay(
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
        bool preferOriginalTemperatureText = false,
        string? spotLabel = null,
        double? spotNormX = null,
        double? spotNormY = null)
    {
        if (finalPixels is null || finalPixels.Length != width * height * 4)
            return Array.Empty<byte>();

        var result = (byte[])finalPixels.Clone();
        bool useProgrammaticOverlay = true;
        if (useProgrammaticOverlay)
        {
            if (mode != ImageViewMode.Visible && scaleLut is not null && scaleLut.Rgb.Count > 0)
            {
                double sx0 = width / 320.0;
                double sy0 = height / 240.0;

                // Estrutura exata da barra FLIR E8xt (medida do original 2.jpg):
                // x:305-312 = 8px de cores | x:304,313 = borda preta 1px | x:303,314 = borda escura 1px
                int fillX1 = (int)(305 * sx0);
                int fillX2 = (int)(312 * sx0);
                int innerX1 = fillX1 - 1; // 304
                int innerX2 = fillX2 + 1; // 313
                int outerX1 = innerX1 - 1; // 303
                int outerX2 = innerX2 + 1; // 314
                int barY1 = (int)(30 * sy0);
                int barY2 = (int)(207 * sy0);

                // 1) Bordas verticais (2px cada lado) — só as bordas, sem preencher fundo
                Color innerBorder = Color.Black;
                Color outerBorder = Color.FromArgb(18, 18, 20);
                DrawVertLine(result, width, height, innerX1, barY1, barY2, innerBorder);
                DrawVertLine(result, width, height, innerX2, barY1, barY2, innerBorder);
                DrawVertLine(result, width, height, outerX1, barY1, barY2, outerBorder);
                DrawVertLine(result, width, height, outerX2, barY1, barY2, outerBorder);
                // Bordas horizontais (topo e base)
                DrawHorizLine(result, width, height, outerX1, outerX2, barY1 - 1, innerBorder);
                DrawHorizLine(result, width, height, outerX1, outerX2, barY2 + 1, innerBorder);

                // 2) Preenchimento de cores vivas (8px largura)
                DrawPaletteScaleBar(result, width, height, scaleLut, fillX1, barY1, fillX2, barY2);
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

            // Centro do retículo: prioridade à posição do spot da câmera (EXIF)
            // A mira da câmera = posição do Tspot, NÃO o centro geométrico
            var reticleX = width / 2.0;
            var reticleY = height / 2.0;
            bool reticleFromSpot = false;

            if (spotNormX.HasValue && spotNormY.HasValue &&
                spotNormX.Value >= 0 && spotNormX.Value <= 1 &&
                spotNormY.Value >= 0 && spotNormY.Value <= 1)
            {
                reticleX = spotNormX.Value * (width - 1);
                reticleY = spotNormY.Value * (height - 1);
                reticleFromSpot = true;
            }
            else if (originalPixels is not null && originalPixels.Length == width * height * 4)
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
                drawReticle: true,
                spotLabel: spotLabel);

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

        // Fallback para o mÃ©todo antigo (caso useProgrammaticOverlay seja false)
        int stride = width * 4;
        const int darkThreshold   = 60;
        const int brightThreshold = 170;
        const int maxSaturation   = 40;

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
                DrawRectBorder(result, width, height,
                    (int)(304 * sx) - 1, (int)(30 * sy) - 1,
                    (int)(313 * sx) + 1, (int)(207 * sy) + 1,
                    1, Color.Black);
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
            ((int)(  2*sx), (int)(  2*sy), (int)( 96*sx), (int)( 28*sy)),
            ((int)(275*sx), (int)(  2*sy), (int)(318*sx), (int)( 28*sy)),
            ((int)(275*sx), (int)(210*sy), (int)(318*sx), (int)(238*sy)),
            ((int)(  2*sx), (int)(210*sy), (int)(100*sx), (int)(238*sy)),
            ((int)(290*sx), (int)( 25*sy), (int)(318*sx), (int)(215*sy)),
            ((int)(130*sx), (int)(95*sy), (int)(190*sx), (int)(145*sy)),
        };

        for (int i = 0; i < uiBoxes.Length; i++)
        {
            if (visibleMode && (i == 1 || i == 2 || i == 4))
                continue;

            var (bx1, by1, bx2, by2) = uiBoxes[i];
            int clampX2 = Math.Min(bx2, width  - 1);
            int clampY2 = Math.Min(by2, height - 1);
            bool isLogoArea = (i == 3);
            bool isScaleArea = (i == 4);
            bool isCrosshairArea = (i == 5);

            for (int y = Math.Max(by1, 0); y <= clampY2; y++)
            {
                for (int x = Math.Max(bx1, 0); x <= clampX2; x++)
                {
                    if (isScaleArea && scaleLut is not null && IsInsideScaleBarFill(x, y, width, height))
                        continue;

                    int idx = (y * stride) + (x * 4);
                    byte ob  = originalPixels[idx];
                    byte og  = originalPixels[idx + 1];
                    byte or_ = originalPixels[idx + 2];

                    int brightness = (or_ + og + ob) / 3;
                    int maxC = Math.Max(or_, Math.Max(og, ob));
                    int minC = Math.Min(or_, Math.Min(og, ob));
                    int sat  = maxC - minC;

                    bool match;
                    if (isLogoArea)
                    {
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

    #region MÃ©todos auxiliares para UI programÃ¡tica

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
        bool drawReticle = true,
        string? spotLabel = null)
    {
        float sx = width / 320f;
        float sy = height / 240f;
        float scl = Math.Max(sx, sy);
        bool visibleMode = mode == ImageViewMode.Visible;

        // ---- Toda a UI em um único passo GDI+ (retícula + caixas + texto) ----
        const string FontFamily = "Segoe UI";

        string spotText = FormatTemperatureValue(spotTemperatureC, approximate: spotIsApproximate);
        const string spotUnit = "°C";

        // Métricas do Segoe UI Bold (constantes tipográficas):
        //   capRatio  = capHeight / emSize    = 1456/2048 ≈ 0.711
        //   capGapRat = (ascender-capHeight)/emSize = 398/2048 ≈ 0.194
        //   → DrawString Y coloca o topo da célula; as caps começam Y + emSize*capGapRat abaixo.
        //
        // Cap heights medidas no FLIR0060.jpg (320×240):
        //   dígitos "105"  → cap 15px → em = 15/0.711 ≈ 21px
        //   sufixo/prefixo → cap 10px → em = 10/0.711 ≈ 14px
        //   barra de escala→ cap 12px → em = 12/0.711 ≈ 17px
        const float CapRatio  = 0.711f;    // Segoe UI: capHeight/emSize
        const float CapGapRat = 0.1943f;   // Segoe UI: espaço acima das caps dentro da célula

        float capH_main = 15f * scl;       // cap height alvo para dígitos principais
        float capH_sub  = 10f * scl;       // cap height alvo para sufixo/prefixo

        float mainPx = Math.Max(8f,  (float)Math.Round(capH_main / CapRatio));   // ~21px
        float subPx  = Math.Max(5f,  (float)Math.Round(capH_sub  / CapRatio));   // ~14px

        float capGap_main = mainPx * CapGapRat;   // ~4px: offset cell→cap para fonte principal
        float capGap_sub  = subPx  * CapGapRat;   // ~3px: offset cell→cap para subfonte

        int   marginX = (int)Math.Max(3f, Math.Round(4f * sx));
        int   marginY = (int)Math.Max(2f, Math.Round(4f * sy));

        using var bitmap = BitmapFromBgra(width, height, pixels);
        using var g      = Graphics.FromImage(bitmap);
        g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // ---- Retícula ----
        if (drawReticle)
        {
            using var shadowPen = new Pen(Color.Black, Math.Max(2.4f, 2.8f * Math.Max(sx, sy)));
            using var whitePen  = new Pen(Color.White,  Math.Max(1.4f, 1.65f * Math.Max(sx, sy)));
            var reticleX = (float)(reticleCenterX ?? (160 * sx));
            var reticleY = (float)(reticleCenterY ?? (120 * sy));
            DrawReticle(g, reticleX, reticleY, sx, sy, shadowPen);
            DrawReticle(g, reticleX, reticleY, sx, sy, whitePen);
        }

        using var mainFont  = new Font(FontFamily, mainPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont   = new Font(FontFamily, subPx,  FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(245, 247, 243));
        using var strFmt    = StringFormat.GenericTypographic;

        // ---- Caixa do Spot (topo-esquerda): [prefix(sub,baseline)] [digits(main)] [°C(sub,top)] ----
        var spotDigitsSz = g.MeasureString(spotText, mainFont, PointF.Empty, strFmt);
        var spotUnitSz   = g.MeasureString(spotUnit,  subFont,  PointF.Empty, strFmt);

        int prefixPixW = 0, prefixGap = 0;
        SizeF prefixSz = SizeF.Empty;
        if (!string.IsNullOrWhiteSpace(spotLabel))
        {
            prefixSz  = g.MeasureString(spotLabel, subFont, PointF.Empty, strFmt);
            prefixPixW = (int)Math.Ceiling(prefixSz.Width);
            prefixGap  = (int)Math.Max(1f, Math.Round(3f * sx));
        }

        int unitGap = (int)Math.Max(1f, Math.Round(4f * sx));
        // Largura: soma das partes + gaps + margens
        int spotBoxW = marginX + prefixPixW + prefixGap
                       + (int)Math.Ceiling(spotDigitsSz.Width)
                       + unitGap + (int)Math.Ceiling(spotUnitSz.Width) + marginX;
        // Altura: marginY + cap height alvo + marginY (não usa GetHeight que inclui leading excessivo)
        int spotBoxH = (int)Math.Round(capH_main) + marginY * 2;
        int boxX     = (int)(4 * sx);
        int boxY     = (int)(4 * sy);

        int cornerR = (int)Math.Max(2f, Math.Round(3f * scl));
        FillRoundedRectGdi(g, boxX, boxY, spotBoxW, spotBoxH, cornerR);

        float curX = boxX + marginX;
        // mainY: DrawString Y tal que as caps fiquem em boxY+marginY
        // (DrawString coloca célula em Y; caps começam Y+capGap_main abaixo da célula)
        float mainY = boxY + marginY - capGap_main;

        if (!string.IsNullOrWhiteSpace(spotLabel))
        {
            // Baseline-aligned: cap bottom do prefixo = cap bottom dos dígitos
            // capBottom_main = mainY + capGap_main + capH_main
            // prefixDrawY = capBottom_main - capGap_sub - capH_sub
            float prefixY = mainY + capGap_main + capH_main - capGap_sub - capH_sub;
            g.DrawString(spotLabel, subFont, textBrush, curX, prefixY, strFmt);
            curX += prefixPixW + prefixGap;
        }

        g.DrawString(spotText, mainFont, textBrush, curX, mainY, strFmt);
        curX += (int)Math.Ceiling(spotDigitsSz.Width) + unitGap;

        // °C: top-aligned (superscript) — cap top do sufixo = cap top dos dígitos
        // capTop_main = mainY + capGap_main
        // suffixDrawY = capTop_main - capGap_sub
        float suffixY = mainY + (capGap_main - capGap_sub);
        g.DrawString(spotUnit, subFont, textBrush, curX, suffixY, strFmt);

        // ---- Tmax / Tmin (modo térmico) ----
        if (!visibleMode)
        {
            // Barra de escala: cap height alvo 12px → em = 12/0.711 ≈ 17px
            float capH_top = 12f * scl;
            float topPx    = Math.Max(5f, (float)Math.Round(capH_top / CapRatio));  // ~17px
            float capGap_top = topPx * CapGapRat;
            int topMX   = (int)Math.Max(2f, Math.Round(4f * sx));
            int topMY   = (int)Math.Max(2f, Math.Round(3f * sy));
            int topCornerR = (int)Math.Max(2f, Math.Round(3f * scl));

            // Tmax (topo-direita)
            string topText  = FormatTemperature(scaleMaxC ?? maxTemperatureC, compact: true);
            using var topFont = new Font(FontFamily, topPx, FontStyle.Bold, GraphicsUnit.Pixel);
            var topSz       = g.MeasureString(topText, topFont, PointF.Empty, strFmt);
            int topBoxW     = topMX + (int)Math.Ceiling(topSz.Width) + topMX;
            int topBoxH     = (int)Math.Round(capH_top) + topMY * 2;
            int topBoxX     = width - topBoxW - (int)Math.Round(4f * sx);
            int topBoxY     = (int)Math.Round(4f * sy);
            FillRoundedRectGdi(g, topBoxX, topBoxY, topBoxW, topBoxH, topCornerR);
            g.DrawString(topText, topFont, textBrush, topBoxX + topMX, topBoxY + topMY - capGap_top, strFmt);

            // Tmin (base-direita)
            string bottomText  = FormatTemperature(scaleMinC ?? minTemperatureC, compact: true);
            using var bottomFont = new Font(FontFamily, topPx, FontStyle.Bold, GraphicsUnit.Pixel);
            var bottomSz       = g.MeasureString(bottomText, bottomFont, PointF.Empty, strFmt);
            int bottomBoxW     = topMX + (int)Math.Ceiling(bottomSz.Width) + topMX;
            int bottomBoxH     = (int)Math.Round(capH_top) + topMY * 2;
            int bottomBoxX     = width - bottomBoxW - (int)Math.Round(4f * sx);
            int bottomBoxY     = height - bottomBoxH - (int)Math.Round(4f * sy);
            FillRoundedRectGdi(g, bottomBoxX, bottomBoxY, bottomBoxW, bottomBoxH, topCornerR);
            g.DrawString(bottomText, bottomFont, textBrush, bottomBoxX + topMX, bottomBoxY + topMY - capGap_top, strFmt);
        }

        // Conversão única para o buffer de pixels
        var rendered = BgraFromBitmap(bitmap);
        Buffer.BlockCopy(rendered, 0, pixels, 0, Math.Min(pixels.Length, rendered.Length));
    }

    /// <summary>
    /// Calcula a maior escala (mÃºltiplo inteiro) para que o texto caiba dentro de uma Ã¡rea mÃ¡xima.
    /// </summary>
    private static int CalculateOptimalScaleForArea(string text, int maxWidth, int maxHeight, int baseGlyphHeight = 10, int minScale = 1, int maxScale = 10)
    {
        int bestScale = minScale;
        for (int scale = maxScale; scale >= minScale; scale--)
        {
            int textWidth = FlirBitmapFont.MeasureText(text, scale);
            int textHeight = baseGlyphHeight * scale;
            if (textWidth <= maxWidth && textHeight <= maxHeight)
            {
                bestScale = scale;
                break;
            }
        }
        return bestScale;
    }

    /// <summary>
    /// Desenha um retângulo preenchido simples diretamente no buffer BGRA.
    /// </summary>
    private static void DrawFilledRect(byte[] pixels, int width, int height, int x, int y, int w, int h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        int stride = width * 4;
        byte r = color.R, g = color.G, b = color.B;
        int x2 = Math.Min(x + w, width);
        int y2 = Math.Min(y + h, height);

        for (int py = Math.Max(0, y); py < y2; py++)
        {
            int rowStart = (py * stride) + (Math.Max(0, x) * 4);
            for (int px = Math.Max(0, x); px < x2; px++)
            {
                int idx = rowStart + ((px - Math.Max(0, x)) * 4);
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        }
    }

    private static void DrawVertLine(byte[] pixels, int width, int height, int x, int y1, int y2, Color color)
    {
        if (x < 0 || x >= width) return;
        int stride = width * 4;
        byte r = color.R, g = color.G, b = color.B;
        int startY = Math.Max(0, y1);
        int endY = Math.Min(height - 1, y2);
        for (int y = startY; y <= endY; y++)
        {
            int idx = (y * stride) + (x * 4);
            pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
        }
    }

    private static void DrawHorizLine(byte[] pixels, int width, int height, int x1, int x2, int y, Color color)
    {
        if (y < 0 || y >= height) return;
        int stride = width * 4;
        byte r = color.R, g = color.G, b = color.B;
        int startX = Math.Max(0, x1);
        int endX = Math.Min(width - 1, x2);
        int rowBase = y * stride;
        for (int x = startX; x <= endX; x++)
        {
            int idx = rowBase + (x * 4);
            pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
        }
    }

    /// <summary>
    /// Desenha um retÃ¢ngulo arredondado preenchido (sem borda) diretamente no buffer BGRA.
    /// Usa cor sÃ³lida (preto) e bordas arredondadas simples.
    /// </summary>
    /// <summary>
    /// Desenha retângulo preenchido preto com cantos arredondados via GDI+.
    /// </summary>
    private static void FillRoundedRectGdi(Graphics g, int x, int y, int w, int h, int radius)
    {
        if (w <= 0 || h <= 0) return;
        using var brush = new SolidBrush(Color.Black);
        if (radius <= 0 || w < 2 * radius || h < 2 * radius)
        {
            g.FillRectangle(brush, x, y, w, h);
            return;
        }
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x,             y,             2 * radius, 2 * radius, 180, 90);
        path.AddArc(x + w - 2 * radius, y,             2 * radius, 2 * radius, 270, 90);
        path.AddArc(x + w - 2 * radius, y + h - 2 * radius, 2 * radius, 2 * radius,   0, 90);
        path.AddArc(x,             y + h - 2 * radius, 2 * radius, 2 * radius,  90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawFilledRoundedRect(byte[] pixels, int width, int height, int x, int y, int w, int h, int radius, Color color)
    {
        if (w <= 0 || h <= 0) return;
        int stride = width * 4;
        byte r = color.R, g = color.G, b = color.B;

        for (int dy = 0; dy < h; dy++)
        {
            int py = y + dy;
            if (py < 0 || py >= height) continue;

            for (int dx = 0; dx < w; dx++)
            {
                int px = x + dx;
                if (px < 0 || px >= width) continue;

                // Verificar se estÃ¡ dentro dos cantos arredondados
                bool inside = true;
                if (radius > 0)
                {
                    // Canto superior esquerdo
                    if (dx < radius && dy < radius)
                    {
                        int dist = (radius - dx) * (radius - dx) + (radius - dy) * (radius - dy);
                        if (dist > radius * radius) inside = false;
                    }
                    // Canto superior direito
                    else if (dx >= w - radius && dy < radius)
                    {
                        int dxr = dx - (w - radius);
                        int dist = (radius - dxr) * (radius - dxr) + (radius - dy) * (radius - dy);
                        if (dist > radius * radius) inside = false;
                    }
                    // Canto inferior esquerdo
                    else if (dx < radius && dy >= h - radius)
                    {
                        int dyr = dy - (h - radius);
                        int dist = (radius - dx) * (radius - dx) + (radius - dyr) * (radius - dyr);
                        if (dist > radius * radius) inside = false;
                    }
                    // Canto inferior direito
                    else if (dx >= w - radius && dy >= h - radius)
                    {
                        int dxr = dx - (w - radius);
                        int dyr = dy - (h - radius);
                        int dist = (radius - dxr) * (radius - dxr) + (radius - dyr) * (radius - dyr);
                        if (dist > radius * radius) inside = false;
                    }
                }

                if (inside)
                {
                    int idx = (py * stride) + (px * 4);
                    pixels[idx] = b;     // B
                    pixels[idx + 1] = g; // G
                    pixels[idx + 2] = r; // R
                    pixels[idx + 3] = 255;
                }
            }
        }
    }

    private static void DrawRectBorder(byte[] pixels, int width, int height, int x, int y, int w, int h, int thickness, Color color)
    {
        if (w <= 0 || h <= 0) return;
        int stride = width * 4;
        byte r = color.R, g = color.G, b = color.B;
        int x2 = x + w;
        int y2 = y + h;

        for (int t = 0; t < thickness; t++)
        {
            int bx = x + t;
            int by = y + t;
            int bx2 = x2 - t;
            int by2 = y2 - t;

            // Top edge
            for (int px = Math.Max(0, bx); px <= Math.Min(width - 1, bx2); px++)
            {
                int py = Math.Clamp(by, 0, height - 1);
                int idx = (py * stride) + (px * 4);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
            // Bottom edge
            for (int px = Math.Max(0, bx); px <= Math.Min(width - 1, bx2); px++)
            {
                int py = Math.Clamp(by2, 0, height - 1);
                int idx = (py * stride) + (px * 4);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
            // Left edge
            for (int py = Math.Max(0, by + 1); py <= Math.Min(height - 1, by2 - 1); py++)
            {
                int px = Math.Clamp(bx, 0, width - 1);
                int idx = (py * stride) + (px * 4);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
            // Right edge
            for (int py = Math.Max(0, by + 1); py <= Math.Min(height - 1, by2 - 1); py++)
            {
                int px = Math.Clamp(bx2, 0, width - 1);
                int idx = (py * stride) + (px * 4);
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
            }
        }
    }

    private static void DrawReticle(Graphics g, float cx, float cy, float sx, float sy, Pen pen)
    {
        pen.StartCap = System.Drawing.Drawing2D.LineCap.Square;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.Square;

        // Dimensões calibradas contra retículo original FLIR E8xt (320×240):
        // braços 10-11px, gap ~7px, círculo central ~3.5px raio
        var radiusX = 3.5f * sx;
        var radiusY = 3.5f * sy;
        var gapX = 6.5f * sx;
        var gapY = 6.5f * sy;
        var armX = 11.0f * sx;
        var armY = 11.0f * sy;

        g.DrawEllipse(pen, cx - radiusX, cy - radiusY, radiusX * 2.0f, radiusY * 2.0f);
        g.DrawLine(pen, cx - armX, cy, cx - gapX, cy);
        g.DrawLine(pen, cx + gapX, cy, cx + armX, cy);
        g.DrawLine(pen, cx, cy - armY, cx, cy - gapY);
        g.DrawLine(pen, cx, cy + gapY, cx, cy + armY);
    }

    #endregion

    #region MÃ©todos auxiliares existentes (nÃ£o modificados)

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

    public static bool TryDetectReticleCenter(byte[]? originalPixels, int width, int height, out double centerX, out double centerY)
    {
        return TryDetectFlirReticleCenter(originalPixels, width, height, out centerX, out centerY);
    }

    private static bool TryDetectFlirReticleCenter(byte[]? originalPixels, int width, int height, out double centerX, out double centerY)
    {
        centerX = width / 2.0;
        centerY = height / 2.0;
        if (originalPixels is null || originalPixels.Length != width * height * 4 || width <= 0 || height <= 0)
            return false;

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
        if (maxX <= minX || maxY <= minY) return false;

        var bestScore = 0;
        var bestSamples = 1;

        // Calcular densidade de pixels brancos na área central para detectar falsos positivos
        int centerWhite = 0, centerTotal = 0;
        int cx0 = width / 2, cy0 = height / 2;
        for (int cy = Math.Max(0, cy0 - 15); cy <= Math.Min(height - 1, cy0 + 15); cy++)
            for (int cx = Math.Max(0, cx0 - 15); cx <= Math.Min(width - 1, cx0 + 15); cx++)
            {
                centerTotal++;
                if (IsReticleOverlayPixel(originalPixels, width, height, cx, cy)) centerWhite++;
            }
        double centerWhiteRatio = centerTotal > 0 ? (double)centerWhite / centerTotal : 0;

        // Se > 50% dos pixels centrais são "brancos", provável conteúdo térmico, não retículo
        if (centerWhiteRatio > 0.50)
            return false;

        // Restringir busca a 15px do centro (reticle E8xt sempre próximo)
        int searchR = (int)(15.0 * Math.Max(sx, sy));
        int searchMinX = Math.Max(minX + outer, (width / 2) - searchR);
        int searchMaxX = Math.Min(maxX - outer, (width / 2) + searchR);
        int searchMinY = Math.Max(minY + outer, (height / 2) - searchR);
        int searchMaxY = Math.Min(maxY - outer, (height / 2) + searchR);

        for (var y = searchMinY; y <= searchMaxY; y++)
        {
            for (var x = searchMinX; x <= searchMaxX; x++)
            {
                var score = 0;
                var samples = 0;

                for (var d = -outer; d <= outer; d++)
                {
                    if (Math.Abs(d) < inner) continue;

                    for (var t = -halfThickness; t <= halfThickness; t++)
                    {
                        samples += 2;
                        if (IsReticleOverlayPixel(originalPixels, width, height, x + d, y + t)) score++;
                        if (IsReticleOverlayPixel(originalPixels, width, height, x + t, y + d)) score++;
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

        // Só aceita se encontrou no mínimo 12 pixels de retículo
        if (bestScore < 12) return false;

        return true;
    }

    private static bool IsReticleOverlayPixel(byte[] pixels, int width, int height, int x, int y)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
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
            return approximate ? "~--" : "--";
        var prefix = approximate ? "~" : string.Empty;
        // FLIR exibe sem decimal quando o valor é inteiro (ex.: 105, não 105.0)
        double v = value.Value;
        var formattedValue = Math.Abs(v - Math.Round(v)) < 0.05
            ? ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"{prefix}{formattedValue}";
    }

    private static string FormatTemperature(double? value, bool compact = false)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return compact ? "--" : "-- °C";
        // FLIR exibe sem decimal quando o valor é inteiro (ex.: 106, não 106.0)
        double v = value.Value;
        var formattedValue = Math.Abs(v - Math.Round(v)) < 0.05
            ? ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return compact ? formattedValue : $"{formattedValue} °C";
    }

    /// <summary>
    /// Sobrepõe o logo FLIR (branco, como na câmera real) a partir do PNG embarcado.
    /// O PNG original tem contornos escuros com detalhes (furinhos do diamante e R)
    /// sobre fundo transparente, com padding grande. Este método recorta ao conteúdo
    /// real, escala para o tamanho correto e inverte: escuro → branco.
    /// </summary>
    private static void OverlayFlirLogoOnly(byte[] result, byte[] originalPixels, int width, int height)
    {
        try
        {
            var logo = LoadFlirLogoPng();
            if (logo is null) return;

            // Bounds do conteúdo real dentro do PNG (medidos via análise):
            // PNG = 1536×1024, conteúdo em (223,313)→(1214,648) = 992×336
            var contentBounds = GetLogoCachedContentBounds(logo);

            double sx = width / 320.0;
            double sy = height / 240.0;

            // Posição e dimensões do logo no termograma (base 320×240):
            // Medido dos originais FLIR E8xt (FLIR0060.jpg, FLIR0192.jpg).
            int destX = (int)(4 * sx);
            int destY = (int)(220 * sy);
            int destW = Math.Max(1, (int)(78 * sx));
            int destH = Math.Max(1, (int)(18 * sy));

            // Desenhar APENAS a região do conteúdo do logo, escalada ao destino
            using var scaledBmp = new Bitmap(destW, destH);
            using var g = Graphics.FromImage(scaledBmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            var destRect = new Rectangle(0, 0, destW, destH);
            g.DrawImage(logo, destRect, contentBounds, GraphicsUnit.Pixel);

            // Alpha blending: contornos escuros → branco (como na câmera real)
            int stride = width * 4;
            for (int dy = 0; dy < destH; dy++)
            {
                int destYPos = destY + dy;
                if (destYPos < 0 || destYPos >= height) continue;
                for (int dx = 0; dx < destW; dx++)
                {
                    int destXPos = destX + dx;
                    if (destXPos < 0 || destXPos >= width) continue;
                    var sp = scaledBmp.GetPixel(dx, dy);
                    if (sp.A < 10) continue; // transparente = pular

                    // Inverter: qualquer pixel com alpha → renderizar como branco
                    // com a intensidade proporcional ao alpha original, amplificado para melhor visibilidade
                    int idx = (destYPos * stride) + (destXPos * 4);
                    float a = sp.A / 255f;
                    a = Math.Min(1.0f, a * 1.5f);
                    result[idx]     = (byte)(255 * a + result[idx]     * (1 - a)); // B
                    result[idx + 1] = (byte)(255 * a + result[idx + 1] * (1 - a)); // G
                    result[idx + 2] = (byte)(255 * a + result[idx + 2] * (1 - a)); // R
                    result[idx + 3] = 255;
                }
            }
        }
        catch { /* Logo é opcional */ }
    }

    /// <summary>
    /// Retorna o bounding box do conteúdo real (pixels com alpha alto) dentro do logo PNG,
    /// com cache para evitar recalcular a cada frame.
    /// </summary>
    private static Rectangle? _cachedLogoContentBounds;
    private static Rectangle GetLogoCachedContentBounds(Bitmap logo)
    {
        if (_cachedLogoContentBounds.HasValue) return _cachedLogoContentBounds.Value;

        int w = logo.Width, h = logo.Height;
        int minX = w, maxX = 0, minY = h, maxY = 0;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Usa 200 para ignorar sombras/artefatos semi-transparentes do PNG original
                if (logo.GetPixel(x, y).A > 200)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

        if (maxX <= minX || maxY <= minY)
        {
            _cachedLogoContentBounds = new Rectangle(0, 0, w, h);
        }
        else
        {
            // Adiciona um pequeno padding (2px)
            int pad = 2;
            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            maxX = Math.Min(w - 1, maxX + pad);
            maxY = Math.Min(h - 1, maxY + pad);
            _cachedLogoContentBounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        return _cachedLogoContentBounds.Value;
    }

    private static Bitmap? _cachedFlirLogo;
    private static Bitmap? LoadFlirLogoPng()
    {
        if (_cachedFlirLogo is not null) return _cachedFlirLogo;
        try
        {
            using var stream = typeof(FlirCameraUiOverlay).Assembly
                .GetManifestResourceStream("ThermixStudio.App.flir_logo.png");
            if (stream is not null)
            {
                _cachedFlirLogo = new Bitmap(stream);
                return _cachedFlirLogo;
            }
        }
        catch { }
        return null;
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

        if (mode == ImageViewMode.Visible) return;

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

    private static bool IsInsideScaleBarFill(int x, int y, int width, int height)
    {
        double sx = width / 320.0;
        double sy = height / 240.0;
        return x >= (int)(304 * sx) && x <= (int)(313 * sx) && y >= (int)(30 * sy) && y <= (int)(207 * sy);
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
        if (lut.Rgb.Count == 0) return;

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

        // Thresholds relaxados para JPEG compression
        const int maxSaturation = 55;
        const int darkThreshold = 72;
        const int brightThreshold = 155;

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

                if (saturation <= maxSaturation && (brightness <= darkThreshold || brightness >= brightThreshold))
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
