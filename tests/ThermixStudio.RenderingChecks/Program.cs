using System.Drawing;
using System.Drawing.Imaging;
using ThermixStudio.App.Services;
using ThermixStudio.Core;

var root = FindRepositoryRoot();

// ─── Teste de hipótese: prefixo "Máx." / "Min" / "~" ──────────────────
Console.WriteLine("\n=== Teste de hipótese dos prefixos ===");
await TestPrefixHypothesis(root, "FLIR0060");
await TestPrefixHypothesis(root, "FLIR0065");

var referencePath = Path.Combine(root, "FLIR0192.jpg");
if (!File.Exists(referencePath))
{
    throw new FileNotFoundException("Arquivo de referencia FLIR0192.jpg nao encontrado.", referencePath);
}

var exifTool = new ExifToolService();
var analysis = new ThermalAnalysisService(exifTool);
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

// ─── Batch validation: all termograms in Termogramas/ ──────────────────
Console.WriteLine("\n=== Batch validation: Termogramas/ ===");
var batchDir = Path.Combine(root, "Termogramas");
if (Directory.Exists(batchDir))
{
    var jpgs = Directory.GetFiles(batchDir, "*.jpg");
    var results = new List<(string name, bool ok, string note)>();
    foreach (var jpg in jpgs)
    {
        var name = Path.GetFileNameWithoutExtension(jpg);
        try
        {
            var img = await analysis.LoadImageAsync(jpg);
            var ok = true;
            var notes = new List<string>();

            // Check 1: dimensions
            if (img.Width != 320 || img.Height != 240)
            { ok = false; notes.Add($"dims={img.Width}x{img.Height}"); }

            // Check 2: metadata completeness
            if (img.Metadata.PlanckR1 is null) { ok = false; notes.Add("no PlanckR1"); }
            if (img.Metadata.PlanckR2 is null) { ok = false; notes.Add("no PlanckR2"); }
            if (img.Metadata.EmbeddedPaletteBgra is not { Length: 1024 }) { ok = false; notes.Add("no embedded palette"); }
            if (img.Metadata.ImageTemperatureMinK is null) { ok = false; notes.Add("no ImageTempMin"); }

            // Check 3: VisualScale detection
            var vs = await detector.DetectAsync(jpg, img);
            if (!vs.Success) { notes.Add($"scale fail: {vs.Notes}"); }
            else { notes.Add($"scale {vs.MinC:F1}-{vs.MaxC:F1}C src={vs.Source}"); }

            // Check 4: Reticle position (sampling original JPEG)
            using var bmp = new Bitmap(jpg);
            int retWhite = 0; int retTotal = 0;
            int cx = bmp.Width / 2; int cy = bmp.Height / 2;
            for (int dx = -12; dx <= 12; dx++)
            {
                if (cx + dx < 0 || cx + dx >= bmp.Width) continue;
                var p = bmp.GetPixel(cx + dx, cy);
                int mx = Math.Max(p.R, Math.Max(p.G, p.B));
                int mn = Math.Min(p.R, Math.Min(p.G, p.B));
                if (mx - mn <= 55 && (p.R + p.G + p.B) / 3 >= 155) retWhite++;
                retTotal++;
            }
            double retCoverage = retTotal > 0 ? (double)retWhite / retTotal : 0;
            if (retCoverage < 0.2) { notes.Add($"reticle H low: {retCoverage:P0}"); }

            results.Add((name, ok, string.Join("; ", notes)));
        }
        catch (Exception ex)
        {
            results.Add((name, false, ex.Message));
        }
    }

    var failures = results.Where(r => !r.ok).ToList();
    Console.WriteLine($"Total: {results.Count}, Passed: {results.Count - failures.Count}, Failed: {failures.Count}");
    foreach (var f in failures)
        Console.WriteLine($"  FAIL {f.name}: {f.note}");
    foreach (var r in results.Where(r => r.ok).Take(5))
        Console.WriteLine($"  OK   {r.name}: {r.note}");
    if (results.Count > 5) Console.WriteLine($"  ... and {results.Count - 5} more OK");
}

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

static async Task TestPrefixHypothesis(string root, string name)
{
    var path = Path.Combine(root, "Termogramas", $"{name}.jpg");
    if (!File.Exists(path)) { Console.WriteLine($"{name}: arquivo nao encontrado"); return; }

    var exifTool = new ExifToolService();
    var analysis = new ThermalAnalysisService(exifTool);
    var img = await analysis.LoadImageAsync(path);

    // Min/Max reais da matriz
    double matMin = double.MaxValue, matMax = double.MinValue;
    for (int y = 0; y < img.Height; y++)
        for (int x = 0; x < img.Width; x++)
        {
            var t = img.Temperatures[y, x];
            if (t < matMin) matMin = t;
            if (t > matMax) matMax = t;
        }

    // Escala visual do EXIF
    double? exifMin = img.Metadata.ImageTemperatureMinK.HasValue ? img.Metadata.ImageTemperatureMinK.Value - 273.15 : null;
    double? exifMax = img.Metadata.ImageTemperatureMaxK.HasValue ? img.Metadata.ImageTemperatureMaxK.Value - 273.15 : null;

    // Tspot (centro da imagem = posição padrão do retículo)
    double spotC = img.Temperatures[img.Height / 2, img.Width / 2];

    // Converter spot para Kelvin e verificar se é "dízima"
    double spotK = spotC + 273.15;
    bool isApproximate = Math.Abs(spotK - Math.Round(spotK)) > 0.05;

    Console.WriteLine($"{name}:");
    Console.WriteLine($"  Matriz: min={matMin:F2}°C  max={matMax:F2}°C");
    Console.WriteLine($"  EXIF:   min={exifMin:F2}°C  max={exifMax:F2}°C");
    Console.WriteLine($"  Tspot (centro): {spotC:F3}°C = {spotK:F3}K");
    Console.WriteLine($"  Escala EXIF > matriz? max: {exifMax > matMax}  min: {exifMin < matMin}");
    Console.WriteLine($"  Spot em K é dízima? {isApproximate} (delta={Math.Abs(spotK - Math.Round(spotK)):F3})");

    // Hipótese "Máx.": escala max > matriz max → spot mostra "Máx."
    bool maxPrefix = exifMax.HasValue && exifMax.Value > matMax + 0.3;
    // Hipótese "Min": escala min < matriz min
    bool minPrefix = exifMin.HasValue && exifMin.Value < matMin - 0.3;
    // Hipótese "~": valor em K é dízima
    bool tilPrefix = isApproximate;

    Console.WriteLine($"  Hipótese: Máx={maxPrefix}  Min={minPrefix}  ~={tilPrefix}");
    Console.WriteLine();
}
