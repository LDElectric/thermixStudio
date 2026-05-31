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
        bool visibleMode = mode == ImageViewMode.Visible;

        // --- Desenhar retÃ­cula (crosshair) com GDI+ ---
        using var bitmap = BitmapFromBgra(width, height, pixels);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        if (drawReticle)
        {
            using var shadowPen = new Pen(Color.Black, Math.Max(2.4f, 2.8f * Math.Max(sx, sy)));
            using var whitePen = new Pen(Color.White, Math.Max(1.4f, 1.65f * Math.Max(sx, sy)));
            var reticleX = (float)(reticleCenterX ?? (160 * sx));
            var reticleY = (float)(reticleCenterY ?? (120 * sy));
            DrawReticle(g, reticleX, reticleY, sx, sy, shadowPen);
            DrawReticle(g, reticleX, reticleY, sx, sy, whitePen);
        }

        // Converter GDI+ de volta para buffer (jÃ¡ tem a retÃ­cula)
        var rendered = BgraFromBitmap(bitmap);
        Buffer.BlockCopy(rendered, 0, pixels, 0, Math.Min(pixels.Length, rendered.Length));
        g.Dispose();
        bitmap.Dispose();

        // --- Agora desenhamos as caixas e textos com fonte bitmap, sem GDI+ para as caixas ---
        // (As caixas serÃ£o desenhadas diretamente no buffer BGRA para total controle)

        // ---- Spot temperature (topo-esquerda) ----
        string spotText = FormatTemperatureValue(spotTemperatureC, approximate: spotIsApproximate);
        string spotUnit = " °C";
        string fullSpotText = spotText + spotUnit;

        // Ãrea mÃ¡xima disponÃ­vel para o spot (evita invadir o centro)
        int maxSpotWidth = (int)((width / 2) - (8 * sx));
        int maxSpotHeight = (int)(height * 0.15f);

        int spotScale = CalculateOptimalScaleForArea(fullSpotText, maxSpotWidth, maxSpotHeight, 10, 1, (int)(Math.Max(sx, sy) * 2.5));
        int spotTextWidth = FlirBitmapFont.MeasureText(fullSpotText, spotScale);
        int spotTextHeight = 10 * spotScale;

        int spotMarginX = (int)(4 * spotScale);
        int spotMarginY = (int)(2 * spotScale);
        int boxWidth = spotTextWidth + (spotMarginX * 2);
        int boxHeight = spotTextHeight + (spotMarginY * 2);

        int boxX = (int)(4 * sx);
        int boxY = (int)(4 * sy);

        int textX = boxX + spotMarginX;
        int textY = boxY + spotMarginY;

        // Prefixo do spot (ex: "Sp1", detectado do EXIF Meas1Label)
        if (!string.IsNullOrWhiteSpace(spotLabel))
        {
            int prefixScale = Math.Max(1, spotScale * 3 / 5);
            int prefixWidth = FlirBitmapFont.MeasureText(spotLabel, prefixScale);
            int prefixY = textY + (spotTextHeight - 10 * prefixScale);

            FlirBitmapFont.DrawText(pixels, width, height, spotLabel,
                textX, prefixY, prefixScale,
                FlirBitmapFont.FlirTextColor.R, FlirBitmapFont.FlirTextColor.G, FlirBitmapFont.FlirTextColor.B);

            textX += prefixWidth + (int)(2 * spotScale);
            boxWidth += prefixWidth + (int)(2 * spotScale);
        }

        DrawFilledRoundedRect(pixels, width, height, boxX, boxY, boxWidth, boxHeight, (int)(spotScale * 1.5f), Color.Black);
        FlirBitmapFont.DrawText(pixels, width, height, fullSpotText,
            textX, textY, spotScale,
            FlirBitmapFont.FlirTextColor.R, FlirBitmapFont.FlirTextColor.G, FlirBitmapFont.FlirTextColor.B);

        if (!visibleMode)
        {
            // ---- Tmax (topo-direita) ----
            string topText = FormatTemperature(scaleMaxC ?? maxTemperatureC, compact: true);
            int maxTopWidth = (int)(width * 0.3f);
            int maxTopHeight = (int)(height * 0.1f);
            int topScale = CalculateOptimalScaleForArea(topText, maxTopWidth, maxTopHeight, 10, 1, (int)(Math.Max(sx, sy) * 1.8));
            int topTextWidth = FlirBitmapFont.MeasureText(topText, topScale);
            int topTextHeight = 10 * topScale;

            int topMarginX = (int)(4 * topScale);
            int topMarginY = (int)(2 * topScale);
            int topBoxWidth = topTextWidth + (topMarginX * 2);
            int topBoxHeight = topTextHeight + (topMarginY * 2);

            int topBoxX = width - topBoxWidth - (int)(4 * sx);
            int topBoxY = (int)(4 * sy);

            DrawFilledRoundedRect(pixels, width, height, topBoxX, topBoxY, topBoxWidth, topBoxHeight, (int)(topScale * 1.5f), Color.Black);
            // Texto alinhado Ã  direita dentro da caixa
            int topTextX = topBoxX + topBoxWidth - topTextWidth - topMarginX;
            int topTextY = topBoxY + topMarginY;
            FlirBitmapFont.DrawText(pixels, width, height, topText,
                topTextX, topTextY, topScale,
                FlirBitmapFont.FlirTextColor.R, FlirBitmapFont.FlirTextColor.G, FlirBitmapFont.FlirTextColor.B);

            // ---- Tmin (base-direita) ----
            string bottomText = FormatTemperature(scaleMinC ?? minTemperatureC, compact: true);
            int bottomScale = CalculateOptimalScaleForArea(bottomText, maxTopWidth, maxTopHeight, 10, 1, (int)(Math.Max(sx, sy) * 1.8));
            int bottomTextWidth = FlirBitmapFont.MeasureText(bottomText, bottomScale);
            int bottomTextHeight = 10 * bottomScale;

            int bottomMarginX = (int)(4 * bottomScale);
            int bottomMarginY = (int)(2 * bottomScale);
            int bottomBoxWidth = bottomTextWidth + (bottomMarginX * 2);
            int bottomBoxHeight = bottomTextHeight + (bottomMarginY * 2);

            int bottomBoxX = width - bottomBoxWidth - (int)(4 * sx);
            int bottomBoxY = height - bottomBoxHeight - (int)(4 * sy);

            DrawFilledRoundedRect(pixels, width, height, bottomBoxX, bottomBoxY, bottomBoxWidth, bottomBoxHeight, (int)(bottomScale * 1.5f), Color.Black);
            int bottomTextX = bottomBoxX + bottomBoxWidth - bottomTextWidth - bottomMarginX;
            int bottomTextY = bottomBoxY + bottomMarginY;
            FlirBitmapFont.DrawText(pixels, width, height, bottomText,
                bottomTextX, bottomTextY, bottomScale,
                FlirBitmapFont.FlirTextColor.R, FlirBitmapFont.FlirTextColor.G, FlirBitmapFont.FlirTextColor.B);
        }

        // Opcional: desenhar a barra de escala (paleta) se necessÃ¡rio (jÃ¡ tratado fora)
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

        for (var y = minY + outer; y <= maxY - outer; y++)
        {
            for (var x = minX + outer; x <= maxX - outer; x++)
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

        // Só aceita se encontrou pixels suficientes (≥33% de cobertura) e o centro não está muito longe do default
        double ratio = (double)bestScore / bestSamples;
        if (ratio < 0.33) return false;

        // Se o centro detectado está muito longe do centro geométrico (>15px), rejeitar
        double distFromDefault = Math.Sqrt((centerX - width / 2.0) * (centerX - width / 2.0) + (centerY - height / 2.0) * (centerY - height / 2.0));
        if (distFromDefault > 15.0 * Math.Max(sx, sy)) return false;

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
            return approximate ? "~--.-" : "--.-";
        var prefix = approximate ? "~" : string.Empty;
        var formattedValue = value.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return $"{prefix}{formattedValue}";
    }

    private static string FormatTemperature(double? value, bool compact = false)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
            return compact ? "--.-" : "--.- C";
        var formattedValue = value.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return compact ? formattedValue : $"{formattedValue} C";
    }

    /// <summary>
    /// Preserva o logo FLIR copiando pixels claros de baixa saturação do original.
    /// Usa thresholds relaxados (JPEG compression + anti-aliasing) e filtro
    /// de componente conectado para eliminar ruído.
    /// </summary>
    private static void OverlayFlirLogoOnly(byte[] result, byte[] originalPixels, int width, int height)
    {
        int stride = width * 4;
        double sx = width / 320.0;
        double sy = height / 240.0;

        // Região expandida para capturar variações de posição do logo
        int x1 = Math.Clamp((int)(1 * sx), 0, width - 1);
        int y1 = Math.Clamp((int)(212 * sy), 0, height - 1);
        int x2 = Math.Clamp((int)(62 * sx), 0, width - 1);
        int y2 = Math.Clamp((int)(237 * sy), 0, height - 1);
        int logoW = x2 - x1 + 1;
        int logoH = y2 - y1 + 1;

        // Thresholds relaxados para JPEG compression e anti-aliasing
        const int maxSaturation = 55;
        const int minBrightness = 140;
        const int minComponentSize = 6;

        // Passo 1: criar máscara binária dos pixels candidatos
        var mask = new bool[logoW, logoH];
        for (int y = y1; y <= y2; y++)
        {
            for (int x = x1; x <= x2; x++)
            {
                int idx = (y * stride) + (x * 4);
                byte ob = originalPixels[idx];
                byte og = originalPixels[idx + 1];
                byte or_ = originalPixels[idx + 2];
                int maxC = Math.Max(or_, Math.Max(og, ob));
                int minC = Math.Min(or_, Math.Min(og, ob));
                int sat = maxC - minC;
                int bright = (or_ + og + ob) / 3;
                mask[x - x1, y - y1] = sat <= maxSaturation && bright >= minBrightness;
            }
        }

        // Passo 2: connected-component filter (elimina pixels isolados)
        var keep = new bool[logoW, logoH];
        var visited = new bool[logoW, logoH];

        for (int my = 0; my < logoH; my++)
        {
            for (int mx = 0; mx < logoW; mx++)
            {
                if (!mask[mx, my] || visited[mx, my]) continue;

                // BFS para medir tamanho do componente
                var q = new Queue<(int, int)>();
                var comp = new List<(int, int)>();
                q.Enqueue((mx, my));
                visited[mx, my] = true;

                while (q.Count > 0)
                {
                    var (cx, cy) = q.Dequeue();
                    comp.Add((cx, cy));

                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx >= 0 && nx < logoW && ny >= 0 && ny < logoH &&
                                mask[nx, ny] && !visited[nx, ny])
                            {
                                visited[nx, ny] = true;
                                q.Enqueue((nx, ny));
                            }
                        }
                }

                // Só preserva componentes com tamanho mínimo
                if (comp.Count >= minComponentSize)
                {
                    foreach (var (cx, cy) in comp)
                        keep[cx, cy] = true;
                }
            }
        }

        // Passo 3: copiar pixels válidos do original
        for (int y = y1; y <= y2; y++)
        {
            for (int x = x1; x <= x2; x++)
            {
                if (!keep[x - x1, y - y1]) continue;
                int idx = (y * stride) + (x * 4);
                result[idx] = originalPixels[idx];
                result[idx + 1] = originalPixels[idx + 1];
                result[idx + 2] = originalPixels[idx + 2];
                result[idx + 3] = originalPixels[idx + 3];
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
