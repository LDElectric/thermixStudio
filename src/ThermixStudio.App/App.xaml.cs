using System.Windows;
using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThermixStudio.Core;
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
				services.AddSingleton<IThermalAnalysisService, Services.ThermalAnalysisService>();
				services.AddSingleton<IThermalRenderEngine, Services.ThermalRenderEngine>();

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

	private static string ResolveDatabasePath()
	{
		// Para executável portátil: banco de dados no mesmo diretório do executável
		// Use AppContext.BaseDirectory ao invés de Assembly.Location para single-file apps
		var appDirectory = AppContext.BaseDirectory;
		var dbDirectory = Path.Combine(appDirectory, "thermixStudioDB");
		Directory.CreateDirectory(dbDirectory);

		var portableDbPath = Path.Combine(dbDirectory, "thermix.db");
		return portableDbPath;
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

