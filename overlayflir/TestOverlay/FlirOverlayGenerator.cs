using System;
using System.Globalization;
using SkiaSharp;

namespace FlirStyleOverlay
{
    public class FlirOverlayGenerator
    {
        // Proporcoes calibradas
        // T e o textSize passado ao SkiaSharp (nao a altura do glifo).
        // Para imageH=240, T deve resultar em caixas de ~21px.
        private const float TextSizeRatio = 0.09375f;

        // Tamanho relativo dos elementos (em relacao a T)
        private const float TildeRatio      = 1.0f;
        private const float WordPrefixRatio = 0.33f;
        private const float SuffixRatio     = 0.67f;
        private const float ScaleRatio      = 0.80f;

        // Paddings da caixa principal (fracao de T)
        private const float PadTop    = 0.20f;
        private const float PadBottom = 0.00f;
        private const float PadLeft   = 0.33f;
        private const float PadRight  = 0.13f;

        // Gaps da caixa principal
        private const float GapPrefixToTemp = 0.13f;
        private const float GapTempToSuffix = 0.60f;

        // Paddings da caixa de escala lateral
        private const float ScalePadTop  = 0.28f;
        private const float ScalePadBot  = 0.12f;
        private const float ScalePadH    = 0.15f;

        // Barra de escala: proporções calibradas para imageH=240
        // Geometria base: 12px total, margem direita de 4px
        public static int GetScaleBarWidth(int imageHeight) 
            => (int)Math.Max(1, Math.Round(12f * (imageHeight / 240f)));
        public static int GetScaleBarMarginRight(int imageHeight) 
            => (int)Math.Max(1, Math.Round(4f * (imageHeight / 240f)));
        private readonly SKTypeface _typeface;

        public FlirOverlayGenerator(string fontName = "Roboto Condensed")
        {
            // Alterado de Bold para Normal/Medium para os dígitos ficarem mais "fininhos"
            _typeface = SKTypeface.FromFamilyName(
                            fontName,
                            SKFontStyleWeight.Normal,
                            SKFontStyleWidth.Condensed,
                            SKFontStyleSlant.Upright)
                        ?? SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal,
                            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                        ?? SKTypeface.Default;
        }

        // ════════════════════════════════════════════════════════════════════
        //  API PUBLICA
        // ════════════════════════════════════════════════════════════════════

        public SKBitmap DrawMainTemperatureBox(
            int imageHeight, float temperature,
            string prefix = null, int decimals = 1)
        {
            float T = imageHeight * TextSizeRatio;
            return RenderMainBox(T, temperature, prefix, decimals);
        }

        public SKBitmap DrawScaleTemperatureBox(
            int imageHeight, float temperature, int decimals = 1)
        {
            float T = imageHeight * TextSizeRatio;
            return RenderScaleBox(T, temperature, decimals);
        }

        /// <summary>
        /// Desenha a barra de escala colorida entre as caixas de temperatura.
        /// barHeight: altura em pixels (distancia vertical entre caixa_max e caixa_min).
        /// Largura dimensionada dinamicamente baseada na altura da imagem (base 240p = 12px).
        /// customLut: Array de cores mapeando do frio (índice 0) ao quente (índice Length-1). Se nulo, usa Iron FLIR padrão.
        /// </summary>
        public SKBitmap DrawScaleBar(int barHeight, int imageHeight, SKColor[] customLut = null)
        {
            int barWidth = GetScaleBarWidth(imageHeight);
            return RenderColorScaleBar(barWidth, barHeight, imageHeight, customLut);
        }

        /// <summary>
        /// Retorna a posicao X da barra de escala para uma imagem.
        /// </summary>
        public static int ScaleBarX(int imageWidth, int imageHeight)
            => imageWidth - GetScaleBarWidth(imageHeight) - GetScaleBarMarginRight(imageHeight);

        // ════════════════════════════════════════════════════════════════════
        //  RENDERIZACAO INTERNA
        // ════════════════════════════════════════════════════════════════════

