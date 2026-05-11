using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using ThermixStudio.Core;

namespace ThermixStudio.App.ViewModels;

public enum AnalysisTool
{
    None,
    Hand,
    Spot,
    Area,
    Line,
    Circle,
    AutoAdjustRegion
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
    private readonly IAppDataService _dataService;
    private readonly IThermalAnalysisService _thermalService;
    private readonly IThermalRenderEngine _renderEngine;
    private readonly IReportService _reportService;
    private readonly IServiceProvider _serviceProvider;

    private ThermalImageData? _loadedImage;
    private bool _loadingThermogram;
    
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
        ThermalPalette.Grayscale
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
        AnalysisTool.Circle
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
    public IAsyncRelayCommand GenerateReportCommand { get; }
    public IAsyncRelayCommand SaveThermogramPropertiesCommand { get; }
    public IAsyncRelayCommand ToggleViewModeCommand { get; }
    public IAsyncRelayCommand UndoLastActionCommand { get; }
    public IAsyncRelayCommand RemoveSelectedMeasurementCommand { get; }
    public IAsyncRelayCommand RemoveSelectedThermogramCommand { get; }
    public IAsyncRelayCommand DeleteSelectionCommand { get; }
    public IAsyncRelayCommand AutoScaleCommand { get; }

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

    public MainViewModel(
        IAppDataService dataService,
        IThermalAnalysisService thermalService,
        IThermalRenderEngine renderEngine,
        IReportService reportService,
        IServiceProvider serviceProvider)
    {
        _dataService = dataService;
        _thermalService = thermalService;
        _renderEngine = renderEngine;
        _reportService = reportService;
        _serviceProvider = serviceProvider;

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
        GenerateReportCommand = new AsyncRelayCommand(GenerateReportAsync);
        SaveThermogramPropertiesCommand = new AsyncRelayCommand(SaveThermogramPropertiesAsync);
        ToggleViewModeCommand = new AsyncRelayCommand(ToggleViewModeAsync);
        UndoLastActionCommand = new AsyncRelayCommand(UndoLastActionAsync);
        RemoveSelectedMeasurementCommand = new AsyncRelayCommand(RemoveSelectedMeasurementAsync);
        RemoveSelectedThermogramCommand = new AsyncRelayCommand(RemoveSelectedThermogramAsync);
        DeleteSelectionCommand = new AsyncRelayCommand(DeleteSelectionAsync);
        AutoScaleCommand = new AsyncRelayCommand(ApplyAutoScaleAsync);
    }

    partial void OnImageViewModeChanged(ImageViewMode value)
    {
        var msg = $"[MODE] OnImageViewModeChanged => {value}, _loadedImage={(_loadedImage != null ? $"{_loadedImage.Width}x{_loadedImage.Height}" : "NULL")}";
        Debug.WriteLine(msg);
        LogToFile(msg);
        StatusMessage = msg;
        OnPropertyChanged(nameof(ViewModeLabel));
        OnPropertyChanged(nameof(ShowBlendControls));
        OnPropertyChanged(nameof(ShowPipControls));
        OnPropertyChanged(nameof(ShowMsxControls));
        UpdateDisplayImage();
    }

    partial void OnSelectedIsothermModeChanged(IsothermMode value)
    {
        OnPropertyChanged(nameof(ShowIsothermIntervalControls));
        OnPropertyChanged(nameof(ShowHumidityControls));
        OnPropertyChanged(nameof(ShowInsulationControls));
    }

