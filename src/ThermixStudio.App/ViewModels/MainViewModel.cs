using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using System.Windows.Media;
using ThermixStudio.Core;
using ThermixStudio.Core.Services;

namespace ThermixStudio.App.ViewModels;

public enum AnalysisTool
{
    None,
    Hand,
    Spot,
    Area,
    Line,
    Circle,
    AutoAdjustRegion,
    IllustrationArrow,
    IllustrationRectangle,
    IllustrationEllipse,
    IllustrationText
}

public enum ImageViewMode
{
    Original,
    Thermal,
    Visible,
    Fusion,
    Blending,
    PiP,
    Msx
}

public enum IsothermMode
{
    Above,
    Below,
    Interval,
    Humidity,
    Insulation,
    Custom
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int CurrentVisualInferenceRuleVersion = 2;

    private readonly IAppDataService _dataService;
    private readonly IThermalAnalysisService _thermalService;
    private readonly IThermalViewPipeline _viewPipeline;
    private readonly IReportService _reportService;
    private readonly IVisualScaleDetector _visualScaleDetector;
    private readonly IImageMetadataPreservationService _metadataPreservationService;
    private readonly IServiceProvider _serviceProvider;

    private ThermalImageData? _loadedImage;
    private bool _loadingThermogram;
    private global::ThermixStudio.Core.ImageViewMode? _metadataDetectedMode;
    private global::ThermixStudio.Core.ImageViewMode? _inferredCaptureMode;

        // ── Cache LRU de ThermalImageData (evita reload completo ao alternar termogramas) ──
        private const int MaxCachedImages = 5;
        private readonly Dictionary<string, ThermalImageData> _imageCache = new();
        private readonly LinkedList<string> _imageCacheLru = new();
        // ── Cache LRU de pixels renderizados (evita re-render ao voltar ao termograma) ──
        private readonly Dictionary<(string path, ThermalPalette palette, double min, double max, ImageViewMode mode), byte[]> _renderCache = new();

        // ── Cancellation: cancela carga anterior quando usuário troca de termograma ──
        private CancellationTokenSource? _loadCts;
        // ── Debounce: evita re-render a cada tick de slider ──
        private CancellationTokenSource? _renderDebounceCts;

