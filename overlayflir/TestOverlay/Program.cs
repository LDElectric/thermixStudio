using System;
using System.IO;
using SkiaSharp;
using FlirStyleOverlay;

class Program
{
    record ImageConfig(
        string RenderPath, string OutputPath,
        float MainTemp, string MainPrefix,
        float ScaleMax, float ScaleMin);

    static void DrawReticle(SKCanvas canvas, float cx, float cy, float scale)
    {
        using var paintWhite = new SKPaint { Color = SKColors.White, IsAntialias = false, StrokeWidth = 1 };
        using var paintBlack = new SKPaint { Color = SKColors.Black, IsAntialias = false, StrokeWidth = 1 };

        void DrawDot(float dx, float dy, SKPaint p) => canvas.DrawRect(cx + dx * scale, cy + dy * scale, scale, scale, p);

        // Draw the 4 main lines
        // Top
        canvas.DrawRect(cx - 1 * scale, cy - 15 * scale, 3 * scale, 9 * scale, paintBlack);
        canvas.DrawRect(cx, cy - 15 * scale, 1 * scale, 9 * scale, paintWhite);
        DrawDot(0, -6, paintBlack); // Inner cap

        // Bottom
        canvas.DrawRect(cx - 1 * scale, cy + 7 * scale, 3 * scale, 9 * scale, paintBlack);
        canvas.DrawRect(cx, cy + 7 * scale, 1 * scale, 9 * scale, paintWhite);
        DrawDot(0, 6, paintBlack); // Inner cap

        // Left
        canvas.DrawRect(cx - 15 * scale, cy - 1 * scale, 9 * scale, 3 * scale, paintBlack);
        canvas.DrawRect(cx - 15 * scale, cy, 9 * scale, 1 * scale, paintWhite);
        DrawDot(-6, 0, paintBlack); // Inner cap

        // Right
        canvas.DrawRect(cx + 7 * scale, cy - 1 * scale, 9 * scale, 3 * scale, paintBlack);
        canvas.DrawRect(cx + 7 * scale, cy, 9 * scale, 1 * scale, paintWhite);
        DrawDot(6, 0, paintBlack); // Inner cap

        // Draw the 4 corner brackets
        float[] signs = { -1, 1 };
        foreach (var sx in signs)
        {
            foreach (var sy in signs)
            {
                DrawDot(5 * sx, 5 * sy, paintWhite); // Corner white pixel
                DrawDot(6 * sx, 5 * sy, paintBlack); // Outer X black border
                DrawDot(5 * sx, 6 * sy, paintBlack); // Outer Y black border
            }
        }
    }

    static void Main()
    {
        var generator = new FlirOverlayGenerator("Roboto Condensed");

        var basePath = @"C:\Users\Leonam Dias\Documents\Projetos C#\overlayflir";
        Console.WriteLine($"Base path: {basePath}");

        var configs = new[]
        {
            new ImageConfig(
                RenderPath:   Path.Combine(basePath, "2_render.jpg"),
                OutputPath:   Path.Combine(basePath, "2_generated.jpg"),
                MainTemp:     43.4f,
                MainPrefix:   "~",
                ScaleMax:     44.7f,
                ScaleMin:     21.4f),

            new ImageConfig(
                RenderPath:   Path.Combine(basePath, "FLIR0192_render.jpg"),
                OutputPath:   Path.Combine(basePath, "FLIR0192_generated.jpg"),
                MainTemp:     43.4f,
                MainPrefix:   "~",
                ScaleMax:     44.7f,
                ScaleMin:     21.4f),
        };

        foreach (var cfg in configs)
        {
            if (!File.Exists(cfg.RenderPath))
            {
                Console.WriteLine($"[SKIP] Render não encontrado: {cfg.RenderPath}");
                continue;
            }

            try
            {
                using var original = SKBitmap.Decode(cfg.RenderPath);
                int W = original.Width;
                int H = original.Height;

                using var finalBmp = new SKBitmap(W, H);
                using var canvas = new SKCanvas(finalBmp);
                canvas.DrawBitmap(original, 0, 0);

                float scale = H / 240f;

                // --- Draw Reticle ---
                DrawReticle(canvas, W / 2f, H / 2f, scale);

                // --- Draw FLIR Logo ---
                FlirLogoRenderer.Draw(canvas, W, H);

                // --- Caixa principal: canto superior esquerdo ---
                using var bmpMain = generator.DrawMainTemperatureBox(H, cfg.MainTemp, cfg.MainPrefix, 1);
                canvas.DrawBitmap(bmpMain, 4, 4);

                // --- Escala máx: canto superior direito ---
                using var bmpMax = generator.DrawScaleTemperatureBox(H, cfg.ScaleMax, 1);
                int scaleX = W - bmpMax.Width - 4;
                int maxY   = 4;
                canvas.DrawBitmap(bmpMax, scaleX, maxY);

                // --- Escala mín: canto inferior direito ---
                using var bmpMin = generator.DrawScaleTemperatureBox(H, cfg.ScaleMin, 1);
                int bottomMargin = (int)Math.Round(H * (6f / 240f));
                int minY = H - bmpMin.Height - bottomMargin;
                canvas.DrawBitmap(bmpMin, scaleX, minY);

                // --- Barra de escala colorida (LUT Iron) ---
                // Adiciona um gap de 4px abaixo da caixa_max e 5px acima da caixa_min (escalado)
                int barY0  = maxY + bmpMax.Height + Math.Max(1, (int)Math.Round(4f * scale));
                int barY1  = minY - Math.Max(1, (int)Math.Round(5f * scale));
                int barH   = barY1 - barY0 + 1;
                if (barH > 1)
                {
                    int barX = FlirOverlayGenerator.ScaleBarX(W, H);
                    using var bmpBar = generator.DrawScaleBar(barH, H);
                    canvas.DrawBitmap(bmpBar, barX, barY0);
                }

                using var image = SKImage.FromBitmap(finalBmp);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
                using var stream = File.OpenWrite(cfg.OutputPath);
                data.SaveTo(stream);

                Console.WriteLine($"[OK] {Path.GetFileName(cfg.OutputPath)} " +
                                  $"({W}x{H}) main={bmpMain.Width}x{bmpMain.Height} " +
                                  $"max={bmpMax.Width}x{bmpMax.Height} " +
                                  $"min={bmpMin.Width}x{bmpMin.Height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] {cfg.RenderPath}: {ex.Message}");
            }
        }
    }
}