    partial void OnAutoScaleEnabledChanged(bool value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnLevelMinCChanged(double value)
    {
        if (_loadingThermogram || AutoScaleEnabled)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnLevelMaxCChanged(double value)
    {
        if (_loadingThermogram || AutoScaleEnabled)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnSelectedPaletteChanged(ThermalPalette value)
    {
        var normalized = NormalizeSupportedPalette(value);
        if (normalized != value)
        {
            SelectedPalette = normalized;
            return;
        }

        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnBlendFactorChanged(double value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnPipScaleChanged(double value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnMsxStrengthChanged(double value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
    }

    partial void OnActiveToolChanged(AnalysisTool value)
    {
        StatusMessage = value switch
        {
            AnalysisTool.Spot => "Ferramenta Spot ativa.",
            AnalysisTool.Area => "Ferramenta Area de atencao ativa.",
            AnalysisTool.Line => "Ferramenta Linha ativa.",
            AnalysisTool.Circle => "Ferramenta Circulo ativa.",
            AnalysisTool.AutoAdjustRegion => "Ferramenta Auto-adjust region ativa.",
            _ => "Pronto."
        };
    }

    partial void OnSelectedThermogramChanged(Thermogram? value)
    {
        if (value is null)
        {
            CurrentImagePath = null;
            DisplayImage = null;
            PairedVisibleImagePath = null;
            Measurements.Clear();
            ThermogramEquipmentTag = string.Empty;
            ThermogramEquipmentDescription = string.Empty;
            ThermogramEquipmentLocation = string.Empty;
            ThermogramNotes = string.Empty;
            ThermogramCriticality = EquipmentCriticality.Medium;
            MaxAdmissibleC = null;
            CurrentScaleLabel = "Escala: -";
            return;
        }

        ThermogramEquipmentTag = value.EquipmentTag;
        ThermogramEquipmentDescription = value.EquipmentDescription;
        ThermogramEquipmentLocation = value.EquipmentLocation;
        ThermogramNotes = value.Notes;
        ThermogramCriticality = value.Criticality;

        var ext = Path.GetExtension(value.FilePath).ToLowerInvariant();
        CurrentImagePath = ext == ".csv" ? null : value.FilePath;
        ImageViewMode = ImageViewMode.Thermal;

        var processingState = ExtractProcessingState(value.ProcessingJson);
        PairedVisibleImagePath = NormalizeVisibleImagePath(
            processingState.VisibleImagePath ?? ExtractVisibleImagePath(value.MetadataJson),
            value.FilePath);

        if (string.IsNullOrWhiteSpace(PairedVisibleImagePath))
        {
            PairedVisibleImagePath = NormalizeVisibleImagePath(AutoDetectVisiblePairPath(value.FilePath), value.FilePath);
        }

        _ = LoadThermogramDataAsync(value);
    }

    private void ConfigurePlot()
    {
        TrendPlotModel.TextColor = OxyColor.FromRgb(200, 200, 200);
        TrendPlotModel.PlotAreaBorderColor = OxyColor.FromRgb(80, 80, 100);

        TrendPlotModel.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "dd/MM",
            Title = "Data",
            AxislineColor = OxyColor.FromRgb(80, 80, 100),
            TextColor = OxyColor.FromRgb(180, 180, 200),
            TitleColor = OxyColor.FromRgb(180, 180, 200)
        });

        TrendPlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Tmax (oC)",
            AxislineColor = OxyColor.FromRgb(80, 80, 100),
            TextColor = OxyColor.FromRgb(180, 180, 200),
            TitleColor = OxyColor.FromRgb(180, 180, 200)
        });
    }

    private async Task LoadDataAsync()
    {
        Thermograms.Clear();
        foreach (var t in await _dataService.GetAllThermogramsAsync())
        {
            Thermograms.Add(t);
        }

        SelectedThermogram ??= Thermograms.FirstOrDefault();
        StatusMessage = $"Banco carregado - {Thermograms.Count} termograma(s).";
    }

    private async Task LoadThermogramDataAsync(Thermogram thermogram)
    {
        _loadingThermogram = true;

        Measurements.Clear();
        if (!File.Exists(thermogram.FilePath))
        {
            StatusMessage = $"Arquivo nao encontrado: {thermogram.FilePath}";
            _loadedImage = null;
            DisplayImage = null;
            RefreshTrendPlot([]);
            _loadingThermogram = false;
            return;
        }

        try
        {
            _loadedImage = await _thermalService.LoadImageAsync(thermogram.FilePath);
        }
        catch
        {
            _loadedImage = null;
        }

        var processing = ExtractProcessingState(thermogram.ProcessingJson);
        Emissivity = processing.Emissivity;
        SelectedPalette = NormalizeSupportedPalette(processing.Palette);
        AutoScaleEnabled = processing.AutoScale;
        MsxStrength = Math.Clamp(processing.MsxStrength, 0.0, 1.0);
        MaxAdmissibleC = processing.MaxAdmissibleC;

        if (_loadedImage is not null)
        {
            var (min, max) = GetImageRange(_loadedImage);
            LevelMinC = processing.LevelMinC ?? min;
            LevelMaxC = processing.LevelMaxC ?? max;
            if (!string.IsNullOrWhiteSpace(_loadedImage.Metadata.VisibleImagePath))
            {
                PairedVisibleImagePath = NormalizeVisibleImagePath(_loadedImage.Metadata.VisibleImagePath, thermogram.FilePath);
            }

            if (TryInferCapturePresentation(_loadedImage, out var inferredMode, out var inferredPalette))
            {
                SelectedPalette = inferredPalette;
                ImageViewMode = inferredMode;
            }
        }

        UpdateDisplayImage();
        OnPropertyChanged(nameof(ImagePixelWidth));
        OnPropertyChanged(nameof(ImagePixelHeight));

        foreach (var m in await _dataService.GetMeasurementsByThermogramAsync(thermogram.Id))
        {
            Measurements.Add(m);
        }

        var trend = await _dataService.GetThermogramTrendAsync(thermogram.Id);
        RefreshTrendPlot(trend);

        _loadingThermogram = false;
        StatusMessage = $"Termograma: {Path.GetFileName(thermogram.FilePath)} | {Measurements.Count} medicao(oes)";
    }

    private async Task OpenFileAsync()
    {
        var cameraSources = DetectConnectedCameraSources();

        var defaultThermogramFolder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Termogramas");
        defaultThermogramFolder = Path.GetFullPath(defaultThermogramFolder);

        var initialDirectory = Directory.Exists(defaultThermogramFolder)
            ? defaultThermogramFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (cameraSources.Count > 0)
        {
            var camera = cameraSources[0];
            initialDirectory = camera.RootPath;

            var promptResult = System.Windows.MessageBox.Show(
                $"Camera detectada em {camera.DisplayName}.\n\n" +
                "Sim: importar automaticamente todos os termogramas da camera.\n" +
                "Nao: abrir seletor manual de arquivos/pastas.\n" +
                "Cancelar: abortar.",
                "Importacao de termogramas",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            if (promptResult == System.Windows.MessageBoxResult.Cancel)
            {
                return;
            }

            if (promptResult == System.Windows.MessageBoxResult.Yes)
            {
                var filesFromCamera = EnumerateSupportedThermogramFiles(camera.RootPath).ToList();
                if (filesFromCamera.Count == 0)
                {
                    StatusMessage = $"Camera detectada, mas nenhum termograma suportado foi encontrado em {camera.DisplayName}.";
                    return;
                }

                await ImportFilesAsync(filesFromCamera, camera.DisplayName);
                return;
            }
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Termogramas|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.csv|Todos os arquivos|*.*",
            Title = "Abrir termograma",
            Multiselect = true,
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportFilesAsync(dialog.FileNames, "selecao manual");
    }

    private async Task ImportFilesAsync(IEnumerable<string> files, string sourceLabel)
    {
        var imported = 0;
        var skipped = 0;
        var errors = 0;
        var existing = new HashSet<string>(Thermograms.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        var libraryRoot = EnsureManagedLibraryRoot();

        foreach (var sourcePath in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(sourcePath))
            {
                skipped++;
                continue;
            }

            var filePath = ResolveManagedCopyPath(sourcePath, libraryRoot);

            if (existing.Contains(filePath))
            {
                skipped++;
                continue;
            }

            try
            {
                if (!File.Exists(filePath))
                {
                    File.Copy(sourcePath, filePath, overwrite: false);
                }
            }
            catch
            {
                errors++;
                continue;
            }

            ThermalImageData? imageData = null;
            try
            {
                imageData = await _thermalService.LoadImageAsync(filePath);
            }
            catch
            {
                // Continue with metadata fallback.
            }

            var defaultState = BuildDefaultProcessingState(imageData);

            var thermogram = new Thermogram
            {
                InspectionId = SelectedInspection?.Id,
                FilePath = filePath,
                CaptureAtUtc = DateTime.UtcNow,
                CameraModel = imageData?.Metadata.CameraModel ?? "Unknown",
                MetadataJson = imageData is not null ? JsonSerializer.Serialize(imageData.Metadata) : "{}",
                ProcessingJson = JsonSerializer.Serialize(defaultState),
                Status = "imported"
            };

            var saved = await _dataService.AddThermogramAsync(thermogram);
            Thermograms.Insert(0, saved);
            existing.Add(filePath);
            imported++;
            SelectedThermogram = saved;
        }

        StatusMessage = $"Importacao ({sourceLabel}) concluida: {imported} arquivo(s), {skipped} ignorado(s), {errors} erro(s).";
    }

    private async Task ToggleViewModeAsync()
    {
        if (_loadedImage is null)
        {
            StatusMessage = "Selecione um termograma para alternar o modo de visualizacao.";
            return;
        }

        var modes = ImageViewModes;
        var currentIndex = 0;
        for (var i = 0; i < modes.Count; i++)
        {
            if (modes[i] != ImageViewMode)
            {
                continue;
            }

            currentIndex = i;
            break;
        }
        var nextIndex = (currentIndex + 1) % modes.Count;
        ImageViewMode = modes[nextIndex];

        var requiresVisible = ImageViewMode is ImageViewMode.Visible or ImageViewMode.Blending or ImageViewMode.PiP or ImageViewMode.Msx;
        var hasVisible = !string.IsNullOrWhiteSpace(PairedVisibleImagePath) && File.Exists(PairedVisibleImagePath);

        StatusMessage = requiresVisible && !hasVisible
            ? $"{GetViewModeDisplay(ImageViewMode)} selecionado sem imagem visivel pareada; exibindo termico."
            : $"Modo {GetViewModeDisplay(ImageViewMode)} ativado.";

        await Task.CompletedTask;
    }

    private bool TryInferCapturePresentation(ThermalImageData image, out ImageViewMode inferredMode, out ThermalPalette inferredPalette)
    {
        inferredMode = ImageViewMode.Thermal;
        inferredPalette = SelectedPalette;

        if (!TryLoadOriginalCameraBgraPixels(image.Width, image.Height, out var originalPixels) || originalPixels is null)
        {
            return false;
        }

        var hasVisible = TryLoadVisibleBgraPixels(image.Width, image.Height, out var visiblePixels) && visiblePixels is not null;

        var paletteCandidates = PaletteOptions.Count > 0
            ? PaletteOptions
            : new[] { ThermalPalette.Iron, ThermalPalette.Rainbow, ThermalPalette.Grayscale };

        var bestScore = double.MaxValue;
        var found = false;
        var bestMode = ImageViewMode.Thermal;
        var bestPalette = SelectedPalette;

        foreach (var palette in paletteCandidates)
        {
            var render = _renderEngine.Render(image, new ThermalRenderParameters
            {
                AutoScale = AutoScaleEnabled,
                LevelMinC = LevelMinC,
                LevelMaxC = LevelMaxC,
                Palette = palette
            });

            var thermalPixels = render.BgraPixels;
            EvaluateCandidate(ImageViewMode.Thermal, palette, thermalPixels);

            if (!hasVisible)
            {
                continue;
            }

            EvaluateCandidate(ImageViewMode.Visible, palette, visiblePixels!);
            EvaluateCandidate(ImageViewMode.Blending, palette, ComposeBlend(thermalPixels, visiblePixels!, Math.Clamp(BlendFactor, 0.0, 1.0)));
            EvaluateCandidate(ImageViewMode.PiP, palette, ComposePiP(thermalPixels, visiblePixels!, image.Width, image.Height, Math.Clamp(PipScale, 0.1, 0.8)));
            EvaluateCandidate(ImageViewMode.Msx, palette, ComposeMsx(thermalPixels, visiblePixels!, image.Width, image.Height, Math.Clamp(MsxStrength, 0.0, 1.0)));
        }

        void EvaluateCandidate(ImageViewMode mode, ThermalPalette palette, byte[] candidatePixels)
        {
            var score = CalculateBgraDistance(originalPixels, candidatePixels);
            if (score >= bestScore)
            {
                return;
            }

            bestScore = score;
            bestMode = mode;
            bestPalette = palette;
            found = true;
        }

        if (found)
        {
            inferredMode = bestMode;
            inferredPalette = bestPalette;
        }

        return found;
    }

    private static double CalculateBgraDistance(byte[] left, byte[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length < 4)
        {
            return double.MaxValue;
        }

        long sum = 0;
        var pixels = 0;

        for (var i = 0; i <= length - 4; i += 4)
        {
            sum += Math.Abs(left[i] - right[i]);
            sum += Math.Abs(left[i + 1] - right[i + 1]);
            sum += Math.Abs(left[i + 2] - right[i + 2]);
            pixels++;
        }

        if (pixels == 0)
        {
            return double.MaxValue;
        }

        return sum / (pixels * 3.0);
    }

    private async Task UndoLastActionAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para desfazer.";
            return;
        }

        var latest = Measurements.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (latest is null)
        {
            StatusMessage = "Nao ha acoes para desfazer neste termograma.";
            return;
        }

        var removed = await _dataService.RemoveMeasurementAsync(latest.Id);
        if (!removed)
        {
            StatusMessage = "Nao foi possivel desfazer a ultima acao.";
            return;
        }

        Measurements.Remove(latest);
        MeasurementRemoved?.Invoke(latest.Id);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = "Ultima acao desfeita (Ctrl+Z).";
    }

    private async Task RemoveSelectedMeasurementAsync()
    {
        if (SelectedThermogram is null || SelectedMeasurement is null)
        {
            StatusMessage = "Selecione uma medicao para remover.";
            return;
        }

        var measurementId = SelectedMeasurement.Id;
        var removed = await _dataService.RemoveMeasurementAsync(measurementId);
        if (!removed)
        {
            StatusMessage = "Nao foi possivel remover a medicao selecionada.";
            return;
        }

        Measurements.Remove(SelectedMeasurement);
        SelectedMeasurement = null;
        MeasurementRemoved?.Invoke(measurementId);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = "Medicao removida.";
    }

    private async Task RemoveSelectedThermogramAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para remover do programa.";
            return;
        }

        var thermogram = SelectedThermogram;
        var removed = await _dataService.RemoveThermogramAsync(thermogram.Id);
        if (!removed)
        {
            StatusMessage = "Nao foi possivel remover o termograma selecionado.";
            return;
        }

        Thermograms.Remove(thermogram);
        if (ReferenceEquals(SelectedThermogram, thermogram))
        {
            SelectedThermogram = Thermograms.FirstOrDefault();
        }

        if (SelectedThermogram is null)
        {
            Measurements.Clear();
            DisplayImage = null;
            _loadedImage = null;
            CurrentScaleLabel = "Escala: -";
        }

        try
        {
            var libraryRoot = EnsureManagedLibraryRoot();
            var fullLibraryRoot = Path.GetFullPath(libraryRoot);
            var fullFilePath = Path.GetFullPath(thermogram.FilePath);

            if (fullFilePath.StartsWith(fullLibraryRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullFilePath))
            {
                File.Delete(fullFilePath);
            }
        }
        catch
        {
            // Keep deletion best-effort; database removal already succeeded.
        }

        StatusMessage = $"Termograma removido do programa: {Path.GetFileName(thermogram.FilePath)} (arquivo original preservado).";
    }

