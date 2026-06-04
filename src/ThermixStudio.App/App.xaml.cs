using System.Windows;
using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Core.Thermal;
using ThermixStudio.Infrastructure;
using ThermixStudio.Reports;

namespace ThermixStudio.App;

public partial class App : Application
{
	private IHost? _host;

	protected override async void OnStartup(StartupEventArgs e)
	{
		if (e.Args.Contains("--batch-calibrate"))
		{
			await RunBatchCalibrateAsync();
			Shutdown();
			return;
		}

		base.OnStartup(e);
		SetDpiAwareness();

		var dbPath = ResolveDatabasePath();
		_host = Host.CreateDefaultBuilder()
			.ConfigureServices(services =>
			{
				services.AddThermixInfrastructure(dbPath);
				services.AddSingleton<IReportService, ReportService>();
				services.AddSingleton<IExifToolService, Services.ExifToolService>();
				services.AddSingleton<IThermalModeDetectionService, Services.ThermalModeDetectionService>();
								services.AddSingleton<IThermalRenderEngine, Services.ThermalRenderEngine>();
				services.AddSingleton<IThermalPaletteEngine, Services.ThermalPaletteEngine>();
				services.AddSingleton<IThermalModeEngine, Services.ThermalModeEngine>();
				services.AddSingleton<IThermalViewPipeline, Services.ThermalViewPipeline>();
				services.AddSingleton<IVisualScaleDetector, Services.VisualScaleDetector>();
				services.AddSingleton<IImageMetadataPreservationService, Services.ImageMetadataPreservationService>();
				services.AddSingleton<IThermalAnalysisService, Services.ThermalAnalysisService>();
				services.AddSingleton<ViewModels.MainViewModel>();
				services.AddTransient<ViewModels.ReportEditorViewModel>();
				services.AddTransient<ReportEditorWindow>();
				services.AddSingleton<MainWindow>();
			})
			.Build();

		using (var scope = _host.Services.CreateScope())
		{
var dataService = scope.ServiceProvider.GetRequiredService<IAppDataService>();
			await dataService.InitializeAsync();
		}

		var window = _host.Services.GetRequiredService<MainWindow>();
		window.Show();
	}

	private static string ResolveProjectRoot()
	{
		var current = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(current))
		{
			if (Directory.Exists(Path.Combine(current, "src")) || File.Exists(Path.Combine(current, "Thermix Studio.sln")))
				return current;
			var parent = Path.GetDirectoryName(current);
			if (parent == current || string.IsNullOrEmpty(parent)) break;
			current = parent;
		}
		return AppContext.BaseDirectory;
	}

	private static string ResolveDatabasePath()
	{
		var projectRoot = ResolveProjectRoot();
		var dbDirectory = Path.Combine(projectRoot, "thermixStudioDB");
		Directory.CreateDirectory(dbDirectory);
		return Path.Combine(dbDirectory, "thermix.db");
	}

	private static void SetDpiAwareness()
	{
		try { if (Environment.OSVersion.Version >= new Version(6, 3)) Debug.WriteLine("DPI awareness enabled"); }
		catch (Exception ex) { Debug.WriteLine($"DPI setup failed: {ex.Message}"); }
	}

	protected override async void OnExit(ExitEventArgs e)
	{
		if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
		base.OnExit(e);
	}

	private static async Task RunBatchCalibrateAsync()
	{
		Console.WriteLine("=== BATCH CALIBRATE ===");
		var root = ResolveProjectRoot();
		var termogramasDir = Path.Combine(root, "Termogramas");
		var outputDir = Path.Combine(root, "Termogramas", "calibrados");
		Directory.CreateDirectory(outputDir);

		var jpgs = Directory.GetFiles(termogramasDir, "*.jpg")
			.Concat(Directory.GetFiles(root, "FLIR0192.jpg"))
			.Concat(Directory.GetFiles(root, "2.jpg"))
			.Distinct().ToArray();

		Console.WriteLine($"Encontrados {jpgs.Length} termogramas.");
		var analysis = new Services.ThermalAnalysisService(new Services.ExifToolService());
		var engine = new Services.ThermalPaletteEngine();

		int ok = 0, fail = 0;
		foreach (var jpg in jpgs)
		{
			var name = Path.GetFileNameWithoutExtension(jpg);
			try
			{
				var img = await analysis.LoadImageAsync(jpg);
				var meta = img.Metadata;
				double minC = meta.PaletteScaleMinC ?? img.Temperatures.Cast<double>().Min();
				double maxC = meta.PaletteScaleMaxC ?? img.Temperatures.Cast<double>().Max();
				if (maxC <= minC) maxC = minC + 0.01;

				var profile = RenderProfile.FromMetadata(meta, minC, maxC);
				var rendered = await engine.RenderWithProfileAsync(img.Temperatures, img.Width, img.Height, "Iron", profile, meta);

				using var origBmp = new System.Drawing.Bitmap(jpg);
				var rect = new System.Drawing.Rectangle(0, 0, img.Width, img.Height);
				var origData = origBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				var origPixels = new byte[origData.Stride * img.Height];
				System.Runtime.InteropServices.Marshal.Copy(origData.Scan0, origPixels, 0, origPixels.Length);
				origBmp.UnlockBits(origData);

				var lut = TemperatureColorLut.Build(img.Temperatures, origPixels, img.Width, img.Height, minC, maxC, numBins: 1024);
				lut.Apply(img.Temperatures, rendered, img.Width, img.Height, minC, maxC);

				using var bmp = new System.Drawing.Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				System.Runtime.InteropServices.Marshal.Copy(rendered, 0, data.Scan0, rendered.Length);
				bmp.UnlockBits(data);

				var outPath = Path.Combine(outputDir, $"{name}_calibrated.jpg");
				bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Jpeg);
				Console.WriteLine($"  OK  {name}  ({minC:F1}~{maxC:F1}C)");
				ok++;
			}
			catch (Exception ex) { Console.WriteLine($"  FAIL {name}: {ex.Message}"); fail++; }
		}
		Console.WriteLine($"Batch concluido: {ok} OK, {fail} falhas. Output: {outputDir}");
	}
}
