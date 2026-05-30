using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly IServiceProvider _serviceProvider;

    private ThermalImageData? _loadedImage;
    private bool _loadingThermogram;
    private global::ThermixStudio.Core.ImageViewMode? _metadataDetectedMode;
    private global::ThermixStudio.Core.ImageViewMode? _inferredCaptureMode;
    
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
        IServiceProvider serviceProvider)
    {
        _dataService = dataService;
        _thermalService = thermalService;
        _viewPipeline = viewPipeline;
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
        ExportIdenticalJpgCommand = new AsyncRelayCommand(ExportIdenticalJpgAsync);
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
        Debug.WriteLine($"[MODE] OnImageViewModeChanged => {value}");
        LogToFile($"[MODE] OnImageViewModeChanged => {value}");
        StatusMessage = $"Modo: {GetViewModeDisplay(value)}";
        OnPropertyChanged(nameof(ViewModeLabel));
        OnPropertyChanged(nameof(ShowBlendControls));
        OnPropertyChanged(nameof(ShowPipControls));
        OnPropertyChanged(nameof(ShowMsxControls));
        UpdateDisplayImage();
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
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnLevelMinCChanged(double value)
    {
        if (_loadingThermogram || AutoScaleEnabled)
        {
            return;
        }

        UpdateDisplayImage();
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnLevelMaxCChanged(double value)
    {
        if (_loadingThermogram || AutoScaleEnabled)
        {
            return;
        }

        UpdateDisplayImage();
        _ = PersistSelectedThermogramViewStateAsync();
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
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnBlendFactorChanged(double value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnPipScaleChanged(double value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
        _ = PersistSelectedThermogramViewStateAsync();
    }

    partial void OnMsxStrengthChanged(double value)
    {
        if (_loadingThermogram)
        {
            return;
        }

        UpdateDisplayImage();
        _ = PersistSelectedThermogramViewStateAsync();
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

    partial void OnSelectedThermogramChanged(Thermogram? value)
    {
        if (value is null)
        {
            CurrentImagePath = null;
            DisplayImage = null;
            _metadataDetectedMode = null;
            PairedVisibleImagePath = null;
            Measurements.Clear();
            Illustrations.Clear();
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
        Illustrations.Clear();
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

        await _viewPipeline.PrepareThermogramAsync(thermogram.FilePath);

        var processing = ExtractProcessingState(thermogram.ProcessingJson);
        var shouldPersistProcessingMetadataUpdate = false;
        _inferredCaptureMode = null;
        _metadataDetectedMode = _loadedImage?.Metadata.DetectedViewMode;
        if (!_metadataDetectedMode.HasValue)
        {
            _metadataDetectedMode = await DetectOriginalCaptureModeAsync(thermogram.FilePath);
        }

        if (processing.MetadataDetectedMode != _metadataDetectedMode)
        {
            // Self-heal legacy persisted states that were inferred visually and saved as metadata.
            shouldPersistProcessingMetadataUpdate = true;
        }

        Emissivity = processing.Emissivity;
        SelectedPalette = NormalizeSupportedPalette(processing.Palette);
        AutoScaleEnabled = processing.AutoScale;
        BlendFactor = Math.Clamp(processing.BlendFactor, 0.0, 1.0);
        PipScale = Math.Clamp(processing.PipScale, 0.10, 1.0);
        MsxStrength = Math.Clamp(processing.MsxStrength, 0.0, 1.0);
        MaxAdmissibleC = processing.MaxAdmissibleC;
        ImageViewMode = MapFromCoreImageViewMode(processing.ViewMode);

        if (processing.ViewMode == global::ThermixStudio.Core.ImageViewMode.Thermal &&
            _metadataDetectedMode is global::ThermixStudio.Core.ImageViewMode.PiP or global::ThermixStudio.Core.ImageViewMode.Visible)
        {
            ImageViewMode = MapFromCoreImageViewMode(_metadataDetectedMode.Value);
            shouldPersistProcessingMetadataUpdate = true;
        }

        foreach (var item in processing.Illustrations)
        {
            Illustrations.Add(item);
        }

        if (_loadedImage is not null)
        {
            var (min, max) = GetPreferredThermalRange(_loadedImage);
            LevelMinC = processing.LevelMinC ?? min;
            LevelMaxC = processing.LevelMaxC ?? max;
            if (!string.IsNullOrWhiteSpace(_loadedImage.Metadata.VisibleImagePath))
            {
                PairedVisibleImagePath = NormalizeVisibleImagePath(_loadedImage.Metadata.VisibleImagePath, thermogram.FilePath);
            }

            var metadataPalette = ResolvePaletteFromMetadata(_loadedImage.Metadata);
            if (processing.Palette == ThermalPalette.Original ||
                (processing.Palette == ThermalPalette.Iron && metadataPalette != ThermalPalette.Iron))
            {
                SelectedPalette = metadataPalette;
            }

            if (string.IsNullOrWhiteSpace(PairedVisibleImagePath))
            {
                TryEnsureVisiblePairOnDemand();
            }

            var shouldDetectPalette = string.IsNullOrWhiteSpace(_loadedImage.Metadata.PaletteName) &&
                                      !_loadedImage.Metadata.DetectedPalette.HasValue;
            if (shouldDetectPalette)
            {
                try
                {
                    var detectedPalette = await _viewPipeline.DetectPaletteFromFileAsync(thermogram.FilePath);
                    if (!string.IsNullOrEmpty(detectedPalette) &&
                        Enum.TryParse<ThermalPalette>(detectedPalette, out var paletteEnum))
                    {
                        SelectedPalette = paletteEnum;
                        LogToFile($"[PALETTE_DETECT] Detected={detectedPalette}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao detectar paleta: {ex.Message}");
                }
            }

            var shouldInferCapturePresentation =
                                               (!processing.VisualInferenceInitialized ||
                                                processing.VisualInferenceRuleVersion < CurrentVisualInferenceRuleVersion) &&
                                               ShouldInferCaptureModeFromPixels(_metadataDetectedMode);
            if (shouldInferCapturePresentation)
            {
                try
                {
                    var imagePath = thermogram.FilePath;
                    var visiblePath = PairedVisibleImagePath;
                    var inferTask = Task.Run(() =>
                    {
                        var success = TryInferCapturePresentation(_loadedImage!, imagePath, visiblePath, out var mode, out var palette);
                        return (success, mode, palette);
                    });

                    var completed = await Task.WhenAny(inferTask, Task.Delay(1800));
                    if (completed == inferTask)
                    {
                        var inference = await inferTask;
                        if (inference.success)
                        {
                            _inferredCaptureMode = MapToCoreImageViewMode(inference.mode);
                            ImageViewMode = inference.mode;
                            if (inference.palette != SelectedPalette)
                            {
                                SelectedPalette = inference.palette;
                            }

                            processing.VisualInferenceInitialized = true;
                            processing.VisualInferenceRuleVersion = CurrentVisualInferenceRuleVersion;
                            shouldPersistProcessingMetadataUpdate = true;
                            LogToFile($"[MODE_INFER] Applied mode={inference.mode} palette={inference.palette}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[MODE_INFER] Timeout de inferencia visual; mantendo modo atual para evitar travamento.");
                        LogToFile("[MODE_INFER] Timeout de inferencia visual; mantendo modo atual para evitar travamento.");
                    }
                }
                catch (Exception inferEx)
                {
                    Debug.WriteLine($"[MODE_INFER] Falha na inferencia visual: {inferEx.Message}");
                    LogToFile($"[MODE_INFER] Falha na inferencia visual: {inferEx.Message}");
                }
            }
            else if (processing.VisualInferenceInitialized)
            {
                LogToFile($"[MODE_INFER] Preserving persisted mode={ImageViewMode} palette={SelectedPalette}");
            }
        }

        UpdateDisplayImage();
        OnPropertyChanged(nameof(ImagePixelWidth));
        OnPropertyChanged(nameof(ImagePixelHeight));

        if (shouldPersistProcessingMetadataUpdate)
        {
            PersistCurrentStateToSelectedThermogram();
            await _dataService.UpdateThermogramAsync(thermogram);
        }

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
                await _viewPipeline.PrepareThermogramAsync(filePath);
            }
            catch
            {
                // Continue with metadata fallback.
            }

            var defaultState = BuildDefaultProcessingState(imageData);
            if (imageData is not null)
            {
                var exifMode = await _viewPipeline.DetectCaptureModeFromMetadataAsync(filePath);
                if (exifMode is global::ThermixStudio.Core.ImageViewMode.PiP or global::ThermixStudio.Core.ImageViewMode.Visible)
                {
                    defaultState.ViewMode = exifMode.Value;
                    defaultState.MetadataDetectedMode = exifMode;
                }
            }

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

    private bool TryInferCapturePresentation(
        ThermalImageData image,
        string imagePath,
        string? visibleImagePath,
        out ImageViewMode inferredMode,
        out ThermalPalette inferredPalette)
    {
        inferredMode = ImageViewMode.Thermal;
        inferredPalette = SelectedPalette;

        if (!TryLoadImageBgraPixels(imagePath, image.Width, image.Height, out var originalPixels) || originalPixels is null)
        {
            return false;
        }

        byte[]? visiblePixels = null;
        var hasVisible = !string.IsNullOrWhiteSpace(visibleImagePath) &&
                         TryLoadImageBgraPixels(visibleImagePath, image.Width, image.Height, out visiblePixels) &&
                         visiblePixels is not null;
        var metadataPalette = ResolvePaletteFromMetadata(image.Metadata);
        var imageName = Path.GetFileName(imagePath);

        if (hasVisible)
        {
            var originalLuma = ComputeBgraLumaPlane(originalPixels, image.Width, image.Height);
            var visibleLuma = ComputeBgraLumaPlane(visiblePixels!, image.Width, image.Height);
            var thermalLuma = TryBuildRenderedThermalLumaPlane(image, metadataPalette, out var renderedThermalLuma)
                ? renderedThermalLuma!
                : ComputeTemperatureLumaPlane(image.Temperatures, image.Width, image.Height);

            var corrOriginalThermal = CalculatePearsonCorrelation(originalLuma, thermalLuma);
            var corrOriginalVisible = CalculatePearsonCorrelation(originalLuma, visibleLuma);

            var highFreqOriginal = CalculateHighFrequencyEnergy(originalLuma, image.Width, image.Height);
            var highFreqVisible = CalculateHighFrequencyEnergy(visibleLuma, image.Width, image.Height);
            var highFreqRatioVisible = highFreqOriginal / Math.Max(highFreqVisible, 1e-6);

            LogToFile($"[MODE_INFER_RULE] file={imageName} signals corrOT={corrOriginalThermal:F4} corrOV={corrOriginalVisible:F4} hfO={highFreqOriginal:F4} hfV={highFreqVisible:F4} hfRatioOV={highFreqRatioVisible:F4}");

            if (corrOriginalThermal <= -0.05 && corrOriginalVisible <= -0.05 && highFreqRatioVisible >= 2.7)
            {
                inferredMode = ImageViewMode.Msx;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=MSX if corrOT<=-0.05 && corrOV<=-0.05 && hfRatioOV>=2.7");
                return true;
            }

            // Backup MSX rule: keeps strong edge dominance behavior even when corrOT is not negative.
            if (corrOriginalVisible <= -0.08 && highFreqRatioVisible >= 2.0 && highFreqOriginal >= 3.0)
            {
                inferredMode = ImageViewMode.Msx;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=MSX if corrOV<=-0.08 && hfRatioOV>=2.0 && hfO>=3.0");
                return true;
            }

            if (corrOriginalThermal >= 0.12 && corrOriginalVisible >= 0.10)
            {
                inferredMode = ImageViewMode.Blending;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=Blending if corrOT>=0.12 && corrOV>=0.10");
                return true;
            }

            if (corrOriginalThermal >= 0.15 && corrOriginalVisible <= 0.05)
            {
                inferredMode = ImageViewMode.Thermal;
                inferredPalette = metadataPalette;
                LogToFile($"[MODE_INFER_RULE] file={imageName} hit=Thermal if corrOT>=0.15 && corrOV<=0.05");
                return true;
            }

            LogToFile($"[MODE_INFER_RULE] file={imageName} no explicit rule hit; falling back to legacy score search");
        }
        else
        {
            LogToFile($"[MODE_INFER_RULE] file={imageName} visible pair unavailable; using legacy score search");
        }

        var paletteCandidates = new List<ThermalPalette> { metadataPalette };
        if (SelectedPalette != ThermalPalette.Original && SelectedPalette != metadataPalette)
        {
            paletteCandidates.Add(SelectedPalette);
        }

        var bestScore = double.MaxValue;
        var found = false;
        var bestMode = ImageViewMode.Thermal;
        var bestPalette = SelectedPalette;
        var bestModeScore = new Dictionary<ImageViewMode, double>();
        var originalEdgeMap = ComputeLumaEdgeEnergyMap(originalPixels, image.Width, image.Height);

        var blendIntensityCandidates = new[] { 0.35, 0.45, 0.55, 0.70, 0.85 };
        var msxIntensityCandidates = new[] { 0.10, 0.18, 0.25, 0.35, 0.45, 0.60 };
        var pipScaleCandidates = new[] { 0.40, 0.50, 0.55, 0.60 };

        foreach (var palette in paletteCandidates)
        {
            byte[] thermalPixels;
            try
            {
                var paletteName = palette == ThermalPalette.Original ? "Iron" : palette.ToString();
                thermalPixels = _viewPipeline.RenderRadiometricWithPaletteAsync(
                    image, paletteName, LevelMinC, LevelMaxC).GetAwaiter().GetResult();
            }
            catch
            {
                continue;
            }

            EvaluateCandidate(ImageViewMode.Thermal, palette, thermalPixels);

            if (!hasVisible)
            {
                continue;
            }

            EvaluateCandidate(ImageViewMode.Visible, palette, visiblePixels!);

            foreach (var blendIntensity in blendIntensityCandidates)
            {
                EvaluateCandidate(
                    ImageViewMode.Blending,
                    palette,
                    _viewPipeline.ComposeViewMode(
                        global::ThermixStudio.Core.ImageViewMode.Blending,
                        thermalPixels,
                        image.Width,
                        image.Height,
                        visiblePixels!,
                        blendIntensity,
                        Math.Clamp(PipScale, 0.1, 0.8),
                        image));
            }

            foreach (var pipScale in pipScaleCandidates)
            {
                EvaluateCandidate(
                    ImageViewMode.PiP,
                    palette,
                    _viewPipeline.ComposeViewMode(
                        global::ThermixStudio.Core.ImageViewMode.PiP,
                        thermalPixels,
                        image.Width,
                        image.Height,
                        visiblePixels!,
                        Math.Clamp(BlendFactor, 0.0, 1.0),
                        pipScale,
                        image));
            }

            foreach (var msxIntensity in msxIntensityCandidates)
            {
                EvaluateCandidate(
                    ImageViewMode.Msx,
                    palette,
                    _viewPipeline.ComposeViewMode(
                        global::ThermixStudio.Core.ImageViewMode.Msx,
                        thermalPixels,
                        image.Width,
                        image.Height,
                        visiblePixels!,
                        msxIntensity,
                        Math.Clamp(PipScale, 0.1, 0.8),
                        image));
            }
        }

        void EvaluateCandidate(ImageViewMode mode, ThermalPalette palette, byte[] candidatePixels)
        {
            var colorScore = CalculateBgraDistance(originalPixels, candidatePixels);
            var edgeScore = CalculateEdgeDistance(originalEdgeMap, candidatePixels, image.Width, image.Height);
            var modePenalty = mode == ImageViewMode.Blending ? 0.35 : 0.0;
            var score = colorScore + (0.45 * edgeScore) + modePenalty;

            if (!bestModeScore.TryGetValue(mode, out var currentModeBest) || score < currentModeBest)
            {
                bestModeScore[mode] = score;
            }

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
            if (hasVisible &&
                bestModeScore.TryGetValue(ImageViewMode.Blending, out var blendingScore) &&
                bestModeScore.TryGetValue(ImageViewMode.Msx, out var msxScore) &&
                Math.Abs(msxScore - blendingScore) <= Math.Max(1.0, blendingScore * 0.06))
            {
                // Neutral tie-break: if captured frame is closer to thermal anchor than visible anchor,
                // prefer MSX; otherwise prefer blending.
                var thermalAnchor = bestModeScore.TryGetValue(ImageViewMode.Thermal, out var thermalScore)
                    ? thermalScore
                    : double.MaxValue;
                var visibleAnchor = bestModeScore.TryGetValue(ImageViewMode.Visible, out var visibleScore)
                    ? visibleScore
                    : double.MaxValue;

                bestMode = thermalAnchor <= visibleAnchor
                    ? ImageViewMode.Msx
                    : ImageViewMode.Blending;
            }

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

    private static double[] ComputeBgraLumaPlane(byte[] pixels, int width, int height)
    {
        var pixelCount = width * height;
        var luma = new double[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            var pixelOffset = i * 4;
            luma[i] = (0.114 * pixels[pixelOffset]) +
                      (0.587 * pixels[pixelOffset + 1]) +
                      (0.299 * pixels[pixelOffset + 2]);
        }

        return luma;
    }

    private static double[] ComputeTemperatureLumaPlane(double[,] temperatures, int width, int height)
    {
        var pixelCount = width * height;
        var luma = new double[pixelCount];

        var min = double.MaxValue;
        var max = double.MinValue;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = temperatures[y, x];
                if (t < min)
                {
                    min = t;
                }

                if (t > max)
                {
                    max = t;
                }
            }
        }

        var range = Math.Max(max - min, 1e-6);
        var idx = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var normalized = (temperatures[y, x] - min) / range;
                luma[idx++] = Math.Clamp(normalized * 255.0, 0.0, 255.0);
            }
        }

        return luma;
    }

    private static double CalculatePearsonCorrelation(double[] left, double[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0.0;
        }

        var leftMean = 0.0;
        var rightMean = 0.0;

        for (var i = 0; i < length; i++)
        {
            leftMean += left[i];
            rightMean += right[i];
        }

        leftMean /= length;
        rightMean /= length;

        var covariance = 0.0;
        var leftVariance = 0.0;
        var rightVariance = 0.0;

        for (var i = 0; i < length; i++)
        {
            var ld = left[i] - leftMean;
            var rd = right[i] - rightMean;
            covariance += ld * rd;
            leftVariance += ld * ld;
            rightVariance += rd * rd;
        }

        var denominator = Math.Sqrt(leftVariance * rightVariance);
        if (denominator < 1e-9)
        {
            return 0.0;
        }

        return covariance / denominator;
    }

    private static double CalculateHighFrequencyEnergy(double[] luma, int width, int height)
    {
        if (luma.Length != width * height || width < 3 || height < 3)
        {
            return 0.0;
        }

        var sum = 0.0;
        var samples = 0;

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                var smooth = (
                    luma[idx] +
                    luma[idx - 1] +
                    luma[idx + 1] +
                    luma[idx - width] +
                    luma[idx + width]) / 5.0;

                sum += Math.Abs(luma[idx] - smooth);
                samples++;
            }
        }

        return samples == 0 ? 0.0 : sum / samples;
    }

    private static double[] ComputeLumaEdgeEnergyMap(byte[] pixels, int width, int height)
    {
        var pixelCount = width * height;
        var luma = new double[pixelCount];
        var edges = new double[pixelCount];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = y * width + x;
                var i = pixelIndex * 4;
                luma[pixelIndex] = (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
            }
        }

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                var gx = luma[idx + 1] - luma[idx - 1];
                var gy = luma[idx + width] - luma[idx - width];
                edges[idx] = Math.Abs(gx) + Math.Abs(gy);
            }
        }

        return edges;
    }

    private static double CalculateEdgeDistance(double[] leftEdgeMap, byte[] rightPixels, int width, int height)
    {
        if (leftEdgeMap.Length != width * height)
        {
            return double.MaxValue;
        }

        var rightEdgeMap = ComputeLumaEdgeEnergyMap(rightPixels, width, height);
        var samples = 0;
        var sum = 0.0;

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var idx = y * width + x;
                sum += Math.Abs(leftEdgeMap[idx] - rightEdgeMap[idx]);
                samples++;
            }
        }

        if (samples == 0)
        {
            return double.MaxValue;
        }

        return sum / samples;
    }

    private async Task UndoLastActionAsync()
    {
        if (SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para desfazer.";
            return;
        }

        if (await TryUndoIllustrationActionAsync())
        {
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

        var (min, max) = GetPreferredThermalRange(_loadedImage);
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

        double appliedMin = LevelMinC;
        double appliedMax = LevelMaxC;

        if (AutoScaleEnabled)
        {
            var (autoMin, autoMax) = GetPreferredThermalRange(_loadedImage);
            appliedMin = autoMin;
            appliedMax = autoMax;
            
            if (_autoAdjustRegion.HasValue)
            {
                var regionRange = GetRegionRange(_loadedImage, _autoAdjustRegion.Value);
                appliedMin = regionRange.min;
                appliedMax = regionRange.max;
            }

            LevelMinC = appliedMin;
            LevelMaxC = appliedMax;
        }

        byte[] thermalPixels = Array.Empty<byte>();
        if (!TryRenderThermalPixelsViaPipeline(_loadedImage, SelectedPalette, appliedMin, appliedMax, out thermalPixels, out appliedMin, out appliedMax))
        {
            Debug.WriteLine($"[PALETTE_ERROR] Falha ao renderizar paleta {SelectedPalette}; tentando Grayscale.");
            LogToFile($"[PALETTE_ERROR] Falha ao renderizar paleta {SelectedPalette}; tentando Grayscale.");
            TryRenderThermalPixelsViaPipeline(_loadedImage, ThermalPalette.Grayscale, appliedMin, appliedMax, out thermalPixels, out appliedMin, out appliedMax);
        }

        var result = new ThermalRenderResult
        {
            Width = _loadedImage.Width,
            Height = _loadedImage.Height,
            BgraPixels = thermalPixels,
            AppliedMinC = appliedMin,
            AppliedMaxC = appliedMax
        };

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

        byte[] finalPixels = thermalPixels;
        if (hasVisible)
        {
            finalPixels = ImageViewMode switch
            {
                ImageViewMode.Fusion => ComposeFusion(thermalPixels, visiblePixels!, _loadedImage, IsothermThresholdC, IsothermUpperThresholdC),
                ImageViewMode.Blending => RenderComposedMode(ImageViewMode.Blending, thermalPixels, width, height, visiblePixels!),
                ImageViewMode.PiP => RenderComposedMode(ImageViewMode.PiP, thermalPixels, width, height, visiblePixels!),
                ImageViewMode.Msx => RenderComposedMode(ImageViewMode.Msx, thermalPixels, width, height, visiblePixels!),
                _ => finalPixels
            };
        }

        // Sobreposição de UI da câmera original (logo FLIR, barra de escala, crosshair,
        // caixas de temperatura). Aplica-se a TODOS os modos — a função detecta automaticamente
        // quais pixels são parte da UI usando luminância + baixa saturação.
        if (hasOriginal && originalPixels is not null)
        {
            finalPixels = _viewPipeline.OverlayCameraUI(finalPixels, originalPixels, width, height);
        }

        Debug.WriteLine($"[MODE] => Final branch | mode={ImageViewMode} | hasVisible={hasVisible} | hasOriginal={hasOriginal}");
        LogToFile($"[MODE] => Final branch | mode={ImageViewMode} | hasVisible={hasVisible} | hasOriginal={hasOriginal}");
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
        StatusMessage = "Ferramenta Retangulo (ilustracao) ativa. Clique e arraste para destacar elementos.";
        await Task.CompletedTask;
    }

    private async Task ActivateLineToolAsync()
    {
        ActiveTool = AnalysisTool.Line;
        StatusMessage = "Ferramenta Seta (ilustracao) ativa. Clique e arraste para apontar elementos.";
        await Task.CompletedTask;
    }

    private async Task ActivateCircleToolAsync()
    {
        ActiveTool = AnalysisTool.Circle;
        StatusMessage = "Ferramenta Circulo (ilustracao) ativa. Clique e arraste para destacar elementos.";
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

    private async Task ExportIdenticalJpgAsync()
    {
        if (_loadedImage is null || SelectedThermogram is null)
        {
            StatusMessage = "Selecione um termograma para exportar.";
            return;
        }

        try
        {
            var width = _loadedImage.Width;
            var height = _loadedImage.Height;

            // 1. Renderiza pixels térmicos
            double appliedMin = LevelMinC;
            double appliedMax = LevelMaxC;
            if (AutoScaleEnabled)
            {
                var (autoMin, autoMax) = GetPreferredThermalRange(_loadedImage);
                appliedMin = autoMin;
                appliedMax = autoMax;
                if (_autoAdjustRegion.HasValue)
                {
                    var regionRange = GetRegionRange(_loadedImage, _autoAdjustRegion.Value);
                    appliedMin = regionRange.min;
                    appliedMax = regionRange.max;
                }
            }

            byte[] thermalPixels;
            if (!TryRenderThermalPixelsViaPipeline(_loadedImage, SelectedPalette, appliedMin, appliedMax, out thermalPixels, out appliedMin, out appliedMax))
            {
                TryRenderThermalPixelsViaPipeline(_loadedImage, ThermalPalette.Grayscale, appliedMin, appliedMax, out thermalPixels, out appliedMin, out appliedMax);
            }

            // 2. Tenta carregar imagem visível e imagem original
            var hasOriginal = TryLoadOriginalCameraBgraPixels(width, height, out var originalPixels);
            var hasVisible = TryLoadVisibleBgraPixels(width, height, out var visiblePixels);

            byte[] finalPixels = thermalPixels;
            if (hasVisible)
            {
                finalPixels = ImageViewMode switch
                {
                    ImageViewMode.Fusion => ComposeFusion(thermalPixels, visiblePixels!, _loadedImage, IsothermThresholdC, IsothermUpperThresholdC),
                    ImageViewMode.Blending => RenderComposedMode(ImageViewMode.Blending, thermalPixels, width, height, visiblePixels!),
                    ImageViewMode.PiP => RenderComposedMode(ImageViewMode.PiP, thermalPixels, width, height, visiblePixels!),
                    ImageViewMode.Msx => RenderComposedMode(ImageViewMode.Msx, thermalPixels, width, height, visiblePixels!),
                    _ => finalPixels
                };
            }

            if (hasOriginal && originalPixels is not null)
            {
                finalPixels = _viewPipeline.OverlayCameraUI(finalPixels, originalPixels, width, height);
            }

            // 3. Salva a imagem processada com dimensões originais (ex: 320x240)
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Imagem JPEG (*.jpg)|*.jpg",
                FileName = $"{Path.GetFileNameWithoutExtension(SelectedThermogram.FilePath)}_exportado.jpg",
                Title = "Exportar Termograma com Dimensões Originais"
            };

            if (saveDialog.ShowDialog() == true)
            {
                using (var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    var rect = new Rectangle(0, 0, width, height);
                    var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        Marshal.Copy(finalPixels, 0, bmpData.Scan0, finalPixels.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(bmpData);
                    }

                    bmp.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                // 4. Copia metadados originais usando ExifTool (se disponível)
                var exifTool = _serviceProvider.GetRequiredService<IExifToolService>().FindExifTool();
                if (!string.IsNullOrEmpty(exifTool))
                {
                    var sourcePath = SelectedThermogram.FilePath;
                    var destPath = saveDialog.FileName;

                    await Task.Run(() =>
                    {
                        using var process = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = exifTool,
                                Arguments = $"-overwrite_original -TagsFromFile \"{sourcePath}\" -all:all \"{destPath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardError = true
                            }
                        };
                        process.Start();
                        var err = process.StandardError.ReadToEnd();
                        process.WaitForExit(5000);
                        if (!string.IsNullOrWhiteSpace(err))
                        {
                            Debug.WriteLine($"[EXPORT_EXIF] stderr: {err}");
                        }
                    });
                }

                StatusMessage = $"Termograma exportado com sucesso: {Path.GetFileName(saveDialog.FileName)}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Falha ao exportar termograma: {ex.Message}";
            Debug.WriteLine($"[EXPORT_ERROR] {ex}");
            LogToFile($"[EXPORT_ERROR] {ex}");
        }
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

        string? capturedCurrentViewPath = null;
        if (ReportSnapshotRequested is not null)
        {
            foreach (var callback in ReportSnapshotRequested.GetInvocationList().OfType<Func<Task<string?>>>() )
            {
                try
                {
                    capturedCurrentViewPath = await callback();
                    if (!string.IsNullOrWhiteSpace(capturedCurrentViewPath))
                    {
                        break;
                    }
                }
                catch
                {
                    // Continua sem bloquear a geração do relatório.
                }
            }
        }

        var editor = _serviceProvider.GetRequiredService<ReportEditorWindow>();
        await editor.ViewModel.LoadAsync(
            SelectedInspection ?? new Inspection
            {
                OsNumber = "-",
                TechnicianName = "N/A",
                StartAtUtc = DateTime.UtcNow
            },
            Thermograms,
            SelectedThermogram,
            capturedCurrentViewPath);

        editor.Owner = Application.Current?.MainWindow;
        editor.Show();
        editor.Activate();
        StatusMessage = "Editor de relatório aberto.";
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
            ViewMode = MapToCoreImageViewMode(ImageViewMode),
            AutoScale = AutoScaleEnabled,
            LevelMinC = LevelMinC,
            LevelMaxC = LevelMaxC,
            MaxAdmissibleC = MaxAdmissibleC,
            Emissivity = Emissivity,
            Palette = NormalizeSupportedPalette(SelectedPalette),
            BlendFactor = BlendFactor,
            PipScale = PipScale,
            MsxStrength = Math.Clamp(MsxStrength, 0.0, 1.0),
            MetadataDetectedMode = _metadataDetectedMode,
            VisualInferenceInitialized = true,
            VisualInferenceRuleVersion = CurrentVisualInferenceRuleVersion,
            VisibleImagePath = PairedVisibleImagePath,
            Illustrations = Illustrations
                .OfType<ThermalIllustration>()
                .Select(CloneIllustration)
                .ToList()
        });

        SelectedThermogram.MetadataJson = SaveVisibleImagePath(SelectedThermogram.MetadataJson, PairedVisibleImagePath);
    }

    private async Task PersistSelectedThermogramViewStateAsync()
    {
        if (_loadingThermogram || SelectedThermogram is null)
        {
            return;
        }

        try
        {
            PersistCurrentStateToSelectedThermogram();
            await _dataService.UpdateThermogramAsync(SelectedThermogram);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STATE_SAVE] Falha ao persistir estado do termograma: {ex.Message}");
            LogToFile($"[STATE_SAVE] Falha ao persistir estado do termograma: {ex.Message}");
        }
    }

    private static ThermalProcessingState BuildDefaultProcessingState(ThermalImageData? imageData)
    {
        if (imageData is null)
        {
            return new ThermalProcessingState();
        }

        var (min, max) = GetPreferredThermalRange(imageData);
        return new ThermalProcessingState
        {
            ViewMode = imageData.Metadata.DetectedViewMode ?? global::ThermixStudio.Core.ImageViewMode.Thermal,
            AutoScale = true,
            LevelMinC = min,
            LevelMaxC = max,
            MaxAdmissibleC = null,
            Emissivity = imageData.Metadata.Emissivity ?? 0.95,
            Palette = ResolvePaletteFromMetadata(imageData.Metadata),
            MsxStrength = 0.10,
            MetadataDetectedMode = imageData.Metadata.DetectedViewMode,
            VisualInferenceInitialized = false,
            VisualInferenceRuleVersion = 0,
            VisibleImagePath = imageData.Metadata.VisibleImagePath
        };
    }

    private static ThermalPalette ResolvePaletteFromMetadata(RadiometricMetadata metadata)
    {
        if (metadata.DetectedPalette.HasValue)
        {
            return NormalizeSupportedPalette(metadata.DetectedPalette.Value);
        }

        return MapPaletteFromMetadata(metadata.PaletteName);
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

        if (name.Contains("hotmetal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("metal quente", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Hotmetal;
        }

        if (name.Contains("arctic", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("artic", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ártico", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Arctic;
        }

        if (name.Contains("thermal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("térmica", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("termica", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Thermal;
        }

        if (name.Contains("jet", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Jet;
        }

        if (name.Equals("hot", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(" quente", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Hot;
        }

        if (name.Equals("cool", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("fria", StringComparison.OrdinalIgnoreCase))
        {
            return ThermalPalette.Cool;
        }

        return ThermalPalette.Iron;
    }

    private static ThermalPalette NormalizeSupportedPalette(ThermalPalette palette)
        => palette switch
        {
            ThermalPalette.Iron => ThermalPalette.Iron,
            ThermalPalette.Rainbow => ThermalPalette.Rainbow,
            ThermalPalette.Grayscale => ThermalPalette.Grayscale,
            ThermalPalette.Hotmetal => ThermalPalette.Hotmetal,
            ThermalPalette.Arctic => ThermalPalette.Arctic,
            ThermalPalette.Thermal => ThermalPalette.Thermal,
            ThermalPalette.Jet => ThermalPalette.Jet,
            ThermalPalette.Hot => ThermalPalette.Hot,
            ThermalPalette.Cool => ThermalPalette.Cool,
            ThermalPalette.Original => ThermalPalette.Original,
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

    private static (double min, double max) GetPreferredThermalRange(ThermalImageData image)
    {
        // A escala visual salva no JPEG FLIR (PaletteScaleMinC/MaxC) descreve os
        // numeros da barra, mas usar esses limites como clamp direto empurra pixels
        // demais para amarelo/branco. A distribuicao visual da camera depende do
        // range radiometrico da matriz + DDE/plateau.
        return GetImageRange(image);
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
    {
        if (TryLoadImageBgraPixels(PairedVisibleImagePath, width, height, out var rawVisiblePixels) && rawVisiblePixels is not null)
        {
            pixels = AlignVisibleToThermalFOV(rawVisiblePixels, width, height);
            return true;
        }
        pixels = null;
        return false;
    }

    private static byte[] AlignVisibleToThermalFOV(byte[] inputPixels, int width, int height)
    {
        // Parâmetros extraídos da FLIR E8 (Real2IR e Offsets de paralaxe reais)
        // Isso mapeia a diferença de FOV entre a lente térmica e a lente óptica
        double real2Ir = 1.2895218;
        int offsetX = -6;
        int offsetY = 9;

        byte[] outputPixels = new byte[width * height * 4];
        double centerX = width / 2.0;
        double centerY = height / 2.0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Fórmula de mapeamento: do Térmico para o Óptico
                int vx = (int)Math.Round((x - centerX) / real2Ir + centerX + offsetX);
                int vy = (int)Math.Round((y - centerY) / real2Ir + centerY + offsetY);

                // Índice do pixel térmico de destino
                int destIdx = (y * width + x) * 4;
                
                // Se cair dentro da imagem óptica, copia. Senão, fica preto.
                if (vx >= 0 && vx < width && vy >= 0 && vy < height)
                {
                    int srcIdx = (vy * width + vx) * 4;
                    outputPixels[destIdx]     = inputPixels[srcIdx];
                    outputPixels[destIdx + 1] = inputPixels[srcIdx + 1];
                    outputPixels[destIdx + 2] = inputPixels[srcIdx + 2];
                    outputPixels[destIdx + 3] = inputPixels[srcIdx + 3];
                }
                else
                {
                    // Bordas preenchidas com preto ou transparência
                    outputPixels[destIdx]     = 0;
                    outputPixels[destIdx + 1] = 0;
                    outputPixels[destIdx + 2] = 0;
                    outputPixels[destIdx + 3] = 255;
                }
            }
        }
        return outputPixels;
    }

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

    private byte[] RenderComposedMode(ImageViewMode mode, byte[] thermalPixels, int width, int height, byte[] visiblePixels)
    {
        return _viewPipeline.ComposeViewMode(
            MapToCoreImageViewMode(mode),
            thermalPixels,
            width,
            height,
            visiblePixels,
            Math.Clamp(mode == ImageViewMode.Blending ? BlendFactor : MsxStrength, 0.0, 1.0),
            Math.Clamp(PipScale, 0.1, 0.8),
            _loadedImage);
    }

    private bool TryRenderThermalPixelsViaPipeline(
        ThermalImageData image,
        ThermalPalette palette,
        double levelMinC,
        double levelMaxC,
        out byte[] thermalPixels,
        out double appliedMinC,
        out double appliedMaxC)
    {
        thermalPixels = Array.Empty<byte>();
        appliedMinC = levelMinC;
        appliedMaxC = levelMaxC;

        try
        {
            if (palette == ThermalPalette.Original)
            {
                var radiometric = _viewPipeline.RenderRadiometric(image, new ThermalRenderParameters
                {
                    AutoScale = false,
                    LevelMinC = levelMinC,
                    LevelMaxC = levelMaxC,
                    Palette = ThermalPalette.Original
                });
                thermalPixels = radiometric.BgraPixels;
                appliedMinC = radiometric.AppliedMinC;
                appliedMaxC = radiometric.AppliedMaxC;
                return thermalPixels.Length > 0;
            }

            thermalPixels = _viewPipeline.RenderRadiometricWithPaletteAsync(
                image,
                palette.ToString(),
                levelMinC,
                levelMaxC).GetAwaiter().GetResult();
            return thermalPixels.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldInferCaptureModeFromPixels(global::ThermixStudio.Core.ImageViewMode? metadataMode)
    {
        if (!metadataMode.HasValue)
        {
            return true;
        }

        return metadataMode.Value is not (
            global::ThermixStudio.Core.ImageViewMode.PiP or
            global::ThermixStudio.Core.ImageViewMode.Visible);
    }

    private bool TryBuildRenderedThermalLumaPlane(ThermalImageData image, ThermalPalette palette, out double[]? luma)
    {
        luma = null;
        if (!TryRenderThermalPixelsViaPipeline(image, palette, LevelMinC, LevelMaxC, out var thermalPixels, out _, out _))
        {
            return false;
        }

        luma = ComputeBgraLumaPlane(thermalPixels, image.Width, image.Height);
        return true;
    }

    private static global::ThermixStudio.Core.ImageViewMode MapToCoreImageViewMode(ImageViewMode mode)
        => mode switch
        {
            ImageViewMode.Original => global::ThermixStudio.Core.ImageViewMode.Original,
            ImageViewMode.Thermal => global::ThermixStudio.Core.ImageViewMode.Thermal,
            ImageViewMode.Visible => global::ThermixStudio.Core.ImageViewMode.Visible,
            ImageViewMode.Fusion => global::ThermixStudio.Core.ImageViewMode.Fusion,
            ImageViewMode.Blending => global::ThermixStudio.Core.ImageViewMode.Blending,
            ImageViewMode.PiP => global::ThermixStudio.Core.ImageViewMode.PiP,
            ImageViewMode.Msx => global::ThermixStudio.Core.ImageViewMode.Msx,
            _ => global::ThermixStudio.Core.ImageViewMode.Thermal
        };

    private static ImageViewMode MapFromCoreImageViewMode(global::ThermixStudio.Core.ImageViewMode mode)
        => mode switch
        {
            global::ThermixStudio.Core.ImageViewMode.Original => ImageViewMode.Original,
            global::ThermixStudio.Core.ImageViewMode.Thermal => ImageViewMode.Thermal,
            global::ThermixStudio.Core.ImageViewMode.Visible => ImageViewMode.Visible,
            global::ThermixStudio.Core.ImageViewMode.Fusion => ImageViewMode.Fusion,
            global::ThermixStudio.Core.ImageViewMode.Blending => ImageViewMode.Blending,
            global::ThermixStudio.Core.ImageViewMode.PiP => ImageViewMode.PiP,
            global::ThermixStudio.Core.ImageViewMode.Msx => ImageViewMode.Msx,
            _ => ImageViewMode.Thermal
        };

    private bool TryRenderThermalPixelsFromCapturedImage(ThermalImageData image, ThermalPalette targetPalette, out byte[] thermalPixels)
    {
        thermalPixels = Array.Empty<byte>();
        var sourcePalette = ResolvePaletteFromMetadata(image.Metadata);

        if (ImageViewMode is ImageViewMode.Visible or ImageViewMode.Original)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(CurrentImagePath) || !File.Exists(CurrentImagePath))
        {
            return false;
        }

        if (!TryLoadOriginalCameraBgraPixels(image.Width, image.Height, out var originalPixels) || originalPixels is null)
        {
            return false;
        }

        if (ImageViewMode is ImageViewMode.Msx or ImageViewMode.Blending or ImageViewMode.PiP)
        {
            if (targetPalette == ThermalPalette.Original || targetPalette == sourcePalette)
            {
                return false;
            }

            if (!IsProcessSmartHdPalette(sourcePalette) || !IsProcessSmartHdPalette(targetPalette))
            {
                return false;
            }

            try
            {
                using var originalBitmap = new Bitmap(CurrentImagePath);
                thermalPixels = _viewPipeline.RemapCapturedFrame(
                    originalBitmap,
                    sourcePalette.ToString(),
                    targetPalette.ToString());
                return thermalPixels.Length > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PALETTE] ProcessSmartHD failed: {ex.Message}");
                LogToFile($"[PALETTE] ProcessSmartHD failed: {ex.Message}");
                return false;
            }
        }

        if (!AutoScaleEnabled || _autoAdjustRegion.HasValue)
        {
            return false;
        }

        if (!IsProcessSmartHdPalette(sourcePalette))
        {
            return false;
        }

        if (targetPalette == ThermalPalette.Original || targetPalette == sourcePalette)
        {
            thermalPixels = originalPixels;
            return true;
        }

        if (!IsProcessSmartHdPalette(targetPalette))
        {
            return false;
        }

        try
        {
            using var originalBitmap = new Bitmap(CurrentImagePath);
            thermalPixels = _viewPipeline.RemapCapturedFrame(
                originalBitmap,
                sourcePalette.ToString(),
                targetPalette.ToString());
            return thermalPixels.Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PALETTE] ProcessSmartHD failed: {ex.Message}");
            LogToFile($"[PALETTE] ProcessSmartHD failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsProcessSmartHdPalette(ThermalPalette palette)
        => palette is ThermalPalette.Iron
            or ThermalPalette.Rainbow
            or ThermalPalette.Grayscale
            or ThermalPalette.Hotmetal
            or ThermalPalette.Arctic
            or ThermalPalette.Thermal
            or ThermalPalette.Jet
            or ThermalPalette.Hot
            or ThermalPalette.Cool;

    private async Task<global::ThermixStudio.Core.ImageViewMode?> DetectOriginalCaptureModeAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return await _viewPipeline.DetectCaptureModeFromMetadataAsync(filePath);
        }
        catch
        {
            return null;
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

    // Ilustrações persistidas em ProcessingJson do termograma selecionado
    public ObservableCollection<IIllustration> Illustrations { get; } = new();

    public async Task RemoveIllustrationByIdAsync(Guid id)
    {
        var existing = Illustrations.FirstOrDefault(i => i.Id == id);
        if (existing is null)
        {
            return;
        }

        PushIllustrationUndoSnapshot();
        Illustrations.Remove(existing);
        await PersistIllustrationsStateAsync();
    }

    public async Task AddIllustrationAsync(IIllustration illustration)
    {
        var normalized = illustration as ThermalIllustration ?? new ThermalIllustration
        {
            Id = illustration.Id,
            Type = illustration.Type,
            X1 = illustration.X1,
            Y1 = illustration.Y1,
            X2 = illustration.X2,
            Y2 = illustration.Y2,
            Text = illustration.Text
        };

        var existing = Illustrations.FirstOrDefault(i => i.Id == normalized.Id);
        if (existing is null)
        {
            PushIllustrationUndoSnapshot();
            Illustrations.Add(normalized);
        }
        else
        {
            var changed = existing.Type != normalized.Type ||
                          existing.X1 != normalized.X1 ||
                          existing.Y1 != normalized.Y1 ||
                          existing.X2 != normalized.X2 ||
                          existing.Y2 != normalized.Y2 ||
                          !string.Equals(existing.Text, normalized.Text, StringComparison.Ordinal);
            if (!changed)
            {
                return;
            }

            PushIllustrationUndoSnapshot();
            existing.Type = normalized.Type;
            existing.X1 = normalized.X1;
            existing.Y1 = normalized.Y1;
            existing.X2 = normalized.X2;
            existing.Y2 = normalized.Y2;
            existing.Text = normalized.Text;
        }

        await PersistIllustrationsStateAsync();
    }

    public async Task UpdateIllustrationAsync(Guid id, IIllustration illustration)
    {
        var existing = Illustrations.FirstOrDefault(i => i.Id == id);
        if (existing is null)
        {
            return;
        }

        var changed = existing.Type != illustration.Type ||
                      existing.X1 != illustration.X1 ||
                      existing.Y1 != illustration.Y1 ||
                      existing.X2 != illustration.X2 ||
                      existing.Y2 != illustration.Y2 ||
                      !string.Equals(existing.Text, illustration.Text, StringComparison.Ordinal);
        if (!changed)
        {
            return;
        }

        PushIllustrationUndoSnapshot();
        existing.Type = illustration.Type;
        existing.X1 = illustration.X1;
        existing.Y1 = illustration.Y1;
        existing.X2 = illustration.X2;
        existing.Y2 = illustration.Y2;
        existing.Text = illustration.Text;

        await PersistIllustrationsStateAsync();
    }

    private async Task PersistIllustrationsStateAsync()
    {
        if (SelectedThermogram is null)
        {
            return;
        }

        PersistCurrentStateToSelectedThermogram();
        await _dataService.UpdateThermogramAsync(SelectedThermogram);
    }

    private void PushIllustrationUndoSnapshot()
    {
        if (_isRestoringIllustrationUndo || SelectedThermogram is null)
        {
            return;
        }

        var stack = GetIllustrationUndoStack(SelectedThermogram.Id);
        var snapshot = Illustrations
            .OfType<ThermalIllustration>()
            .Select(CloneIllustration)
            .ToList();

        stack.Push(snapshot);

        // Limitar histórico para evitar crescimento indefinido em sessões longas.
        const int maxUndoSteps = 50;
        if (stack.Count <= maxUndoSteps)
        {
            return;
        }

        var trimmed = stack.Take(maxUndoSteps).Reverse().ToArray();
        stack.Clear();
        foreach (var state in trimmed)
        {
            stack.Push(state);
        }
    }

    private Stack<List<ThermalIllustration>> GetIllustrationUndoStack(Guid thermogramId)
    {
        if (_illustrationUndoHistory.TryGetValue(thermogramId, out var existing))
        {
            return existing;
        }

        var created = new Stack<List<ThermalIllustration>>();
        _illustrationUndoHistory[thermogramId] = created;
        return created;
    }

    private async Task<bool> TryUndoIllustrationActionAsync()
    {
        if (SelectedThermogram is null)
        {
            return false;
        }

        var stack = GetIllustrationUndoStack(SelectedThermogram.Id);
        if (stack.Count == 0)
        {
            return false;
        }

        var previous = stack.Pop();
        _isRestoringIllustrationUndo = true;
        try
        {
            Illustrations.Clear();
            foreach (var item in previous)
            {
                Illustrations.Add(CloneIllustration(item));
            }

            await PersistIllustrationsStateAsync();
            StatusMessage = "Ultima ilustração desfeita (Ctrl+Z).";
            return true;
        }
        finally
        {
            _isRestoringIllustrationUndo = false;
        }
    }

    private static ThermalIllustration CloneIllustration(ThermalIllustration source)
        => new()
        {
            Id = source.Id,
            Type = source.Type,
            X1 = source.X1,
            Y1 = source.Y1,
            X2 = source.X2,
            Y2 = source.Y2,
            Text = source.Text
        };

    // Stubs para features de medições (não completamente implementadas)
    public async Task SetMeasurementMaxAdmissibleAsync(Guid measurementId, double maxAdmissible)
    {
        await Task.CompletedTask;
    }

    public async Task RemoveMeasurementByIdAsync(int id)
    {
        await Task.CompletedTask;
    }
}
