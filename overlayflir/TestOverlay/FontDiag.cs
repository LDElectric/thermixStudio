using System;
using System.Globalization;
using SkiaSharp;

namespace FlirStyleOverlay
{
    static class FontDiag
    {
        public static void Run()
        {
            // Meta: box height = 21px para imagem de 240px
            // box = padTop + glyphH + padBot
            // padTop = 0.20 * T, padBot = 0.00 * T
            // Portanto: 21 = 0.20*T + glyphH
            // glyphH depende de textSize e da fonte
            
            string[] fonts = { "Roboto Condensed", "Arial Narrow", "Arial" };

            foreach (string fontName in fonts)
            {
                var tf = SKTypeface.FromFamilyName(fontName,
                    SKFontStyleWeight.Bold, SKFontStyleWidth.Condensed, SKFontStyleSlant.Upright)
                    ?? SKTypeface.Default;

                Console.WriteLine($"\n=== Fonte: {tf.FamilyName} ===");

                // Testa textSize de 14 a 25 procurando boxH ~= 21
                for (float textSize = 14f; textSize <= 25f; textSize += 0.5f)
                {
                    using var paint = new SKPaint
                    {
                        Typeface = tf,
                        TextSize = textSize,
                        IsAntialias = true,
                    };

                    var bounds = new SKRect();
                    paint.MeasureText("43.4", ref bounds);
                    float glyphH = -bounds.Top;

                    float T = textSize; // T = textSize neste teste
                    float padTop = T * 0.20f;
                    float padBot = T * 0.00f;
                    float boxH = padTop + glyphH + padBot;

                    // Largura da caixa principal "~ 43.4°C"
                    using var pTilde  = new SKPaint { Typeface = tf, TextSize = textSize * 0.33f, IsAntialias = true };
                    using var pSuffix = new SKPaint { Typeface = tf, TextSize = textSize * 0.67f, IsAntialias = true };
                    
                    var bTilde  = new SKRect(); pTilde.MeasureText("~", ref bTilde);
                    var bTemp   = new SKRect(); paint.MeasureText("43.4", ref bTemp);
                    var bSuffix = new SKRect(); pSuffix.MeasureText("°C", ref bSuffix);

                    float gapPT = T * 0.13f;
                    float gapTS = T * 0.60f;
                    float padL  = T * 0.33f;
                    float padR  = T * 0.13f;
                    float boxW = padL + bTilde.Width + gapPT + bTemp.Width + gapTS + bSuffix.Width + padR;

                    // Escala "44.7" com ScaleRatio=0.80
                    float sScale = textSize * 0.80f;
                    using var pScale = new SKPaint { Typeface = tf, TextSize = sScale, IsAntialias = true };
                    var bScale = new SKRect(); pScale.MeasureText("44.7", ref bScale);
                    float glyphHs = -bScale.Top;
                    float Ts = sScale;
                    float sPadTop = Ts * 0.33f;
                    float sPadBot = Ts * 0.17f;
                    float sPadH   = Ts * 0.33f;
                    float scaleBoxW = sPadH + bScale.Width + sPadH;
                    float scaleBoxH = sPadTop + glyphHs + sPadBot;

                    if (Math.Abs(boxH - 21f) < 2f || Math.Abs(scaleBoxH - 21f) < 2f)
                    {
                        Console.WriteLine($"  T={textSize:F1}: mainBox={boxW:F0}x{boxH:F0} " +
                                          $"scaleBox={scaleBoxW:F0}x{scaleBoxH:F0} " +
                                          $"glyphH_main={glyphH:F1} glyphH_scale={glyphHs:F1}");
                    }
                }
            }
        }
    }
}