        private SKBitmap RenderMainBox(float T, float temperature, string prefix, int decimals)
        {
            string fmt     = decimals > 0 ? "0." + new string('0', decimals) : "0";
            string tempStr = temperature.ToString(fmt, CultureInfo.InvariantCulture);
            const string suffix = "\u00b0C";

            bool isTilde   = prefix == "~";
            bool isWordPfx = !isTilde && !string.IsNullOrEmpty(prefix);
            bool hasPrefix = isTilde || isWordPfx;

            float sizeMain   = T;
            float sizePrefix = T * (isTilde ? TildeRatio : WordPrefixRatio);
            float sizeSuffix = T * SuffixRatio;

            using var paintMain   = MakePaint(sizeMain,   bold: true);
            using var paintPrefix = hasPrefix ? MakePaint(sizePrefix, bold: true) : null;
            using var paintSuffix = MakePaint(sizeSuffix, bold: false);

            var tempBounds   = MeasureBounds(tempStr, paintMain);
            var suffixBounds = MeasureBounds(suffix,  paintSuffix);
            var prefixBounds = hasPrefix ? MeasureBounds(prefix, paintPrefix) : SKRect.Empty;

            float glyphH  = -tempBounds.Top;

            float padTop   = T * PadTop;
            float padBot   = T * PadBottom;
            float padLeft  = T * PadLeft;
            float padRight = T * PadRight;
            float gapPT    = hasPrefix ? T * GapPrefixToTemp : 0f;
            float gapTS    = T * GapTempToSuffix;

            float prefixW  = hasPrefix ? prefixBounds.Width : 0f;
            float contentW = prefixW + gapPT + tempBounds.Width + gapTS + suffixBounds.Width;
            float boxW = padLeft + contentW + padRight;
            float boxH = padTop  + glyphH   + padBot;

            int bmpW = Math.Max(1, (int)Math.Ceiling(boxW));
            int bmpH = Math.Max(1, (int)Math.Ceiling(boxH));

            var result = new SKBitmap(bmpW, bmpH);
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.Transparent);

            float radius = T * 0.10f;
            using var bgPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(0, 0, bmpW, bmpH), radius, radius, bgPaint);

            float tempBaseline = padTop + glyphH;
            float x = padLeft;

            if (hasPrefix)
            {
                float prefixGlyphH = -prefixBounds.Top;
                float prefixBaseline;
                if (isTilde)
                {
                    // Desenha o prefixo (~) ajustado
                    prefixBaseline = tempBaseline + (T * 0.12f);
                }
                else
                {
                    prefixBaseline = tempBaseline;
                }
                canvas.DrawText(prefix, x, prefixBaseline, paintPrefix);
                x += prefixW + gapPT;
            }

            canvas.DrawText(tempStr, x, tempBaseline, paintMain);
            x += tempBounds.Width + gapTS;

            float suffixGlyphH = -suffixBounds.Top;
            canvas.DrawText(suffix, x, padTop + suffixGlyphH, paintSuffix);

