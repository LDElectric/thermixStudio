using System.IO;
using System.Text.Json;
using SkiaSharp;
using ThermixStudio.Core;

namespace ThermixStudio.App.Services;

/// <summary>
/// Gera imagens IR com marcadores de medição (spots, áreas, círculos) sobrepostos,
/// para uso nos relatórios termográficos.
/// </summary>
internal static class ThermalImageAnnotator
{
    /// <summary>
    /// Carrega a imagem no caminho especificado, desenha os marcadores das medições e/ou
    /// ilustrações e salva em arquivo temporário. Retorna null se a imagem não puder ser
    /// carregada ou se não houver nada para desenhar.
    /// </summary>
    public static string? AnnotateWithSpots(
        string imagePath,
        IReadOnlyList<ThermalMeasurement> measurements,
        IReadOnlyList<ThermalIllustration>? illustrations = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        // Filtrar apenas medições exibíveis sobre a imagem.
        var visibleMeasurements = measurements
            .Where(m => m.Type is MeasurementType.Spot or MeasurementType.Area or MeasurementType.Circle)
            .ToList();

        var visibleIllustrations = illustrations ?? [];

        if (visibleMeasurements.Count == 0 && visibleIllustrations.Count == 0)
        {
            return null;
        }

        try
        {
            using var inputStream = File.OpenRead(imagePath);
            using var codec = SKCodec.Create(inputStream);
            if (codec is null)
            {
                return null;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);

            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                return null;
            }

            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.DrawBitmap(bitmap, 0, 0);

            DrawMeasurements(canvas, info.Width, info.Height, visibleMeasurements);
            DrawIllustrations(canvas, info.Width, info.Height, visibleIllustrations);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            if (data is null)
            {
                return null;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "ThermixStudio", "AnnotatedImages");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, $"annot_{Path.GetFileNameWithoutExtension(imagePath)}_{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(tempFile, data.ToArray());
            return tempFile;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThermalImageAnnotator] {ex.Message}");
            return null;
        }
    }

    public static IReadOnlyList<ThermalIllustration> ExtractIllustrationsFromProcessingJson(string processingJson)
    {
        if (string.IsNullOrWhiteSpace(processingJson))
        {
            return [];
        }

        try
        {
            var state = JsonSerializer.Deserialize<ThermalProcessingState>(processingJson);
            return state?.Illustrations ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void DrawMeasurements(SKCanvas canvas, int imgW, int imgH, List<ThermalMeasurement> measurements)
    {
        using var fillPaint = new SKPaint { Color = new SKColor(220, 40, 40), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, IsStroke = true, StrokeWidth = 1.0f };
        using var rectPaint = new SKPaint { Color = new SKColor(220, 40, 40, 170), IsAntialias = true, IsStroke = true, StrokeWidth = 1.25f };
        using var labelBgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 155), IsAntialias = true };
        var fontSize = Math.Clamp(imgH / 36f, 8f, 14f);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface, fontSize);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        var spotIndex = 0;

        foreach (var m in measurements)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(m.CoordinatesJson) ? "{}" : m.CoordinatesJson);
                var root = doc.RootElement;

                switch (m.Type)
                {
                    case MeasurementType.Spot:
                    {
                        spotIndex++;
                        if (!root.TryGetProperty("x", out var xEl) || !root.TryGetProperty("y", out var yEl)) break;
                        var px = (float)xEl.GetDouble();
                        var py = (float)yEl.GetDouble();
                        var r = Math.Clamp(imgH / 95f, 2.5f, 5f);

                        canvas.DrawCircle(px, py, r, fillPaint);
                        canvas.DrawCircle(px, py, r + 0.9f, borderPaint);

                        var label = $"S{spotIndex}: {m.Tmax:F1}°C";
                        DrawLabel(canvas, label, px + r + 3, py - font.Size / 2f, font, textPaint, labelBgPaint);
                        break;
                    }

                    case MeasurementType.Area:
                    {
                        spotIndex++;
                        if (!root.TryGetProperty("x", out var xEl) || !root.TryGetProperty("y", out var yEl)
                            || !root.TryGetProperty("rw", out var rwEl) || !root.TryGetProperty("rh", out var rhEl)) break;

                        var ax = (float)xEl.GetDouble();
                        var ay = (float)yEl.GetDouble();
                        var aw = (float)rwEl.GetDouble();
                        var ah = (float)rhEl.GetDouble();

                        canvas.DrawRect(ax, ay, aw, ah, rectPaint);

                        var label = $"A{spotIndex}: {m.Tmax:F1}°C";
                        DrawLabel(canvas, label, ax + aw + 3, ay, font, textPaint, labelBgPaint);
                        break;
                    }

                    case MeasurementType.Circle:
                    {
                        spotIndex++;
                        if (!root.TryGetProperty("cx", out var cxEl) || !root.TryGetProperty("cy", out var cyEl)
                            || !root.TryGetProperty("radius", out var radEl)) break;

                        var cx = (float)cxEl.GetDouble();
                        var cy = (float)cyEl.GetDouble();
                        var rad = (float)radEl.GetDouble();

                        canvas.DrawCircle(cx, cy, rad, rectPaint);

                        var label = $"C{spotIndex}: {m.Tmax:F1}°C";
                        DrawLabel(canvas, label, cx + rad + 3, cy - font.Size / 2f, font, textPaint, labelBgPaint);
                        break;
                    }
                }
            }
            catch
            {
                // continua para próxima medição
            }
        }
    }

    private static void DrawIllustrations(SKCanvas canvas, int imgW, int imgH, IReadOnlyList<ThermalIllustration> illustrations)
    {
        if (illustrations.Count == 0)
        {
            return;
        }

        using var strokePaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = Math.Clamp(imgH / 300f, 1.2f, 2.4f)
        };
        // Match on-canvas illustration style (white dashed stroke).
        strokePaint.PathEffect = SKPathEffect.CreateDash(new float[] { 6f, 4f }, 0f);
        using var fillPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 18),
            IsAntialias = true,
            IsStroke = false
        };
        using var textBgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface, Math.Clamp(imgH / 30f, 9f, 18f));

        foreach (var item in illustrations)
        {
            var x1 = (float)(Math.Clamp(item.X1, 0.0, 1.0) * imgW);
            var y1 = (float)(Math.Clamp(item.Y1, 0.0, 1.0) * imgH);
            var x2 = (float)(Math.Clamp(item.X2, 0.0, 1.0) * imgW);
            var y2 = (float)(Math.Clamp(item.Y2, 0.0, 1.0) * imgH);

            switch (item.Type)
            {
                case IllustrationType.Arrow:
                {
                    canvas.DrawLine(x1, y1, x2, y2, strokePaint);
                    DrawArrowHead(canvas, strokePaint, x1, y1, x2, y2);
                    break;
                }
                case IllustrationType.Rectangle:
                {
                    var left = Math.Min(x1, x2);
                    var top = Math.Min(y1, y2);
                    var width = Math.Max(2f, Math.Abs(x2 - x1));
                    var height = Math.Max(2f, Math.Abs(y2 - y1));
                    canvas.DrawRect(left, top, width, height, fillPaint);
                    canvas.DrawRect(left, top, width, height, strokePaint);
                    break;
                }
                case IllustrationType.Ellipse:
                {
                    var cx = (x1 + x2) / 2f;
                    var cy = (y1 + y2) / 2f;
                    var rx = Math.Max(1f, Math.Abs(x2 - x1) / 2f);
                    var ry = Math.Max(1f, Math.Abs(y2 - y1) / 2f);
                    canvas.DrawOval(cx, cy, rx, ry, fillPaint);
                    canvas.DrawOval(cx, cy, rx, ry, strokePaint);
                    break;
                }
                case IllustrationType.Text:
                {
                    var text = string.IsNullOrWhiteSpace(item.Text) ? "Texto" : item.Text.Trim();
                    DrawLabel(canvas, text, x1 + 3f, y1 - font.Size / 2f, font, textPaint, textBgPaint);
                    break;
                }
            }
        }
    }

    private static void DrawArrowHead(SKCanvas canvas, SKPaint strokePaint, float x1, float y1, float x2, float y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1)
        {
            return;
        }

        var ux = (float)(dx / len);
        var uy = (float)(dy / len);
        var head = 12f;

        var lx = x2 - (ux * head) - (uy * head * 0.45f);
        var ly = y2 - (uy * head) + (ux * head * 0.45f);
        var rx = x2 - (ux * head) + (uy * head * 0.45f);
        var ry = y2 - (uy * head) - (ux * head * 0.45f);

        canvas.DrawLine(x2, y2, lx, ly, strokePaint);
        canvas.DrawLine(x2, y2, rx, ry, strokePaint);
    }

    private static void DrawLabel(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint textPaint, SKPaint bgPaint)
    {
        var textWidth = font.MeasureText(text);
        var textHeight = font.Size;
        var padding = 2f;

        var bgRect = new SKRect(x - padding, y - padding, x + textWidth + padding, y + textHeight + padding);
        canvas.DrawRoundRect(bgRect, 2f, 2f, bgPaint);
        canvas.DrawText(text, x, y + textHeight * 0.85f, SKTextAlign.Left, font, textPaint);
    }
}
