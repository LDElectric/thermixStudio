using System.Drawing;
using System.Drawing.Imaging;
using ThermixStudio.App.Services;
using ThermixStudio.Core;

var root = FindRepositoryRoot();
var referencePath = Path.Combine(root, "FLIR0192.jpg");
if (!File.Exists(referencePath))
{
    throw new FileNotFoundException("Arquivo de referencia FLIR0192.jpg nao encontrado.", referencePath);
}

var analysis = new ThermalAnalysisService();
var image = await analysis.LoadImageAsync(referencePath);
Assert(image.Width == 320 && image.Height == 240, "Dimensoes esperadas 320x240.");
Assert(image.Metadata.EmbeddedPaletteBgra is { Length: 1024 }, "Paleta FLIR embarcada deve ser extraida como BGRA 256.");
Assert(image.Metadata.PaletteBelowColorYCrCb is { Length: >= 3 }, "BelowColor deve estar nos metadados.");
Assert(image.Metadata.PaletteAboveColorYCrCb is { Length: >= 3 }, "AboveColor deve estar nos metadados.");
Assert(image.Metadata.PaletteOverflowColorYCrCb is { Length: >= 3 }, "OverflowColor deve estar nos metadados.");
Assert(image.Metadata.PaletteUnderflowColorYCrCb is { Length: >= 3 }, "UnderflowColor deve estar nos metadados.");

var beforeSpot = image.Temperatures[120, 160];
var detector = new VisualScaleDetector();
var visualScale = await detector.DetectAsync(referencePath, image);
Assert(visualScale.Success, $"Detector de escala visual falhou: {visualScale.Notes}");
Assert(visualScale.MinC is > 15 and < 30, $"VisualMin fora do esperado: {visualScale.MinC}");
Assert(visualScale.MaxC is > 35 and < 55, $"VisualMax fora do esperado: {visualScale.MaxC}");

image.Metadata.VisualScaleMinC = visualScale.MinC;
image.Metadata.VisualScaleMaxC = visualScale.MaxC;
image.Metadata.VisualScaleSource = visualScale.Source;
image.Metadata.VisualScaleConfidence = visualScale.Confidence;

var afterSpot = image.Temperatures[120, 160];
Assert(Math.Abs(afterSpot - beforeSpot) < 0.000001, "Detector/render nao pode alterar a matriz de temperaturas.");

var palette = new ThermalPaletteEngine();
var rendered = await palette.RenderThermalWithPaletteAsync(
    image.Temperatures,
    image.Width,
    image.Height,
    "Iron",
    visualScale.MinC,
    visualScale.MaxC,
    image.Metadata);

using var reference = new Bitmap(referencePath);
var comparison = CompareReference(reference, rendered, image.Width, image.Height);
Console.WriteLine($"Visual scale: {visualScale.MinC:F1} C / {visualScale.MaxC:F1} C, source={visualScale.Source}, confidence={visualScale.Confidence:F2}");
Console.WriteLine($"Scene RGB RMSE: {comparison.SceneRmse:F2}");
Console.WriteLine($"Below-color hit rate: {comparison.BelowHitRate:P1}");

Assert(comparison.SceneRmse < 125.0, $"Render muito distante da referencia. RMSE={comparison.SceneRmse:F2}");
Assert(comparison.BelowHitRate > 0.35, $"Poucos pixels frios usando BelowColor. HitRate={comparison.BelowHitRate:P1}");

await CheckMetadataCopyAsync(root, referencePath);
Console.WriteLine("Rendering checks OK.");

static (double SceneRmse, double BelowHitRate) CompareReference(Bitmap reference, byte[] rendered, int width, int height)
{
    var totalSquared = 0.0;
    var count = 0;
    var belowHits = 0;
    var belowCount = 0;

    for (var y = 24; y < height - 28; y += 2)
    {
        for (var x = 4; x < width - 58; x += 2)
        {
            var original = reference.GetPixel(x, y);
            if (IsLikelyOverlay(original))
            {
                continue;
            }

            var idx = ((y * width) + x) * 4;
            var b = rendered[idx];
            var g = rendered[idx + 1];
            var r = rendered[idx + 2];
            var dr = original.R - r;
            var dg = original.G - g;
            var db = original.B - b;
            totalSquared += (dr * dr) + (dg * dg) + (db * db);
            count++;

            if (original.R < 75 && original.G < 75 && original.B < 75)
            {
                belowCount++;
                if (Math.Abs(r - 50) <= 18 && Math.Abs(g - 50) <= 18 && Math.Abs(b - 50) <= 18)
                {
                    belowHits++;
                }
            }
        }
    }

    var rmse = count == 0 ? double.MaxValue : Math.Sqrt(totalSquared / (count * 3.0));
    var hitRate = belowCount == 0 ? 0.0 : belowHits / (double)belowCount;
    return (rmse, hitRate);
}

static async Task CheckMetadataCopyAsync(string root, string referencePath)
{
    var exif = new ExifToolService();
    if (!exif.IsExifToolAvailable())
    {
        Console.WriteLine("ExifTool indisponivel; teste de copia de metadados ignorado.");
        return;
    }

    var tempPath = Path.Combine(root, "tests", "ThermixStudio.RenderingChecks", "metadata_copy_check.jpg");
    using (var source = new Bitmap(referencePath))
    using (var clone = new Bitmap(source.Width, source.Height))
    using (var graphics = Graphics.FromImage(clone))
    {
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        clone.Save(tempPath, ImageFormat.Jpeg);
    }

    var service = new ImageMetadataPreservationService(exif);
    var copied = await service.CopyOriginalMetadataAsync(referencePath, tempPath);
    Assert(copied, "ExifTool deve copiar metadados originais para o export.");

    var json = await exif.TryGetAllMetadataJsonAsync(tempPath);
    Assert(!string.IsNullOrWhiteSpace(json) && json.Contains("PaletteName", StringComparison.OrdinalIgnoreCase),
        "Export com metadados deve preservar PaletteName.");
}

static bool IsLikelyOverlay(Color color)
{
    var max = Math.Max(color.R, Math.Max(color.G, color.B));
    var min = Math.Min(color.R, Math.Min(color.G, color.B));
    var brightness = (color.R + color.G + color.B) / 3;
    return brightness > 180 && max - min < 50;
}

static string FindRepositoryRoot()
{
    var current = AppContext.BaseDirectory;
    while (!string.IsNullOrWhiteSpace(current))
    {
        if (File.Exists(Path.Combine(current, "ThermixStudio.slnx")) &&
            File.Exists(Path.Combine(current, "FLIR0192.jpg")))
        {
            return current;
        }

        var parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || parent == current)
        {
            break;
        }

        current = parent;
    }

    throw new DirectoryNotFoundException("Raiz do repositorio nao encontrada.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
