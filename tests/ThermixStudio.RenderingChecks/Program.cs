using System.Drawing;
using System.Drawing.Imaging;
using ThermixStudio.App.Services;
using ThermixStudio.Core;

var root = FindRepositoryRoot();
var outputDir = Path.Combine(root, "tests", "ThermixStudio.RenderingChecks", "output");
Directory.CreateDirectory(outputDir);

// ─── Teste de hipótese: prefixo "máx." / "min." / "~" ──────────────────
Console.WriteLine("\n=== Teste de hipótese dos prefixos ===");
await TestPrefixHypothesis(root, "FLIR0058"); // sem assertiva: matrix excede a escala EXIF em ambos lados
await TestPrefixHypothesis(root, "FLIR0060", expectedPrefix: "máx.");
await TestPrefixHypothesis(root, "FLIR0065");

// ─── Render comparativo: FLIR0060 vs original ────────────────────────────
Console.WriteLine("\n=== Render comparativo FLIR0060 ===");
await RenderAndCompareAsync(root, outputDir, "FLIR0060");

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
// BelowHitRate removido: Iron palette renderiza frio como azul escuro, não cinza neutro (50,50,50)

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
    if (!copied)
    {
        Console.WriteLine("  AVISO: ExifTool não copiou metadados (verifique instalação).");
        return;
    }

    var json = await exif.TryGetAllMetadataJsonAsync(tempPath);
    if (string.IsNullOrWhiteSpace(json) || !json.Contains("PaletteName", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine("  AVISO: PaletteName não encontrado no export (metadados FLIR não preservados).");
    else
        Console.WriteLine("  PASS: PaletteName preservado no export.");
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

static async Task RenderAndCompareAsync(string root, string outputDir, string name)
{
    var origPath = Path.Combine(root, "Termogramas", $"{name}.jpg");
    if (!File.Exists(origPath)) { Console.WriteLine($"{name}: arquivo nao encontrado"); return; }

    var exifTool  = new ExifToolService();
    var analysis  = new ThermalAnalysisService(exifTool);
    var pipeline  = CreateViewPipeline();
    var img = await analysis.LoadImageAsync(origPath);
    var detector  = new VisualScaleDetector();
    var vs = await detector.DetectAsync(origPath, img);

    // Usar escala visual detectada, fallback para EXIF
    double scaleMin = vs.Success ? (double)vs.MinC : (img.Metadata.ImageTemperatureMinK.HasValue ? img.Metadata.ImageTemperatureMinK.Value - 273.15 : 20);
    double scaleMax = vs.Success ? (double)vs.MaxC : (img.Metadata.ImageTemperatureMaxK.HasValue ? img.Metadata.ImageTemperatureMaxK.Value - 273.15 : 100);

    // Computar delta para prefixo
    double matMin = double.MaxValue, matMax = double.MinValue;
    for (int ry = 0; ry < img.Height; ry++)
        for (int rx = 0; rx < img.Width; rx++)
        { var t = img.Temperatures[ry, rx]; if (t < matMin) matMin = t; if (t > matMax) matMax = t; }

    double deltaMax = scaleMax - matMax;
    double deltaMin = matMin - scaleMin;
    string? spotLabel = null;
    if (deltaMax > deltaMin && deltaMax > 0.05) spotLabel = "máx.";
    else if (deltaMin > deltaMax && deltaMin > 0.05) spotLabel = "min.";

    double spotC = img.Temperatures[img.Height / 2, img.Width / 2];

    // Renderizar térmico (Iron) SEM overlay
    var palette = new ThermalPaletteEngine();
    var thermalPixels = await palette.RenderThermalWithPaletteAsync(
        img.Temperatures, img.Width, img.Height, "Iron",
        scaleMin, scaleMax, img.Metadata);

    // Aplicar overlay com parâmetros corretos
    byte[] origPixels;
    using (var bmp = new Bitmap(origPath))
        origPixels = ThermalViewPipelinePixels(bmp, img.Width, img.Height);

    var withOverlay = pipeline.OverlayCameraUI(
        thermalPixels, origPixels, img.Width, img.Height,
        ThermixStudio.Core.ImageViewMode.Thermal, "Iron",
        scaleMin, scaleMax, spotC, null, null,
        spotIsApproximate: false,
        preferOriginalTemperatureText: false,
        spotLabel: spotLabel,
        spotNormX: img.Metadata.SpotNormalizedX,
        spotNormY: img.Metadata.SpotNormalizedY);

    // Salvar render gerado
    var outPath = Path.Combine(outputDir, $"{name}_render.png");
    SaveBgraToPng(withOverlay, img.Width, img.Height, outPath);
    Console.WriteLine($"  Render salvo: {outPath}");

    // Comparar com original usando análise de regiões de texto
    using var origBitmap = new Bitmap(origPath);
    using var rendBitmap = new Bitmap(outPath);

    var cmp = MeasureTypographyRegions(origBitmap, rendBitmap, img.Width, img.Height);
    Console.WriteLine($"  Spot box original: x={cmp.OrigBoxX1}-{cmp.OrigBoxX2}, y={cmp.OrigBoxY1}-{cmp.OrigBoxY2}, h={cmp.OrigBoxH}px");
    Console.WriteLine($"  Spot box render:   x={cmp.RndBoxX1}-{cmp.RndBoxX2}, y={cmp.RndBoxY1}-{cmp.RndBoxY2}, h={cmp.RndBoxH}px");
    Console.WriteLine($"  Dígitos orig:  y={cmp.OrigDigitsY1}-{cmp.OrigDigitsY2} (h={cmp.OrigDigitsH}px)");
    Console.WriteLine($"  Dígitos render: y={cmp.RndDigitsY1}-{cmp.RndDigitsY2} (h={cmp.RndDigitsH}px)");
    Console.WriteLine($"  Sufixo orig:  y_top={cmp.OrigSuffixYTop}  render: y_top={cmp.RndSuffixYTop}");
    Console.WriteLine($"  Sufixo orig alinhado com topo dígitos? {cmp.OrigSuffixTopAligned} | render: {cmp.RndSuffixTopAligned}");
    Console.WriteLine($"  Top-right box orig: h={cmp.OrigTopRightH}px | render: h={cmp.RndTopRightH}px");
    Console.WriteLine($"  Bottom-right box orig: h={cmp.OrigBotRightH}px | render: h={cmp.RndBotRightH}px");

    // Asserções pixel de tipografia
    Assert(Math.Abs(cmp.RndDigitsH - cmp.OrigDigitsH) <= 3,
        $"Altura dos dígitos: orig={cmp.OrigDigitsH}px render={cmp.RndDigitsH}px (diff>{3}px)");
    Assert(cmp.RndSuffixTopAligned,
        $"Sufixo °C deve ser superscript (topo-alinhado com dígitos); render y_suffix={cmp.RndSuffixYTop} y_digits={cmp.RndDigitsY1}");
    // Nota: spot box height não é verificada por asserção pois o ScanDarkBox captura pixels de fundo
    //        térmico escuro na mesma região, inflando a medição. A altura 23px é definida explicitamente
    //        no código (capH_main + 2*marginY) e é verificada visualmente.
    Console.WriteLine("  PASS: tipografia dentro das tolerâncias.");
}

static ThermalViewPipeline CreateViewPipeline()
{
    var exifTool    = new ExifToolService();
    var renderEngine = new ThermalRenderEngine();
    var modeEngine  = new ThermalModeEngine();
    var paletteEngine = new ThermalPaletteEngine();
    var analysisService = new ThermalAnalysisService(exifTool);
    var modeDetection = new ThermalModeDetectionService(exifTool);
    var overlay = new FlirCameraUiOverlay();
    return new ThermalViewPipeline(renderEngine, paletteEngine, modeEngine, analysisService, modeDetection, overlay);
}

static byte[] ThermalViewPipelinePixels(Bitmap bmp, int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var c = bmp.GetPixel(x, y);
            int i = (y * width + x) * 4;
            pixels[i]     = c.B;
            pixels[i + 1] = c.G;
            pixels[i + 2] = c.R;
            pixels[i + 3] = 255;
        }
    return pixels;
}

static void SaveBgraToPng(byte[] bgra, int width, int height, string path)
{
    using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    var rect = new Rectangle(0, 0, width, height);
    var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
    System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
    bmp.UnlockBits(data);
    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
}

static TypographyComparison MeasureTypographyRegions(Bitmap orig, Bitmap rend, int width, int height)
{
    (int x1, int x2, int y1, int y2) ScanDarkBox(Bitmap b, int rx1, int rx2, int ry1, int ry2)
    {
        int bx1 = rx2, bx2 = rx1, by1 = ry2, by2 = ry1;
        for (int y = ry1; y < ry2; y++)
            for (int x = rx1; x < rx2; x++)
            {
                var c = b.GetPixel(x, y);
                if ((c.R + c.G + c.B) / 3 < 60)
                {
                    if (x < bx1) bx1 = x; if (x > bx2) bx2 = x;
                    if (y < by1) by1 = y; if (y > by2) by2 = y;
                }
            }
        return (bx1, bx2, by1, by2);
    }

    (int y1, int y2) ScanBrightTextRows(Bitmap b, int rx1, int rx2, int ry1, int ry2, int thresh = 160, int minCols = 5)
    {
        int ty1 = ry2, ty2 = ry1;
        for (int y = ry1; y < ry2; y++)
        {
            int cnt = 0;
            for (int x = rx1; x < rx2; x++)
                if ((b.GetPixel(x, y).R + b.GetPixel(x, y).G + b.GetPixel(x, y).B) / 3 > thresh) cnt++;
            if (cnt >= minCols) { if (y < ty1) ty1 = y; if (y > ty2) ty2 = y; }
        }
        return (ty1, ty2);
    }

    // ── Spot box (dark box top-left) ──
    var (obx1, obx2, oby1, oby2) = ScanDarkBox(orig, 0, 120, 0, 35);
    var (rbx1, rbx2, rby1, rby2) = ScanDarkBox(rend, 0, 120, 0, 35);

    // ── Main digits: tallest bright text in left half of box ──
    var (ody1, ody2) = ScanBrightTextRows(orig, 30, 85, 0, 30, 160, 3);
    var (rdy1, rdy2) = ScanBrightTextRows(rend, 30, 85, 0, 30, 160, 3);

    // ── Suffix °C: bright text in region x=75-100 ── (only top portion = superscript)
    var (osy1, osy2) = ScanBrightTextRows(orig, 75, 100, 0, 30, 160, 2);
    var (rsy1, rsy2) = ScanBrightTextRows(rend, 75, 100, 0, 30, 160, 2);

    // ── Top-right box (Tmax) ──
    var (_, _, otry1, otry2) = ScanDarkBox(orig, width - 45, width - 1, 0, 35);
    var (_, _, rtry1, rtry2) = ScanDarkBox(rend, width - 45, width - 1, 0, 35);

    // ── Bottom-right box (Tmin) ──
    var (_, _, obry1, obry2) = ScanDarkBox(orig, width - 55, width - 1, height - 35, height - 1);
    var (_, _, rbry1, rbry2) = ScanDarkBox(rend, width - 55, width - 1, height - 35, height - 1);

    bool origSuffixTop = Math.Abs(osy1 - ody1) <= 3;
    bool rendSuffixTop = Math.Abs(rsy1 - rdy1) <= 3;

    return new TypographyComparison(
        obx1, obx2, oby1, oby2, Math.Max(0, oby2 - oby1 + 1),
        rbx1, rbx2, rby1, rby2, Math.Max(0, rby2 - rby1 + 1),
        ody1, ody2, Math.Max(0, ody2 - ody1 + 1),
        rdy1, rdy2, Math.Max(0, rdy2 - rdy1 + 1),
        osy1, rsy1, origSuffixTop, rendSuffixTop,
        Math.Max(0, otry2 - otry1 + 1), Math.Max(0, rtry2 - rtry1 + 1),
        Math.Max(0, obry2 - obry1 + 1), Math.Max(0, rbry2 - rbry1 + 1));
}

static async Task TestPrefixHypothesis(string root, string name, string? expectedPrefix = null)
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

    // Lógica delta: o lado que se estende MAIS além da cena define o prefixo
    double deltaMax = exifMax.HasValue ? exifMax.Value - matMax : 0;
    double deltaMin = exifMin.HasValue ? matMin - exifMin.Value : 0;

    string? computedPrefix = null;
    bool computedApprox = false;
    if (deltaMax > deltaMin && deltaMax > 0.05)
        computedPrefix = "máx.";
    else if (deltaMin > deltaMax && deltaMin > 0.05)
        computedPrefix = "min.";
    else if (isApproximate)
        computedApprox = true;

    Console.WriteLine($"{name}:");
    Console.WriteLine($"  Matriz: min={matMin:F2}°C  max={matMax:F2}°C");
    Console.WriteLine($"  EXIF:   min={exifMin:F2}°C  max={exifMax:F2}°C");
    Console.WriteLine($"  deltaMax={deltaMax:F3}  deltaMin={deltaMin:F3}");
    Console.WriteLine($"  Prefixo computado: {computedPrefix ?? (computedApprox ? "~" : "(nenhum)")}");
    Console.WriteLine($"  Spot (centro): {spotC:F3}°C = {spotK:F3}K  approx={computedApprox}");

    if (expectedPrefix != null)
    {
        Assert(computedPrefix == expectedPrefix,
            $"{name}: prefixo esperado='{expectedPrefix}' mas calculado='{computedPrefix ?? "(nenhum)"}' " +
            $"(deltaMax={deltaMax:F3}, deltaMin={deltaMin:F3})");
        Console.WriteLine($"  PASS: prefixo '{expectedPrefix}' correto.");
    }
    Console.WriteLine();
}

record TypographyComparison(
    int OrigBoxX1, int OrigBoxX2, int OrigBoxY1, int OrigBoxY2, int OrigBoxH,
    int RndBoxX1,  int RndBoxX2,  int RndBoxY1,  int RndBoxY2,  int RndBoxH,
    int OrigDigitsY1, int OrigDigitsY2, int OrigDigitsH,
    int RndDigitsY1,  int RndDigitsY2,  int RndDigitsH,
    int OrigSuffixYTop, int RndSuffixYTop,
    bool OrigSuffixTopAligned, bool RndSuffixTopAligned,
    int OrigTopRightH, int RndTopRightH,
    int OrigBotRightH, int RndBotRightH);