        private static readonly string _debugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "ThermixStudio_DEBUG.log");

    [ObservableProperty]
    private Thermogram? selectedThermogram;

    [ObservableProperty]
    private Inspection? selectedInspection;

    [ObservableProperty]
    private string? currentImagePath;

    [ObservableProperty]
    private string statusMessage = "Pronto.";

    [ObservableProperty]
    private double isothermThresholdC = 80.0;

    [ObservableProperty]
    private double isothermUpperThresholdC = 120.0;

    [ObservableProperty]
    private IsothermMode selectedIsothermMode = IsothermMode.Above;

    [ObservableProperty]
    private double humidityRelativeLimit = 70.0;

    [ObservableProperty]
    private double insulationIndoorC = 22.0;

    [ObservableProperty]
    private double insulationOutdoorC = 12.0;

    [ObservableProperty]
    private double insulationThermalIndex = 0.7;

    [ObservableProperty]
    private string thermogramEquipmentTag = string.Empty;

    [ObservableProperty]
    private string thermogramEquipmentDescription = string.Empty;

    [ObservableProperty]
    private string thermogramEquipmentLocation = string.Empty;

    [ObservableProperty]
    private string thermogramNotes = string.Empty;

    [ObservableProperty]
    private EquipmentCriticality thermogramCriticality = EquipmentCriticality.Medium;

    [ObservableProperty]
    private AnalysisTool activeTool = AnalysisTool.Spot;

    [ObservableProperty]
    private ImageViewMode imageViewMode = ImageViewMode.Thermal;

    [ObservableProperty]
    private ImageSource? displayImage;

    [ObservableProperty]
    private ThermalMeasurement? selectedMeasurement;

    [ObservableProperty]
    private string? pairedVisibleImagePath;

    [ObservableProperty]
    private bool autoScaleEnabled = true;

    [ObservableProperty]
    private double levelMinC;

    [ObservableProperty]
    private double levelMaxC;

    [ObservableProperty]
    private double thermalScaleFloorC;

    [ObservableProperty]
    private double thermalScaleCeilingC = 100.0;

    [ObservableProperty]
    private double? maxAdmissibleC;

    [ObservableProperty]
    private double emissivity = 0.95;

    [ObservableProperty]
    private ThermalPalette selectedPalette = ThermalPalette.Iron;

    [ObservableProperty]
    private string currentScaleLabel = "Escala: -";

    [ObservableProperty]
    private double blendFactor = 0.55;

    [ObservableProperty]
    private double pipScale = 0.55;

    [ObservableProperty]
    private double msxStrength = 0.10;

    private (double startX, double startY, double endX, double endY)? _autoAdjustRegion;
    private readonly Dictionary<Guid, Stack<List<ThermalIllustration>>> _illustrationUndoHistory = [];
    private bool _isRestoringIllustrationUndo;

    public bool HasAutoAdjustRegion => _autoAdjustRegion.HasValue;
    public (double startX, double startY, double endX, double endY)? AutoAdjustRegionNormalized => _autoAdjustRegion;

    public ObservableCollection<Thermogram> Thermograms { get; } = [];
    public ObservableCollection<Inspection> Inspections { get; } = [];
    public ObservableCollection<ThermalMeasurement> Measurements { get; } = [];

    public IReadOnlyList<EquipmentCriticality> CriticalityOptions { get; } =
        Enum.GetValues<EquipmentCriticality>().ToList();

    public IReadOnlyList<ThermalPalette> PaletteOptions { get; } =
    [
        ThermalPalette.Iron,
        ThermalPalette.Rainbow,
        ThermalPalette.Grayscale,
        ThermalPalette.Hotmetal,
        ThermalPalette.Arctic,
        ThermalPalette.Thermal,
        ThermalPalette.Jet,
        ThermalPalette.Hot,
        ThermalPalette.Cool,
    ];

    public IReadOnlyList<ImageViewMode> ImageViewModes { get; } =
    [
        ImageViewMode.Msx,
        ImageViewMode.Thermal,
        ImageViewMode.PiP,
        ImageViewMode.Blending,
        ImageViewMode.Visible
    ];

    public IReadOnlyList<IsothermMode> IsothermModes { get; } =
        Enum.GetValues<IsothermMode>().ToList();

    public IReadOnlyList<AnalysisTool> ToolOptions { get; } =
    [
        AnalysisTool.Spot,
        AnalysisTool.Area,
        AnalysisTool.Line,
        AnalysisTool.Circle,
        AnalysisTool.IllustrationText
    ];

    public PlotModel TrendPlotModel { get; } = new() { Background = OxyColor.FromRgb(30, 30, 46) };

    public IAsyncRelayCommand LoadDataCommand { get; }
    public IAsyncRelayCommand OpenFileCommand { get; }
    public IAsyncRelayCommand AddSpotCommand { get; }
    public IAsyncRelayCommand AddAreaCommand { get; }
    public IAsyncRelayCommand AddLineCommand { get; }
    public IAsyncRelayCommand AddCircleCommand { get; }
    public IAsyncRelayCommand AddDifferenceCommand { get; }
    public IAsyncRelayCommand AddIsothermCommand { get; }
    public IAsyncRelayCommand DefineAutoAdjustRegionCommand { get; }
    public IAsyncRelayCommand ClearAutoAdjustRegionCommand { get; }
    public IAsyncRelayCommand ApplyHumidityPresetCommand { get; }
    public IAsyncRelayCommand ApplyInsulationPresetCommand { get; }
    public IAsyncRelayCommand ExportImageCsvCommand { get; }
    public IAsyncRelayCommand ExportMeasurementsCsvCommand { get; }
    public IAsyncRelayCommand ExportIdenticalJpgCommand { get; }
    public IAsyncRelayCommand GenerateReportCommand { get; }
    public IAsyncRelayCommand SaveThermogramPropertiesCommand { get; }
    public IAsyncRelayCommand ToggleViewModeCommand { get; }
    public IAsyncRelayCommand UndoLastActionCommand { get; }
    public IAsyncRelayCommand RemoveSelectedMeasurementCommand { get; }
    public IAsyncRelayCommand RemoveSelectedThermogramCommand { get; }
    public IAsyncRelayCommand DeleteSelectionCommand { get; }
    public IAsyncRelayCommand AutoScaleCommand { get; }
    public IAsyncRelayCommand UseCameraScaleCommand { get; }

    public string ViewModeLabel => $"Modo: {GetViewModeDisplay(ImageViewMode)}";

    public bool ShowBlendControls => ImageViewMode == ImageViewMode.Blending;
    public bool ShowPipControls => ImageViewMode == ImageViewMode.PiP;
    public bool ShowMsxControls => ImageViewMode == ImageViewMode.Msx;
    public bool ShowIsothermIntervalControls => SelectedIsothermMode is IsothermMode.Above or IsothermMode.Below or IsothermMode.Interval or IsothermMode.Custom;
    public bool ShowHumidityControls => SelectedIsothermMode == IsothermMode.Humidity;
    public bool ShowInsulationControls => SelectedIsothermMode == IsothermMode.Insulation;

    public int ImagePixelWidth => _loadedImage?.Width ?? 0;
    public int ImagePixelHeight => _loadedImage?.Height ?? 0;

    public event Action<Guid>? MeasurementRemoved;
    public event Func<Task<string?>>? ReportSnapshotRequested;

    public MainViewModel(
        IAppDataService dataService,
        IThermalAnalysisService thermalService,
        IThermalViewPipeline viewPipeline,
        IReportService reportService,
        IVisualScaleDetector visualScaleDetector,
        IImageMetadataPreservationService metadataPreservationService,
        IServiceProvider serviceProvider)
    {
        _dataService = dataService;
        _thermalService = thermalService;
        _viewPipeline = viewPipeline;
        _reportService = reportService;
        _visualScaleDetector = visualScaleDetector;
        _metadataPreservationService = metadataPreservationService;
        _serviceProvider = serviceProvider;

        // ══════════════════════════════════════════════════════════════
        // TESTE: Render limpo — sem overlay de câmera (logo, escala, spot)
        // Para reativar o overlay, mude para false.
        // ══════════════════════════════════════════════════════════════
        SuppressOverlay = true;

        ConfigurePlot();

        LoadDataCommand = new AsyncRelayCommand(LoadDataAsync);
        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync);
        AddSpotCommand = new AsyncRelayCommand(ActivateSpotToolAsync);
        AddAreaCommand = new AsyncRelayCommand(ActivateAreaToolAsync);
        AddLineCommand = new AsyncRelayCommand(ActivateLineToolAsync);
        AddCircleCommand = new AsyncRelayCommand(ActivateCircleToolAsync);
        AddDifferenceCommand = new AsyncRelayCommand(AddDifferenceAsync);
        AddIsothermCommand = new AsyncRelayCommand(AddIsothermAsync);
        DefineAutoAdjustRegionCommand = new AsyncRelayCommand(ActivateAutoAdjustRegionToolAsync);
        ClearAutoAdjustRegionCommand = new AsyncRelayCommand(ClearAutoAdjustRegionAsync);
        ApplyHumidityPresetCommand = new AsyncRelayCommand(ApplyHumidityPresetAsync);
        ApplyInsulationPresetCommand = new AsyncRelayCommand(ApplyInsulationPresetAsync);
        ExportImageCsvCommand = new AsyncRelayCommand(ExportImageCsvAsync);
        ExportMeasurementsCsvCommand = new AsyncRelayCommand(ExportMeasurementsCsvAsync);
        ExportIdenticalJpgCommand = new AsyncRelayCommand(ExportIdenticalJpgAsync);
        GenerateReportCommand = new AsyncRelayCommand(GenerateReportAsync);
        SaveThermogramPropertiesCommand = new AsyncRelayCommand(SaveThermogramPropertiesAsync);
        ToggleViewModeCommand = new AsyncRelayCommand(ToggleViewModeAsync);
        UndoLastActionCommand = new AsyncRelayCommand(UndoLastActionAsync);
        RemoveSelectedMeasurementCommand = new AsyncRelayCommand(RemoveSelectedMeasurementAsync);
        RemoveSelectedThermogramCommand = new AsyncRelayCommand(RemoveSelectedThermogramAsync);
        DeleteSelectionCommand = new AsyncRelayCommand(DeleteSelectionAsync);
        AutoScaleCommand = new AsyncRelayCommand(ApplyAutoScaleAsync);
        UseCameraScaleCommand = new AsyncRelayCommand(ApplyCameraScaleAsync);
    }

    partial void OnImageViewModeChanged(ImageViewMode value)
    {
           StatusMessage = $"Modo: {GetViewModeDisplay(value)}";
        OnPropertyChanged(nameof(ViewModeLabel));
        OnPropertyChanged(nameof(ShowBlendControls));
        OnPropertyChanged(nameof(ShowPipControls));
        OnPropertyChanged(nameof(ShowMsxControls));
        // Usa debounce para evitar render síncrona bloqueante na UI thread
        TriggerRenderDebounced(delayMs: 0);
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnSelectedIsothermModeChanged(IsothermMode value)
    {
        OnPropertyChanged(nameof(ShowIsothermIntervalControls));
        OnPropertyChanged(nameof(ShowHumidityControls));
        OnPropertyChanged(nameof(ShowInsulationControls));
    }

    partial void OnAutoScaleEnabledChanged(bool value)
    {
        if (_loadingThermogram) return;
        TriggerRenderDebounced(delayMs: 0);
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnLevelMinCChanged(double value)
    {
        _temperatureLut = null;
        if (_loadingThermogram || AutoScaleEnabled) return;
        if (value >= LevelMaxC)
        {
            LevelMaxC = value + 0.1;
        }
            TriggerRenderDebounced();
    }

    partial void OnLevelMaxCChanged(double value)
    {
        _temperatureLut = null;
        if (_loadingThermogram || AutoScaleEnabled) return;
        if (value <= LevelMinC)
        {
            LevelMinC = value - 0.1;
        }
            TriggerRenderDebounced();
    }

    partial void OnSelectedPaletteChanged(ThermalPalette value)
    {
        var normalized = NormalizeSupportedPalette(value);
        if (normalized != value)
        {
            SelectedPalette = normalized;
            return;
        }
        if (_loadingThermogram) return;
            TriggerRenderDebounced(delayMs: 0);
    }

    partial void OnBlendFactorChanged(double value)
    {
        if (_loadingThermogram) return;
            TriggerRenderDebounced();
    }

    partial void OnPipScaleChanged(double value)
    {
        if (_loadingThermogram) return;
            TriggerRenderDebounced();
    }

    partial void OnMsxStrengthChanged(double value)
    {
        if (_loadingThermogram) return;
            TriggerRenderDebounced();
    }

    partial void OnActiveToolChanged(AnalysisTool value)
    {
        StatusMessage = value switch
        {
            AnalysisTool.Spot => "Ferramenta Spot ativa.",
            AnalysisTool.Area => "Ferramenta Retangulo (ilustracao) ativa.",
            AnalysisTool.Line => "Ferramenta Seta (ilustracao) ativa.",
            AnalysisTool.Circle => "Ferramenta Circulo (ilustracao) ativa.",
            AnalysisTool.IllustrationText => "Ferramenta Texto (ilustracao) ativa.",
            AnalysisTool.AutoAdjustRegion => "Ferramenta Auto-adjust region ativa.",
            _ => "Pronto."
        };
    }

        /// <summary>
        /// Agenda um re-render com debounce para evitar re-renderizações excessivas
        /// durante arrasto de sliders. <paramref name="delayMs"/>=0 renderiza no próximo
        /// ciclo do dispatcher sem Delay.
        /// </summary>
        private void TriggerRenderDebounced(int delayMs = 70)
        {
            _renderDebounceCts?.Cancel();
            _renderDebounceCts = new CancellationTokenSource();
            var token = _renderDebounceCts.Token;

            if (delayMs <= 0)
            {
                // Renderiza no próximo ciclo do dispatcher (sem Task.Run)
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            UpdateDisplayImage();
                        }
                    }));
                return;
            }

            // Para debounce, usa DispatcherTimer (mais leve que Task.Run)
            var timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(delayMs),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) =>
                {
                    ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                    if (!token.IsCancellationRequested)
                    {
                        UpdateDisplayImage();
                        _ = PersistSelectedThermogramViewStateAsync();
                    }
                },
                System.Windows.Application.Current.Dispatcher);
            timer.Start();
        }

    private static void LogToFile(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var logLine = $"[{timestamp}] {message}\n";
            File.AppendAllText(_debugLogPath, logLine);
        }
        catch { /* Fail silently */ }
    }
}