    public async Task RemoveThermogramByReferenceAsync(Thermogram? thermogram)
    {
        if (thermogram is null)
        {
            return;
        }

        SelectedThermogram = thermogram;
        await RemoveSelectedThermogramAsync();
    }

    private async Task DeleteSelectionAsync()
    {
        if (SelectedMeasurement is not null)
        {
            await RemoveSelectedMeasurementAsync();
            return;
        }

        if (SelectedThermogram is not null)
        {
            await RemoveSelectedThermogramAsync();
            return;
        }

        StatusMessage = "Nada selecionado para remover.";
    }

    private async Task ApplyAutoScaleAsync()
    {
        if (_loadedImage is null)
        {
            return;
        }

        var (min, max) = GetImageRange(_loadedImage);
        AutoScaleEnabled = true;
        LevelMinC = min;
        LevelMaxC = max;
        UpdateDisplayImage();
        await Task.CompletedTask;
    }

    private void UpdateDisplayImage()
    {
        var msg1 = $"[MODE] UpdateDisplayImage => mode={ImageViewMode}, loadedImage={(_loadedImage != null ? $"{_loadedImage.Width}x{_loadedImage.Height}" : "NULL")}";
        Debug.WriteLine(msg1);
        LogToFile(msg1);

        if (_loadedImage is null)
        {
            Debug.WriteLine("[MODE] _loadedImage is null");
            DisplayImage = null;
            CurrentScaleLabel = "Escala: -";
            return;
        }

        var result = _renderEngine.Render(_loadedImage, new ThermalRenderParameters
        {
            AutoScale = AutoScaleEnabled,
            LevelMinC = LevelMinC,
            LevelMaxC = LevelMaxC,
            Palette = SelectedPalette
        });

        if (AutoScaleEnabled)
        {
            if (_autoAdjustRegion.HasValue)
            {
                var regionRange = GetRegionRange(_loadedImage, _autoAdjustRegion.Value);
                LevelMinC = regionRange.min;
                LevelMaxC = regionRange.max;
                result = _renderEngine.Render(_loadedImage, new ThermalRenderParameters
                {
                    AutoScale = false,
                    LevelMinC = LevelMinC,
                    LevelMaxC = LevelMaxC,
                    Palette = SelectedPalette
                });
            }
            else
            {
                LevelMinC = result.AppliedMinC;
                LevelMaxC = result.AppliedMaxC;
            }
        }

        var thermalPixels = result.BgraPixels;
        var width = result.Width;
        var height = result.Height;

        var hasOriginal = TryLoadOriginalCameraBgraPixels(width, height, out var originalPixels);
        var hasVisible = TryLoadVisibleBgraPixels(width, height, out var visiblePixels);

        var msg2 = $"[MODE] hasOriginal={hasOriginal}, hasVisible={hasVisible}, PairedVisibleImagePath={PairedVisibleImagePath ?? "NULL"}";
        Debug.WriteLine(msg2);
        LogToFile(msg2);

        if (!hasVisible && !string.IsNullOrWhiteSpace(CurrentImagePath))
        {
            var resolvedVisiblePath = NormalizeVisibleImagePath(
                AutoDetectVisiblePairPath(CurrentImagePath),
                CurrentImagePath);

            var msg_detect = $"[VISIBLE_DETECT] AutoDetectVisiblePairPath returned: {(resolvedVisiblePath ?? "NULL")} | CurrentImagePath={CurrentImagePath ?? "NULL"}";
            Debug.WriteLine(msg_detect);
            LogToFile(msg_detect);

            if (!string.IsNullOrWhiteSpace(resolvedVisiblePath))
            {
                PairedVisibleImagePath = resolvedVisiblePath;
                var msg_set = $"[VISIBLE_DETECT] PairedVisibleImagePath SET to: {resolvedVisiblePath}";
                Debug.WriteLine(msg_set);
                LogToFile(msg_set);
                hasVisible = TryLoadVisibleBgraPixels(width, height, out visiblePixels);
            }
        }

        var modeRequiresVisible = ImageViewMode is ImageViewMode.Visible or ImageViewMode.Blending or ImageViewMode.PiP or ImageViewMode.Msx;
        if (modeRequiresVisible && !hasVisible && TryEnsureVisiblePairOnDemand())
        {
            var msg_ensure = $"[VISIBLE_DETECT] TryEnsureVisiblePairOnDemand SUCCESS => PairedVisibleImagePath={PairedVisibleImagePath}";
            Debug.WriteLine(msg_ensure);
            LogToFile(msg_ensure);
            hasVisible = TryLoadVisibleBgraPixels(width, height, out visiblePixels);
        }

        if (ImageViewMode == ImageViewMode.Original && hasOriginal)
        {
            var msg = "[MODE] => Original branch taken";
            Debug.WriteLine(msg);
            LogToFile(msg);
            DisplayImage = null;
            DisplayImage = BuildBitmap(new ThermalRenderResult
            {
                Width = width, Height = height, BgraPixels = originalPixels!,
                AppliedMinC = result.AppliedMinC, AppliedMaxC = result.AppliedMaxC
            });
            CurrentScaleLabel = "Escala: original da camera";
            return;
        }

        if (ImageViewMode == ImageViewMode.Visible && hasVisible)
        {
            var msg = "[MODE] => Visible branch taken";
            Debug.WriteLine(msg);
            LogToFile(msg);
            DisplayImage = null;
            if (TryLoadImageSource(PairedVisibleImagePath, out var visibleSource) && visibleSource is not null)
            {
                DisplayImage = visibleSource;
            }
            else
            {
                DisplayImage = BuildBitmap(new ThermalRenderResult
                {
                    Width = width, Height = height, BgraPixels = visiblePixels!,
                    AppliedMinC = result.AppliedMinC, AppliedMaxC = result.AppliedMaxC
                });
            }
            CurrentScaleLabel = "Escala: camera digital";
            return;
        }

        var thermalIvPixels = ComposeThermalIv(thermalPixels, width, height);
        byte[] finalPixels = ImageViewMode == ImageViewMode.Thermal ? thermalIvPixels : thermalPixels;
        if (hasVisible)
        {
            finalPixels = ImageViewMode switch
            {
                ImageViewMode.Fusion => ComposeFusion(thermalPixels, visiblePixels!, _loadedImage, IsothermThresholdC, IsothermUpperThresholdC),
                ImageViewMode.Blending => ComposeBlend(thermalPixels, visiblePixels!, Math.Clamp(BlendFactor, 0.0, 1.0)),
                ImageViewMode.PiP => ComposePiP(thermalPixels, visiblePixels!, width, height, Math.Clamp(PipScale, 0.1, 0.8)),
                ImageViewMode.Msx => ComposeMsx(thermalIvPixels, visiblePixels!, width, height, Math.Clamp(MsxStrength, 0.0, 1.0)),
                _ => finalPixels
            };
        }

        Debug.WriteLine($"[MODE] => Final branch | mode={ImageViewMode} | hasVisible={hasVisible}");
        LogToFile($"[MODE] => Final branch | mode={ImageViewMode} | hasVisible={hasVisible}");
        CurrentScaleLabel = $"Escala: {result.AppliedMinC:F1} C - {result.AppliedMaxC:F1} C";
        DisplayImage = null;
        DisplayImage = BuildBitmap(new ThermalRenderResult
        {
            Width = width,
            Height = height,
            BgraPixels = finalPixels,
            AppliedMinC = result.AppliedMinC,
            AppliedMaxC = result.AppliedMaxC
        });
    }