            return result;
        }

        private SKBitmap RenderScaleBox(float T, float temperature, int decimals)
        {
            string fmt     = decimals > 0 ? "0." + new string('0', decimals) : "0";
            string tempStr = temperature.ToString(fmt, CultureInfo.InvariantCulture);

            float sizeScale = T * ScaleRatio;
            using var paint = MakePaint(sizeScale, bold: false);

            var bounds  = MeasureBounds(tempStr, paint);
            float glyphH = -bounds.Top;

            float Ts     = sizeScale;
            float padTop = Ts * ScalePadTop;
            float padBot = Ts * ScalePadBot;
            float padH   = Ts * ScalePadH;

            float boxW = padH + bounds.Width + padH;
            float boxH = padTop + glyphH + padBot;

            int bmpW = Math.Max(1, (int)Math.Ceiling(boxW));
            int bmpH = Math.Max(1, (int)Math.Ceiling(boxH));

            var result = new SKBitmap(bmpW, bmpH);
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.Transparent);

            float radius = T * 0.10f;
            using var bgPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(0, 0, bmpW, bmpH), radius, radius, bgPaint);

            canvas.DrawText(tempStr, padH, padTop + glyphH, paint);

            return result;
        }

        // Barra de escala colorida (Iron LUT)
        // Geometria calibrada do termograma 2.jpg (320x240):
        //   Largura total : 12px  (x=304..315)
        //   Borda horiz   : 2px cada lado  (cols 304-305 esq | 314-315 dir)
        //   Borda vert    : 1px cada lado  (row 29 topo | row 209 base)
        //   Interior LUT  : 8px x (barH-2)  (cols 306..313)
        //   LUT amostrada : coluna x=309, y=30(max/topo)..208(min/base)
        private static SKBitmap RenderColorScaleBar(int barW, int barH, int imageHeight, SKColor[] customLut)
        {
            // LUT Iron FLIR E8-xt (Fallback)
            // norm=0.0 -> frio (quase preto)  |  norm=1.0 -> quente (branco)
            var stops = new (float n, byte r, byte g, byte b)[]
            {
                (0.0000f,   0,   4,   7),   // quase preto (min/frio)
                (0.0393f,  15,   4,  98),   // azul escuro
                (0.0730f,  60,   0, 139),   // azul-roxo
                (0.1067f,  97,   6, 171),   // roxo
                (0.1404f, 134,   5, 170),   // roxo medio
                (0.1742f, 168,  15, 159),   // magenta-roxo
                (0.2079f, 192,  28, 143),   // magenta
                (0.2416f, 216,  50, 116),   // rosa-magenta
                (0.2809f, 230,  70,  72),   // vermelho
                (0.3146f, 238,  89,  49),   // vermelho-laranja
                (0.3483f, 251, 116,  26),   // laranja
                (0.3820f, 255, 137,   9),   // laranja
                (0.4157f, 255, 178,   0),   // laranja-amarelo
                (0.4494f, 254, 207,   3),   // amarelo-laranja
                (0.4831f, 254, 210,  17),   // amarelo
                (0.5225f, 255, 218,  37),   // amarelo
                (0.5562f, 255, 220,  36),   // amarelo
                (0.5899f, 255, 224,  41),   // amarelo
                (0.6236f, 255, 224,  47),   // amarelo
                (0.6573f, 255, 230,  49),   // amarelo claro
                (0.6910f, 255, 230,  61),   // amarelo claro
                (0.7247f, 255, 232,  74),   // amarelo claro
                (0.7640f, 254, 233,  78),   // amarelo muito claro
                (0.7978f, 255, 238,  95),   // amarelo palido
                (0.8315f, 255, 244,  94),   // amarelo palido
                (0.8652f, 251, 243, 110),   // amarelo-branco
                (0.8989f, 250, 246, 136),   // quase branco
                (0.9326f, 248, 252, 158),   // quase branco
                (0.9663f, 248, 255, 199),   // quase branco
                (1.0000f, 255, 255, 255),   // branco (max/quente)
            };

            // Geometria: bordas acompanham a escala (base 240)
            float scale = imageHeight / 240f;
            int borderH = Math.Max(1, (int)Math.Round(2f * scale));
            int borderV = Math.Max(1, (int)Math.Round(1f * scale));
            
            int innerW = barW - borderH * 2;
            int innerH = barH - borderV * 2;
            if (innerW < 1) innerW = 1;
            if (innerH < 1) innerH = 1;

            var bmp = new SKBitmap(barW, barH, SKColorType.Rgb888x, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Black);   // moldura preta

            using var px = new SKPaint { IsAntialias = false };
            for (int y = 0; y < innerH; y++)
            {
                // norm=1.0 no topo (temp max), norm=0.0 na base (temp min)
                float norm = 1.0f - (float)y / Math.Max(1, innerH - 1);

                if (customLut != null && customLut.Length > 0)
                {
                    int idx = (int)Math.Round(norm * (customLut.Length - 1));
                    idx = Math.Clamp(idx, 0, customLut.Length - 1);
                    px.Color = customLut[idx];
                }
                else
                {
                    // Interpolacao linear entre stops vizinhos (Iron FLIR fallback)
                    int lo = 0;
                    for (int s = 0; s < stops.Length - 1; s++)
                        if (stops[s].n <= norm) lo = s;
                    int hi = Math.Min(lo + 1, stops.Length - 1);

                    float t = (hi == lo) ? 0f :
                              (norm - stops[lo].n) / (stops[hi].n - stops[lo].n);
                    t = Math.Clamp(t, 0f, 1f);

                    byte R = (byte)(stops[lo].r + t * (stops[hi].r - stops[lo].r));
                    byte G = (byte)(stops[lo].g + t * (stops[hi].g - stops[lo].g));
                    byte B = (byte)(stops[lo].b + t * (stops[hi].b - stops[lo].b));

                    px.Color = new SKColor(R, G, B);
                }
                
                canvas.DrawRect(borderH, borderV + y, innerW, 1, px);
            }

            return bmp;
        }

        // ════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════════

        private SKPaint MakePaint(float textSize, bool bold)
        {
            var weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            var tf = SKTypeface.FromFamilyName(
                         _typeface.FamilyName, weight,
                         SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
                     ?? _typeface;

            return new SKPaint
            {
                Typeface     = tf,
                TextSize     = textSize,
                Color        = SKColors.White,
                IsAntialias  = true,
                SubpixelText = true,
            };
        }

        private static SKRect MeasureBounds(string text, SKPaint paint)
        {
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            return bounds;
        }
    }
}
