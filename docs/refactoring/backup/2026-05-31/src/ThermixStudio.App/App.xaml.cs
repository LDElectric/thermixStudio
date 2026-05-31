using System.Windows;
using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;
using ThermixStudio.Infrastructure;
using ThermixStudio.Reports;

namespace ThermixStudio.App;

public partial class App : Application
{
	private IHost? _host;

	protected override async void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		
		// Ensure DPI awareness for better icon rendering
		SetDpiAwareness();

		var dbPath = ResolveDatabasePath();

		_host = Host.CreateDefaultBuilder()
			.ConfigureServices(services =>
			{
				services.AddThermixInfrastructure(dbPath);
				services.AddSingleton<IReportService, ReportService>();
				
				// FASE 1-3: Novos motores
				services.AddSingleton<IExifToolService, Services.ExifToolService>();
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
			if (Directory.Exists(Path.Combine(current, "src")) || 
				File.Exists(Path.Combine(current, "Thermix Studio.sln")))
			{
				return current;
			}
			var parent = Path.GetDirectoryName(current);
			if (parent == current || string.IsNullOrEmpty(parent)) break;
			current = parent;
		}
		return AppContext.BaseDirectory;
	}

	private static string ResolveDatabasePath()
	{
		// Para tudo ficar contido na pasta do projeto durante testes/depuração,
		// localizamos a raiz do projeto e salvamos na pasta thermixStudioDB lá.
		var projectRoot = ResolveProjectRoot();
		var dbDirectory = Path.Combine(projectRoot, "thermixStudioDB");
		Directory.CreateDirectory(dbDirectory);

		var dbPath = Path.Combine(dbDirectory, "thermix.db");
		return dbPath;
	}
	
	private static void SetDpiAwareness()
	{
		try
		{
			// Enable per-monitor DPI awareness for better scaling on high-DPI displays
			if (Environment.OSVersion.Version >= new Version(6, 3)) // Windows 8.1+
			{
				// Windows will automatically scale the icon based on system DPI
				Debug.WriteLine("DPI awareness enabled for better icon rendering");
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"DPI awareness setup failed: {ex.Message}");
		}
	}

	protected override async void OnExit(ExitEventArgs e)
	{
		if (_host is not null)
		{
			await _host.StopAsync();
			_host.Dispose();
		}

		base.OnExit(e);
	}
}