    private static ImageSource BuildBitmap(ThermalRenderResult render)
    {
        var wb = new WriteableBitmap(render.Width, render.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, render.Width, render.Height), render.BgraPixels, render.Width * 4, 0);
        wb.Freeze();
        return wb;
    }
    
    private static void LogToFile(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var logLine = $"[{timestamp}] {message}\n";
            File.AppendAllText(_debugLogPath, logLine);
        }
        catch { /* Fail silently if logging fails */ }
    }

    private void RefreshTrendPlot(IReadOnlyList<TrendPoint> points)
    {
        TrendPlotModel.Series.Clear();

        if (points.Count > 0)
        {
            var series = new LineSeries
            {
                Title = "Tmax",
                Color = OxyColor.FromRgb(255, 102, 0),
                MarkerType = MarkerType.Circle,
                MarkerSize = 4
            };
            foreach (var p in points)
            {
                series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(p.DateUtc), p.Temperature));
            }
            TrendPlotModel.Series.Add(series);
        }

        TrendPlotModel.InvalidatePlot(true);
    }

    private async Task ActivateSpotToolAsync()
    {
        ActiveTool = AnalysisTool.Spot;
        StatusMessage = "Ferramenta Spot ativa. Clique na imagem para marcar.";
        await Task.CompletedTask;
    }

    private async Task ActivateAreaToolAsync()
    {
        ActiveTool = AnalysisTool.Area;
        StatusMessage = "Ferramenta Area de atencao ativa. Clique e arraste para destacar a regiao de interesse.";
        await Task.CompletedTask;
    }

    private async Task ActivateLineToolAsync()
    {
        ActiveTool = AnalysisTool.Line;
        StatusMessage = "Ferramenta Linha ativa. Clique e arraste no canvas.";
        await Task.CompletedTask;
    }

    private async Task ActivateCircleToolAsync()
    {
        ActiveTool = AnalysisTool.Circle;
        StatusMessage = "Ferramenta Circulo ativa. Clique e arraste para definir a area circular.";
        await Task.CompletedTask;
    }

    private async Task ActivateAutoAdjustRegionToolAsync()
    {
        ActiveTool = AnalysisTool.AutoAdjustRegion;
        StatusMessage = "Auto-adjust region ativa. Clique e arraste para definir a regiao de ajuste automatico.";
        await Task.CompletedTask;
    }

    private async Task ClearAutoAdjustRegionAsync()
    {
        _autoAdjustRegion = null;
        UpdateDisplayImage();
        StatusMessage = "Regiao de auto-ajuste removida.";
        await Task.CompletedTask;
    }

    private async Task ApplyHumidityPresetAsync()
    {
        SelectedIsothermMode = IsothermMode.Humidity;
        HumidityRelativeLimit = 70;
        StatusMessage = "Preset de isoterma de umidade aplicado (RH limite 70%).";
        await Task.CompletedTask;
    }

    private async Task ApplyInsulationPresetAsync()
    {
        SelectedIsothermMode = IsothermMode.Insulation;
        InsulationIndoorC = 22;
        InsulationOutdoorC = 12;
        InsulationThermalIndex = 0.70;
        StatusMessage = "Preset de isoterma de insulação aplicado (22/12 C, índice 0.70).";
        await Task.CompletedTask;
    }

    public async Task<ThermalMeasurement?> AddSpotAtNormalizedAsync(double normalizedX, double normalizedY)
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            return null;
        }

        var x = Math.Clamp((int)Math.Round(normalizedX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y = Math.Clamp((int)Math.Round(normalizedY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var temperature = _thermalService.GetTemperatureAt(_loadedImage, x, y);

        var measurement = new ThermalMeasurement
        {
            ThermogramId = SelectedThermogram.Id,
            Type = MeasurementType.Spot,
            Tmin = temperature,
            Tmax = temperature,
            Tavg = temperature,
            CoordinatesJson = JsonSerializer.Serialize(new { x, y }),
            Notes = "Spot manual no canvas"
        };

        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Spot em ({x},{y}): {temperature:F1} oC";
        return measurement;
    }

    public async Task<ThermalMeasurement?> AddAreaAtNormalizedAsync(double startX, double startY, double endX, double endY)
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            return null;
        }

        var x1 = Math.Clamp((int)Math.Round(startX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y1 = Math.Clamp((int)Math.Round(startY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var x2 = Math.Clamp((int)Math.Round(endX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y2 = Math.Clamp((int)Math.Round(endY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);

        var x = Math.Min(x1, x2);
        var y = Math.Min(y1, y2);
        var rw = Math.Max(2, Math.Abs(x2 - x1));
        var rh = Math.Max(2, Math.Abs(y2 - y1));

        var measurement = new ThermalMeasurement
        {
            ThermogramId = SelectedThermogram.Id,
            Type = MeasurementType.Area,
            Tmin = 0,
            Tmax = 0,
            Tavg = 0,
            DeltaT = 0,
            CoordinatesJson = JsonSerializer.Serialize(new { x, y, rw, rh }),
            Notes = "Area de atencao"
        };

        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Area de atencao ({rw}x{rh}) criada.";
        return measurement;
    }

    public async Task<ThermalMeasurement?> AddLineAtNormalizedAsync(double startY, double endY)
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            return null;
        }

        var lineY = Math.Clamp((int)Math.Round(((startY + endY) / 2.0) * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var profile = _thermalService.GetHorizontalLineProfile(_loadedImage, lineY);

        var measurement = new ThermalMeasurement
        {
            ThermogramId = SelectedThermogram.Id,
            Type = MeasurementType.Line,
            Tmin = profile.Statistics.Tmin,
            Tmax = profile.Statistics.Tmax,
            Tavg = profile.Statistics.Tavg,
            DeltaT = profile.Statistics.DeltaT,
            CoordinatesJson = JsonSerializer.Serialize(new { y = lineY, profile = profile.Temperatures.Take(250).ToArray() }),
            Notes = "Linha horizontal manual no canvas"
        };

        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Linha: Tmax {profile.Statistics.Tmax:F1} oC";
        return measurement;
    }

    public async Task<ThermalMeasurement?> AddCircleAtNormalizedAsync(double startX, double startY, double endX, double endY)
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            return null;
        }

        var x1 = Math.Clamp((int)Math.Round(startX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y1 = Math.Clamp((int)Math.Round(startY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);
        var x2 = Math.Clamp((int)Math.Round(endX * (_loadedImage.Width - 1)), 0, _loadedImage.Width - 1);
        var y2 = Math.Clamp((int)Math.Round(endY * (_loadedImage.Height - 1)), 0, _loadedImage.Height - 1);

        var cx = (x1 + x2) / 2;
        var cy = (y1 + y2) / 2;
        var radius = Math.Max(2, (int)Math.Round(Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)) / 2.0));

        var stats = GetCircleStatistics(_loadedImage, cx, cy, radius);

        var measurement = new ThermalMeasurement
        {
            ThermogramId = SelectedThermogram.Id,
            Type = MeasurementType.Circle,
            Tmin = stats.Tmin,
            Tmax = stats.Tmax,
            Tavg = stats.Tavg,
            DeltaT = stats.DeltaT,
            CoordinatesJson = JsonSerializer.Serialize(new { cx, cy, radius }),
            Notes = "Circulo manual no canvas"
        };

        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Circulo (r={radius}) Tmax {stats.Tmax:F1} oC";
        return measurement;
    }

    public Task SetAutoAdjustRegionNormalizedAsync(double startX, double startY, double endX, double endY)
    {
        _autoAdjustRegion = (startX, startY, endX, endY);
        UpdateDisplayImage();
        StatusMessage = "Regiao de auto-ajuste aplicada.";
        return Task.CompletedTask;
    }

    private async Task AddDifferenceAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para calcular diferenca.";
            return;
        }

        var latestTwoSpots = Measurements
            .Where(x => x.Type == MeasurementType.Spot)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(2)
            .ToList();

        if (latestTwoSpots.Count < 2)
        {
            StatusMessage = "Para diferenca, adicione pelo menos dois spots.";
            return;
        }

        var a = latestTwoSpots[0];
        var b = latestTwoSpots[1];
        var delta = Math.Abs(a.Tmax - b.Tmax);

        var measurement = new ThermalMeasurement
        {
            ThermogramId = SelectedThermogram.Id,
            Type = MeasurementType.Difference,
            Tmin = Math.Min(a.Tmax, b.Tmax),
            Tmax = Math.Max(a.Tmax, b.Tmax),
            Tavg = (a.Tmax + b.Tmax) / 2.0,
            DeltaT = delta,
            CoordinatesJson = JsonSerializer.Serialize(new { a = a.Id, b = b.Id }),
            Notes = "Diferenca entre dois spots"
        };

        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Diferenca calculada: DeltaT {delta:F1} oC";
    }

    private async Task ExportImageCsvAsync()
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para exportar CSV da imagem.";
            return;
        }

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "Reports");
        Directory.CreateDirectory(outputDirectory);
        var fileName = $"{Path.GetFileNameWithoutExtension(SelectedThermogram.FilePath)}_matrix_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var outputPath = Path.Combine(outputDirectory, fileName);

        using (var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
        {
            for (var y = 0; y < _loadedImage.Height; y++)
            {
                var row = new string[_loadedImage.Width];
                for (var x = 0; x < _loadedImage.Width; x++)
                {
                    row[x] = _loadedImage.Temperatures[y, x].ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                }

                await writer.WriteLineAsync(string.Join(';', row));
            }
        }

        StatusMessage = $"CSV da imagem exportado: {fileName}";
    }

    private async Task ExportMeasurementsCsvAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para exportar CSV de medicoes.";
            return;
        }

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "Reports");
        Directory.CreateDirectory(outputDirectory);
        var fileName = $"{Path.GetFileNameWithoutExtension(SelectedThermogram.FilePath)}_measurements_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var outputPath = Path.Combine(outputDirectory, fileName);

        using (var writer = new StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
        {
            await writer.WriteLineAsync("Type;Tmin;Tmax;Tavg;DeltaT;CreatedAtUtc;Notes");
            foreach (var measurement in Measurements.OrderBy(x => x.CreatedAtUtc))
            {
                var line = string.Join(';',
                    measurement.Type,
                    measurement.Tmin.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    measurement.Tmax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    measurement.Tavg.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    measurement.DeltaT.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    measurement.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    (measurement.Notes ?? string.Empty).Replace(';', ',').Replace('\r', ' ').Replace('\n', ' '));

                await writer.WriteLineAsync(line);
            }
        }

        StatusMessage = $"CSV de medicoes exportado: {fileName}";
    }

    private async Task AddIsothermAsync()
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            StatusMessage = "Abra um termograma antes de criar isoterma.";
            return;
        }

        var stats = GetIsothermStatisticsByMode(_loadedImage, SelectedIsothermMode);
        if (stats.Tmax <= 0)
        {
            StatusMessage = "Nenhum pixel corresponde aos criterios da isoterma configurada.";
            return;
        }

        var measurement = new ThermalMeasurement
        {
            ThermogramId = SelectedThermogram.Id,
            Type = MeasurementType.Isotherm,
            Tmin = stats.Tmin,
            Tmax = stats.Tmax,
            Tavg = stats.Tavg,
            DeltaT = stats.DeltaT,
            CoordinatesJson = JsonSerializer.Serialize(new
            {
                mode = SelectedIsothermMode,
                lowerC = IsothermThresholdC,
                upperC = IsothermUpperThresholdC,
                humidityRelativeLimit = HumidityRelativeLimit,
                insulationIndoorC = InsulationIndoorC,
                insulationOutdoorC = InsulationOutdoorC,
                insulationThermalIndex = InsulationThermalIndex
            }),
            Notes = BuildIsothermNote()
        };

        await _dataService.AddMeasurementAsync(measurement);
        Measurements.Insert(0, measurement);
        RefreshTrendPlot(await _dataService.GetThermogramTrendAsync(SelectedThermogram.Id));
        StatusMessage = $"Isoterma {SelectedIsothermMode}: Tmax {stats.Tmax:F1} oC";
    }

    private async Task SaveThermogramPropertiesAsync()
    {
        if (SelectedThermogram is null)
        {
            return;
        }

        SyncEditableFieldsToSelectedThermogram();

        PersistCurrentStateToSelectedThermogram();

        await _dataService.UpdateThermogramAsync(SelectedThermogram);
        StatusMessage = $"Propriedades e estado termico salvos: {Path.GetFileName(SelectedThermogram.FilePath)}";
    }

    private async Task GenerateReportAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para gerar o relatorio.";
            return;
        }

        SyncEditableFieldsToSelectedThermogram();
        PersistCurrentStateToSelectedThermogram();
        await _dataService.UpdateThermogramAsync(SelectedThermogram);

        var editor = _serviceProvider.GetRequiredService<ReportEditorWindow>();
        await editor.ViewModel.LoadAsync(
            SelectedInspection ?? new Inspection
            {
                OsNumber = "-",
                TechnicianName = "N/A",
                StartAtUtc = DateTime.UtcNow
            },
            Thermograms,
            SelectedThermogram);

        editor.Owner = Application.Current?.MainWindow;
        if (editor.ShowDialog() == true)
        {
            StatusMessage = "Relatorio gerado e editor fechado.";
        }
    }

    private void SyncEditableFieldsToSelectedThermogram()
    {
        if (SelectedThermogram is null)
        {
            return;
        }

        SelectedThermogram.EquipmentTag = ThermogramEquipmentTag;
        SelectedThermogram.EquipmentDescription = ThermogramEquipmentDescription;
        SelectedThermogram.EquipmentLocation = ThermogramEquipmentLocation;
        SelectedThermogram.Criticality = ThermogramCriticality;
        SelectedThermogram.Notes = ThermogramNotes;
        SelectedThermogram.InspectionId = SelectedInspection?.Id;
    }

    private void PersistCurrentStateToSelectedThermogram()
    {
        if (SelectedThermogram is null)
        {
            return;
        }

        SelectedThermogram.ProcessingJson = JsonSerializer.Serialize(new ThermalProcessingState
        {
            AutoScale = AutoScaleEnabled,
            LevelMinC = LevelMinC,
            LevelMaxC = LevelMaxC,
            MaxAdmissibleC = MaxAdmissibleC,
            Emissivity = Emissivity,
            Palette = NormalizeSupportedPalette(SelectedPalette),
            MsxStrength = Math.Clamp(MsxStrength, 0.0, 1.0),
            VisibleImagePath = PairedVisibleImagePath
        });

        SelectedThermogram.MetadataJson = SaveVisibleImagePath(SelectedThermogram.MetadataJson, PairedVisibleImagePath);
    }

    private static ThermalProcessingState BuildDefaultProcessingState(ThermalImageData? imageData)
    {
        if (imageData is null)
        {
            return new ThermalProcessingState();
        }

        var (min, max) = GetImageRange(imageData);
        return new ThermalProcessingState
        {
            AutoScale = true,
            LevelMinC = min,
            LevelMaxC = max,
            MaxAdmissibleC = null,
            Emissivity = imageData.Metadata.Emissivity ?? 0.95,
            Palette = MapPaletteFromMetadata(imageData.Metadata.PaletteName),
            MsxStrength = 0.10,
            VisibleImagePath = imageData.Metadata.VisibleImagePath
        };
    }

    private static ThermalPalette MapPaletteFromMetadata(string? paletteName)
    {
        if (string.IsNullOrWhiteSpace(paletteName))
        {
            return ThermalPalette.Iron;
        }

        var name = paletteName.Trim();
        if (name.Contains("rainbow", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("arco", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Rainbow;
        }

        if (name.Contains("gray", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("grey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cinza", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Grayscale;
        }

        // O programa e o fluxo de calibração atual suportam apenas Iron/Arco-Iris/Cinzento.
        // Qualquer paleta fora desse conjunto é normalizada para Iron.

        return ThermalPalette.Iron;
    }

    private static ThermalPalette NormalizeSupportedPalette(ThermalPalette palette)
        => palette switch
        {
            ThermalPalette.Iron => ThermalPalette.Iron,
            ThermalPalette.Rainbow => ThermalPalette.Rainbow,
            ThermalPalette.Grayscale => ThermalPalette.Grayscale,
            _ => ThermalPalette.Iron
        };

    private static (double min, double max) GetImageRange(ThermalImageData image)
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
        {
            return (0, 1);
        }

        return (min, max);
    }

    private static (double min, double max) GetRegionRange(ThermalImageData image, (double startX, double startY, double endX, double endY) region)
    {
        var x1 = Math.Clamp((int)Math.Round(Math.Min(region.startX, region.endX) * (image.Width - 1)), 0, image.Width - 1);
        var y1 = Math.Clamp((int)Math.Round(Math.Min(region.startY, region.endY) * (image.Height - 1)), 0, image.Height - 1);
        var x2 = Math.Clamp((int)Math.Round(Math.Max(region.startX, region.endX) * (image.Width - 1)), 0, image.Width - 1);
        var y2 = Math.Clamp((int)Math.Round(Math.Max(region.startY, region.endY) * (image.Height - 1)), 0, image.Height - 1);

        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = y1; y <= y2; y++)
        {
            for (var x = x1; x <= x2; x++)
            {
                var value = image.Temperatures[y, x];
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
        {
            return GetImageRange(image);
        }

        return (min, max);
    }

    private static ThermalStatistics GetCircleStatistics(ThermalImageData image, int centerX, int centerY, int radius)
    {
        var radiusSquared = radius * radius;
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;

        var startX = Math.Max(0, centerX - radius);
        var endX = Math.Min(image.Width - 1, centerX + radius);
        var startY = Math.Max(0, centerY - radius);
        var endY = Math.Min(image.Height - 1, centerY + radius);

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if ((dx * dx) + (dy * dy) > radiusSquared)
                {
                    continue;
                }

                var value = image.Temperatures[y, x];
                if (value < min) min = value;
                if (value > max) max = value;
                sum += value;
                count++;
            }
        }

        if (count == 0)
        {
            return new ThermalStatistics();
        }

        return new ThermalStatistics
        {
            Tmin = min,
            Tmax = max,
            Tavg = sum / count
        };
    }

    private static ThermalProcessingState ExtractProcessingState(string processingJson)
    {
        if (string.IsNullOrWhiteSpace(processingJson))
        {
            return new ThermalProcessingState();
        }

        try
        {
            return JsonSerializer.Deserialize<ThermalProcessingState>(processingJson) ?? new ThermalProcessingState();
        }
        catch
        {
            return new ThermalProcessingState();
        }
    }

    private static string? ExtractVisibleImagePath(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(metadataJson);
            var path = node?["VisibleImagePath"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static string SaveVisibleImagePath(string metadataJson, string? visiblePath)
    {
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(metadataJson) as JsonObject ?? [];
        }
        catch
        {
            payload = [];
        }

        payload["VisibleImagePath"] = visiblePath;
        return payload.ToJsonString();
    }

    private static string? AutoDetectVisiblePairPath(string thermalPath)
    {
        if (string.IsNullOrWhiteSpace(thermalPath) || !File.Exists(thermalPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(thermalPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(thermalPath);
        var canonicalBaseName = TrimTrailingNumericSuffix(baseName);
        var ext = Path.GetExtension(thermalPath);
        var baseCandidates = new[] { baseName, canonicalBaseName }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidateNames = baseCandidates.SelectMany(name => new[]
        {
            $"{name}_visivel",
            $"{name}_visivel_original",
            $"{name}_visible",
            $"{name}_visible_original",
            name.Replace("_IR", "_VIS", StringComparison.OrdinalIgnoreCase),
            name.Replace("_T", "_V", StringComparison.OrdinalIgnoreCase),
            name.Replace("IR", "VIS", StringComparison.OrdinalIgnoreCase),
            name.Replace("Thermal", "Visible", StringComparison.OrdinalIgnoreCase)
        });

        foreach (var candidateName in candidateNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(directory, candidateName + ext);
            if (File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            var matchedFile = baseCandidates
                .SelectMany(name => Directory.EnumerateFiles(directory, $"{name}*{ext}", SearchOption.TopDirectoryOnly))
                .FirstOrDefault(path =>
                {
                    var candidateName = Path.GetFileNameWithoutExtension(path);
                    return candidateName.Contains("visivel", StringComparison.OrdinalIgnoreCase)
                        || candidateName.Contains("visible", StringComparison.OrdinalIgnoreCase);
                });

            if (!string.IsNullOrWhiteSpace(matchedFile))
            {
                return matchedFile;
            }

            var visibleCacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThermixStudio",
                "visible-cache");

            if (Directory.Exists(visibleCacheDirectory))
            {
                var cacheExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
                foreach (var name in baseCandidates)
                {
                    foreach (var extension in cacheExtensions)
                    {
                        var cacheCandidates = new[]
                        {
                            Path.Combine(visibleCacheDirectory, $"{name}_visible{extension}"),
                            Path.Combine(visibleCacheDirectory, $"{name}_visible_original{extension}"),
                            Path.Combine(visibleCacheDirectory, $"{name}_visivel{extension}"),
                            Path.Combine(visibleCacheDirectory, $"{name}_visivel_original{extension}")
                        };

                        foreach (var candidate in cacheCandidates)
                        {
                            if (File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string TrimTrailingNumericSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var index = value.Length - 1;
        while (index >= 0 && char.IsDigit(value[index]))
        {
            index--;
        }

        if (index > 0 && index < value.Length - 1 && value[index] == '_')
        {
            return value[..index];
        }

        return value;
    }

    private static bool HasOriginalCameraImage(string? imagePath)
        => !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

    private static string? NormalizeVisibleImagePath(string? candidatePath, string? thermalPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(thermalPath))
        {
            try
            {
                var candidateFullPath = Path.GetFullPath(candidatePath);
                var thermalFullPath = Path.GetFullPath(thermalPath);
                if (string.Equals(candidateFullPath, thermalFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            catch
            {
                if (string.Equals(candidatePath, thermalPath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }

        return candidatePath;
    }

    private bool TryLoadOriginalCameraBgraPixels(int width, int height, out byte[]? pixels)
        => TryLoadImageBgraPixels(CurrentImagePath, width, height, out pixels);

    private bool TryLoadVisibleBgraPixels(int width, int height, out byte[]? pixels)
        => TryLoadImageBgraPixels(PairedVisibleImagePath, width, height, out pixels);

    private bool TryEnsureVisiblePairOnDemand()
    {
        var msg_entry = $"[VISIBLE_DETECT] TryEnsureVisiblePairOnDemand() called | CurrentImagePath={CurrentImagePath}";
        Debug.WriteLine(msg_entry);
        LogToFile(msg_entry);

        if (string.IsNullOrWhiteSpace(CurrentImagePath) || !File.Exists(CurrentImagePath))
        {
            var msg_fail1 = "[VISIBLE_DETECT] CurrentImagePath is null/whitespace or file doesn't exist";
            Debug.WriteLine(msg_fail1);
            LogToFile(msg_fail1);
            return false;
        }

        var detectedPath = NormalizeVisibleImagePath(AutoDetectVisiblePairPath(CurrentImagePath), CurrentImagePath);
        if (!string.IsNullOrWhiteSpace(detectedPath))
        {
            var msg_auto = $"[VISIBLE_DETECT] AutoDetect found path: {detectedPath}";
            Debug.WriteLine(msg_auto);
            LogToFile(msg_auto);
            PairedVisibleImagePath = detectedPath;
            return true;
        }

        var msg_fail = "[VISIBLE_DETECT] No visible pair found by native detection";
        Debug.WriteLine(msg_fail);
        LogToFile(msg_fail);

        var msg_service = "[VISIBLE_DETECT] Native detection failed; trying service extraction reload";
        Debug.WriteLine(msg_service);
        LogToFile(msg_service);

        try
        {
            var refreshed = _thermalService.LoadImageAsync(CurrentImagePath).GetAwaiter().GetResult();
            var refreshedPath = NormalizeVisibleImagePath(refreshed.Metadata.VisibleImagePath, CurrentImagePath);

            if (!string.IsNullOrWhiteSpace(refreshedPath))
            {
                PairedVisibleImagePath = refreshedPath;
                var msg_service_ok = $"[VISIBLE_DETECT] Service extraction resolved visible path: {refreshedPath}";
                Debug.WriteLine(msg_service_ok);
                LogToFile(msg_service_ok);
                return true;
            }

            var msg_service_empty = "[VISIBLE_DETECT] Service extraction returned no visible path";
            Debug.WriteLine(msg_service_empty);
            LogToFile(msg_service_empty);
        }
        catch (Exception ex)
        {
            var msg_service_fail = $"[VISIBLE_DETECT] Service extraction reload failed: {ex.Message}";
            Debug.WriteLine(msg_service_fail);
            LogToFile(msg_service_fail);
        }

        return false;
    }

    private static bool TryLoadImageSource(string? imagePath, out ImageSource? imageSource)
    {
        imageSource = null;

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            imageSource = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLoadImageBgraPixels(string? imagePath, int width, int height, out byte[]? pixels)
    {
        pixels = null;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            BitmapSource source = bitmap;
            if (source.PixelWidth != width || source.PixelHeight != height)
            {
                source = new TransformedBitmap(source, new ScaleTransform(
                    width / (double)source.PixelWidth,
                    height / (double)source.PixelHeight));
            }

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var buffer = new byte[width * height * 4];
            formatted.CopyPixels(buffer, width * 4, 0);
            pixels = buffer;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ComposeBlend(byte[] thermal, byte[] visible, double alpha)
    {
        var output = new byte[thermal.Length];
        var invAlpha = 1.0 - alpha;

        for (var i = 0; i < thermal.Length; i += 4)
        {
            output[i] = (byte)Math.Clamp((thermal[i] * alpha) + (visible[i] * invAlpha), 0, 255);
            output[i + 1] = (byte)Math.Clamp((thermal[i + 1] * alpha) + (visible[i + 1] * invAlpha), 0, 255);
            output[i + 2] = (byte)Math.Clamp((thermal[i + 2] * alpha) + (visible[i + 2] * invAlpha), 0, 255);
            output[i + 3] = 255;
        }

        return output;
    }

    private static byte[] ComposeFusion(byte[] thermal, byte[] visible, ThermalImageData image, double lowerLimitC, double upperLimitC)
    {
        var output = new byte[thermal.Length];
        var lower = Math.Min(lowerLimitC, upperLimitC);
        var upper = Math.Max(lowerLimitC, upperLimitC);

        var idx = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                var useThermal = t >= lower && t <= upper;

                output[idx] = useThermal ? thermal[idx] : visible[idx];
                output[idx + 1] = useThermal ? thermal[idx + 1] : visible[idx + 1];
                output[idx + 2] = useThermal ? thermal[idx + 2] : visible[idx + 2];
                output[idx + 3] = 255;
                idx += 4;
            }
        }

        return output;
    }

    private static byte[] ComposePiP(byte[] thermal, byte[] visible, int width, int height, double scale)
    {
        var output = new byte[visible.Length];
        Buffer.BlockCopy(visible, 0, output, 0, visible.Length);

        var pipWidth = Math.Clamp((int)(width * scale), 40, width);
        var pipHeight = Math.Clamp((int)(height * scale), 40, height);
        var startX = (width - pipWidth) / 2;
        var startY = (height - pipHeight) / 2;

        for (var y = startY; y < startY + pipHeight; y++)
        {
            for (var x = startX; x < startX + pipWidth; x++)
            {
                var idx = ((y * width) + x) * 4;
                output[idx] = thermal[idx];
                output[idx + 1] = thermal[idx + 1];
                output[idx + 2] = thermal[idx + 2];
                output[idx + 3] = 255;
            }
        }

        return output;
    }

    // Thermal IV profile: preserve thermal hotspots while reducing thin structural artifacts
    // that can appear when source JPG already carries embedded contour-like traces.
    private static byte[] ComposeThermalIv(byte[] thermal, int width, int height)
    {
        var output = new byte[thermal.Length];
        Buffer.BlockCopy(thermal, 0, output, 0, thermal.Length);

        if (width < 3 || height < 3)
        {
            return output;
        }

        var smooth = new byte[thermal.Length];
        Buffer.BlockCopy(thermal, 0, smooth, 0, thermal.Length);

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = ((y * width) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var p00 = thermal[(((y - 1) * width) + (x - 1)) * 4 + c];
                    var p01 = thermal[(((y - 1) * width) + x) * 4 + c];
                    var p02 = thermal[(((y - 1) * width) + (x + 1)) * 4 + c];
                    var p10 = thermal[((y * width) + (x - 1)) * 4 + c];
                    var p11 = thermal[idx + c];
                    var p12 = thermal[((y * width) + (x + 1)) * 4 + c];
                    var p20 = thermal[(((y + 1) * width) + (x - 1)) * 4 + c];
                    var p21 = thermal[(((y + 1) * width) + x) * 4 + c];
                    var p22 = thermal[(((y + 1) * width) + (x + 1)) * 4 + c];

                    smooth[idx + c] = (byte)((p00 + (2 * p01) + p02 +
                                              (2 * p10) + (4 * p11) + (2 * p12) +
                                              p20 + (2 * p21) + p22) / 16);
                }

                smooth[idx + 3] = 255;
            }
        }

        // Adaptive blend: stronger cleanup on thin high-frequency transitions.
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = ((y * width) + x) * 4;
                var lumOrig = (thermal[idx] + thermal[idx + 1] + thermal[idx + 2]) / 3.0;
                var lumSmooth = (smooth[idx] + smooth[idx + 1] + smooth[idx + 2]) / 3.0;
                var delta = Math.Abs(lumOrig - lumSmooth);

                var blend = Math.Clamp(delta / 42.0, 0.0, 1.0) * 0.55;
                var invBlend = 1.0 - blend;

                output[idx] = (byte)Math.Clamp((thermal[idx] * invBlend) + (smooth[idx] * blend), 0, 255);
                output[idx + 1] = (byte)Math.Clamp((thermal[idx + 1] * invBlend) + (smooth[idx + 1] * blend), 0, 255);
                output[idx + 2] = (byte)Math.Clamp((thermal[idx + 2] * invBlend) + (smooth[idx + 2] * blend), 0, 255);
                output[idx + 3] = 255;
            }
        }

        return output;
    }

    // FLIR MSX (Multi-Spectral Dynamic Imaging):
    // Extract clean structural edges from the visible image and overlay them
    // subtly on top of thermal colors to emulate FLIR-style contour detail.
    private static byte[] ComposeMsx(byte[] thermal, byte[] visible, int width, int height, double intensity)
    {
        var output = new byte[thermal.Length];
        Buffer.BlockCopy(thermal, 0, output, 0, thermal.Length);
        var msxIntensity = Math.Clamp(intensity, 0.0, 1.0);

        var pixelCount = width * height;
        var luminance = new int[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var idx = i * 4;
            luminance[i] = (visible[idx] + visible[idx + 1] + visible[idx + 2]) / 3;
        }

        var smooth = new int[pixelCount];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var p00 = luminance[((y - 1) * width) + (x - 1)];
                var p01 = luminance[((y - 1) * width) + x];
                var p02 = luminance[((y - 1) * width) + (x + 1)];
                var p10 = luminance[(y * width) + (x - 1)];
                var p11 = luminance[(y * width) + x];
                var p12 = luminance[(y * width) + (x + 1)];
                var p20 = luminance[((y + 1) * width) + (x - 1)];
                var p21 = luminance[((y + 1) * width) + x];
                var p22 = luminance[((y + 1) * width) + (x + 1)];

                smooth[(y * width) + x] =
                    (p00 + (2 * p01) + p02 +
                     (2 * p10) + (4 * p11) + (2 * p12) +
                     p20 + (2 * p21) + p22) / 16;
            }
        }

        var edgeEnergy = new double[pixelCount];
        var energySum = 0.0;
        var samples = 0;

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var gx = -Smooth(y - 1, x - 1) + Smooth(y - 1, x + 1)
                         - (2 * Smooth(y, x - 1)) + (2 * Smooth(y, x + 1))
                         - Smooth(y + 1, x - 1) + Smooth(y + 1, x + 1);

                var gy = -Smooth(y - 1, x - 1) - (2 * Smooth(y - 1, x)) - Smooth(y - 1, x + 1)
                         + Smooth(y + 1, x - 1) + (2 * Smooth(y + 1, x)) + Smooth(y + 1, x + 1);

                var gradient = Math.Sqrt((gx * gx) + (gy * gy));
                var pixelIndex = (y * width) + x;
                edgeEnergy[pixelIndex] = gradient;
                energySum += gradient;
                samples++;
            }
        }

        var meanEnergy = samples > 0 ? energySum / samples : 0.0;
        var threshold = Math.Clamp(meanEnergy * (2.10 - (msxIntensity * 0.55)), 18.0, 110.0);

        // Keep edge density under control to avoid streaky/polluted overlays.
        for (var pass = 0; pass < 5; pass++)
        {
            var strongEdges = 0;
            for (var i = 0; i < edgeEnergy.Length; i++)
            {
                if (edgeEnergy[i] >= threshold)
                {
                    strongEdges++;
                }
            }

            var density = samples > 0 ? strongEdges / (double)samples : 0.0;
            if (density <= 0.08)
            {
                break;
            }

            threshold *= 1.18;
        }

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var pixelIndex = (y * width) + x;
                var gradient = edgeEnergy[pixelIndex];
                if (gradient < threshold)
                {
                    continue;
                }

                var normalized = (gradient - threshold) / Math.Max(1.0, 220.0 - threshold);
                var strength = Math.Clamp(normalized, 0.0, 1.0);
                var inkBase = 0.08 + (msxIntensity * 0.16);
                var inkRange = 0.14 + (msxIntensity * 0.22);
                var ink = inkBase + (strength * inkRange);
                var idx = pixelIndex * 4;

                output[idx] = (byte)Math.Clamp(output[idx] * (1.0 - ink), 0, 255);
                output[idx + 1] = (byte)Math.Clamp(output[idx + 1] * (1.0 - ink), 0, 255);
                output[idx + 2] = (byte)Math.Clamp(output[idx + 2] * (1.0 - ink), 0, 255);
            }
        }

        return output;

        int Smooth(int py, int px)
        {
            return smooth[(py * width) + px];
        }
    }

    private ThermalStatistics GetIsothermStatisticsByMode(ThermalImageData image, IsothermMode mode)
    {
        var lower = Math.Min(IsothermThresholdC, IsothermUpperThresholdC);
        var upper = Math.Max(IsothermThresholdC, IsothermUpperThresholdC);

        var ambient = image.Metadata.AmbientTemperatureC ?? (lower + upper) / 2.0;
        var humidityRiskTemp = ambient - ((100.0 - Math.Clamp(HumidityRelativeLimit, 1, 100)) / 5.0);
        var insulationRiskTemp = InsulationOutdoorC + (Math.Clamp(InsulationThermalIndex, 0.0, 1.0) * (InsulationIndoorC - InsulationOutdoorC));

        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        var count = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var t = image.Temperatures[y, x];
                var include = mode switch
                {
                    IsothermMode.Above => t >= IsothermThresholdC,
                    IsothermMode.Below => t <= IsothermThresholdC,
                    IsothermMode.Interval => t >= lower && t <= upper,
                    IsothermMode.Humidity => t <= humidityRiskTemp,
                    IsothermMode.Insulation => t <= insulationRiskTemp,
                    IsothermMode.Custom => t >= lower && t <= upper,
                    _ => false
                };

                if (!include)
                {
                    continue;
                }

                if (t < min) min = t;
                if (t > max) max = t;
                sum += t;
                count++;
            }
        }

        if (count == 0)
        {
            return new ThermalStatistics();
        }

        return new ThermalStatistics
        {
            Tmin = min,
            Tmax = max,
            Tavg = sum / count
        };
    }

    private string BuildIsothermNote()
    {
        return SelectedIsothermMode switch
        {
            IsothermMode.Above => $"Isoterma Above >= {IsothermThresholdC:F1} oC",
            IsothermMode.Below => $"Isoterma Below <= {IsothermThresholdC:F1} oC",
            IsothermMode.Interval => $"Isoterma Interval {Math.Min(IsothermThresholdC, IsothermUpperThresholdC):F1}..{Math.Max(IsothermThresholdC, IsothermUpperThresholdC):F1} oC",
            IsothermMode.Humidity => $"Isoterma Humidity (RH limite {HumidityRelativeLimit:F0}%)",
            IsothermMode.Insulation => $"Isoterma Insulation (Ti {InsulationIndoorC:F1} / To {InsulationOutdoorC:F1} / TI {InsulationThermalIndex:F2})",
            IsothermMode.Custom => $"Isoterma Custom {Math.Min(IsothermThresholdC, IsothermUpperThresholdC):F1}..{Math.Max(IsothermThresholdC, IsothermUpperThresholdC):F1} oC",
            _ => "Isoterma"
        };
    }

    private static string GetViewModeDisplay(ImageViewMode mode)
    {
        return mode switch
        {
            ImageViewMode.Original => "Original da Camera",
            ImageViewMode.Thermal => "Termica (IV)",
            ImageViewMode.Visible => "Camera Digital",
            ImageViewMode.Fusion => "Combinacao Termica (Intervalo)",
            ImageViewMode.Blending => "Combinacao Termica",
            ImageViewMode.PiP => "Imagem na imagem",
            ImageViewMode.Msx => "MSX",
            _ => mode.ToString()
        };
    }

    private static string EnsureManagedLibraryRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ThermixStudio",
            "Library");

        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveManagedCopyPath(string sourcePath, string libraryRoot)
    {
        var currentDateFolder = DateTime.Now.ToString("yyyy-MM-dd");
        var targetFolder = Path.Combine(libraryRoot, currentDateFolder);
        Directory.CreateDirectory(targetFolder);

        var fileName = Path.GetFileName(sourcePath);
        var candidate = Path.Combine(targetFolder, fileName);

        if (string.Equals(sourcePath, candidate, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            return candidate;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (true)
        {
            var versioned = Path.Combine(targetFolder, $"{baseName}_{counter}{extension}");
            if (!File.Exists(versioned))
            {
                return versioned;
            }

            counter++;
        }
    }

    private static IReadOnlyList<CameraImportSource> DetectConnectedCameraSources()
    {
        var sources = new List<CameraImportSource>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var isRemovable = drive.DriveType == DriveType.Removable;
            var hasFlirLabel = drive.VolumeLabel.Contains("FLIR", StringComparison.OrdinalIgnoreCase);

            if (!isRemovable && !hasFlirLabel)
            {
                continue;
            }

            if (!IsLikelyCameraStorage(drive.RootDirectory.FullName, hasFlirLabel))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.Name
                : $"{drive.VolumeLabel} ({drive.Name})";

            sources.Add(new CameraImportSource(drive.RootDirectory.FullName, displayName));
        }

        return sources;
    }

    private static bool IsLikelyCameraStorage(string rootPath, bool hasFlirLabel)
    {
        if (hasFlirLabel)
        {
            return true;
        }

        try
        {
            var dcimPath = Path.Combine(rootPath, "DCIM");
            if (Directory.Exists(dcimPath))
            {
                return true;
            }

            var firstLevelDirs = Directory.EnumerateDirectories(rootPath).Take(20);
            foreach (var dir in firstLevelDirs)
            {
                var name = Path.GetFileName(dir);
                if (name.Contains("FLIR", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("THERM", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSupportedThermogramFiles(string rootPath)
    {
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".csv"
        };

        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (allowedExtensions.Contains(extension))
                {
                    yield return file;
                }
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private sealed record CameraImportSource(string RootPath, string DisplayName);
}
