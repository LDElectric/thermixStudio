using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThermixStudio.Core;

namespace ThermixStudio.App.ViewModels;

public sealed partial class ReportEditorViewModel : ObservableObject
{
    private readonly IAppDataService _dataService;
    private readonly IReportService _reportService;
    private CancellationTokenSource? _previewRefreshCts;

    [ObservableProperty]
    private string osNumber = "-";

    [ObservableProperty]
    private string installationName = string.Empty;

    [ObservableProperty]
    private string equipmentName = string.Empty;

    [ObservableProperty]
    private DateTime reportDate = DateTime.Now;

    [ObservableProperty]
    private string technicalOpinion = string.Empty;

    [ObservableProperty]
    private string recommendedAction = string.Empty;

    [ObservableProperty]
    private string previewHtml = GetEmptyPreviewHtml();

    [ObservableProperty]
    private Thermogram? selectedAvailableThermogram;

    [ObservableProperty]
    private ReportSectionItemViewModel? selectedSection;

    public ObservableCollection<Thermogram> AvailableThermograms { get; } = [];
    public ObservableCollection<ReportSectionItemViewModel> Sections { get; } = [];

    public IAsyncRelayCommand AddSelectedThermogramCommand { get; }
    public IRelayCommand RemoveSelectedSectionCommand { get; }
    public IAsyncRelayCommand GenerateReportCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<bool>? CloseRequested;

    public ReportEditorViewModel(IAppDataService dataService, IReportService reportService)
    {
        _dataService = dataService;
        _reportService = reportService;

        AddSelectedThermogramCommand = new AsyncRelayCommand(AddSelectedThermogramAsync);
        RemoveSelectedSectionCommand = new RelayCommand(RemoveSelectedSection);
        GenerateReportCommand = new AsyncRelayCommand(GenerateReportAsync);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
    }

    partial void OnOsNumberChanged(string value) => SchedulePreviewRefresh();
    partial void OnInstallationNameChanged(string value) => SchedulePreviewRefresh();
    partial void OnEquipmentNameChanged(string value) => SchedulePreviewRefresh();
    partial void OnReportDateChanged(DateTime value) => SchedulePreviewRefresh();
    partial void OnTechnicalOpinionChanged(string value) => SchedulePreviewRefresh();
    partial void OnRecommendedActionChanged(string value)
    {
        foreach (var section in Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Recommendation))
            {
                section.Recommendation = value;
            }
        }

        SchedulePreviewRefresh();
    }
    partial void OnSelectedAvailableThermogramChanged(Thermogram? value) => SchedulePreviewRefresh();

    public async Task LoadAsync(Inspection inspection, IEnumerable<Thermogram> availableThermograms, Thermogram? initialThermogram)
    {
        OsNumber = string.IsNullOrWhiteSpace(inspection.OsNumber) ? "-" : inspection.OsNumber;
        InstallationName = inspection.Plant;
        ReportDate = inspection.StartAtUtc == default ? DateTime.Now : inspection.StartAtUtc;

        AvailableThermograms.Clear();
        foreach (var thermogram in availableThermograms)
        {
            AvailableThermograms.Add(thermogram);
        }

        Sections.Clear();
        if (initialThermogram is not null)
        {
            await AddThermogramToReportAsync(initialThermogram);
        }

        SelectedSection = Sections.FirstOrDefault();

        await RefreshPreviewAsync(CancellationToken.None);
    }

    private async Task AddSelectedThermogramAsync()
    {
        if (SelectedAvailableThermogram is null)
        {
            return;
        }

        await AddThermogramToReportAsync(SelectedAvailableThermogram);
    }

    private async Task AddThermogramToReportAsync(Thermogram thermogram)
    {
        if (Sections.Any(x => x.Thermogram.Id == thermogram.Id))
        {
            return;
        }

        var measurements = await _dataService.GetMeasurementsByThermogramAsync(thermogram.Id);
        var index = Sections.Count + 1;
        var sectionItem = new ReportSectionItemViewModel(thermogram)
        {
            Title = $"Termograma {index}",
            Observations = thermogram.Notes,
            Recommendation = RecommendedAction,
        };

        sectionItem.PropertyChanged += SectionItemOnPropertyChanged;
        sectionItem.SetMeasurements(measurements);
        Sections.Add(sectionItem);
        SelectedSection = sectionItem;
        SchedulePreviewRefresh();
    }

    private void RemoveSelectedSection()
    {
        if (SelectedSection is null)
        {
            return;
        }

        SelectedSection.PropertyChanged -= SectionItemOnPropertyChanged;
        var removedIndex = Sections.IndexOf(SelectedSection);
        Sections.Remove(SelectedSection);

        if (Sections.Count == 0)
        {
            SelectedSection = null;
        }
        else
        {
            var nextIndex = Math.Clamp(removedIndex, 0, Sections.Count - 1);
            SelectedSection = Sections[nextIndex];
        }

        for (var i = 0; i < Sections.Count; i++)
        {
            Sections[i].Title = $"Termograma {i + 1}";
        }

        SchedulePreviewRefresh();
    }

    private async Task GenerateReportAsync()
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "Reports");
        var result = await GenerateReportToDirectoryAsync(outputDirectory);
        if (result is null)
        {
            return;
        }

        CloseRequested?.Invoke(true);
    }

    public async Task<ReportResult?> GenerateReportToDirectoryAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (Sections.Count == 0)
        {
            return null;
        }

        var request = BuildReportRequest();
        return await _reportService.GenerateAsync(request, outputDirectory, cancellationToken);
    }

    private ReportRequest BuildReportRequest()
    {
        var inspection = new Inspection
        {
            OsNumber = string.IsNullOrWhiteSpace(OsNumber) ? "-" : OsNumber,
            Plant = InstallationName,
            StartAtUtc = ReportDate,
            TechnicianName = string.Empty
        };

        var request = new ReportRequest
        {
            Inspection = inspection,
            InstallationName = InstallationName,
            EquipmentName = EquipmentName,
            ReportDate = ReportDate,
            Sections = Sections.Select(section => new ReportSectionRequest
            {
                Thermogram = section.Thermogram,
                Measurements = section.Measurements,
                Title = section.Title,
                Observations = section.Observations,
                Recommendation = string.IsNullOrWhiteSpace(section.Recommendation) ? RecommendedAction : section.Recommendation
            }).ToList(),
            TechnicalOpinion = TechnicalOpinion,
            RecommendedAction = RecommendedAction
        };

        return request;
    }

    private void SectionItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SchedulePreviewRefresh();
    }

    private void SchedulePreviewRefresh()
    {
        _previewRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewRefreshCts = cts;
        _ = RefreshPreviewAsync(cts.Token);
    }

    private async Task RefreshPreviewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var previewRequest = await BuildPreviewRequestAsync(cancellationToken);
            PreviewHtml = await _reportService.BuildPreviewHtmlAsync(previewRequest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<ReportRequest> BuildPreviewRequestAsync(CancellationToken cancellationToken)
    {
        var request = BuildReportRequest();
        if (request.Sections.Count > 0 || SelectedAvailableThermogram is null)
        {
            return request;
        }

        var measurements = await _dataService.GetMeasurementsByThermogramAsync(SelectedAvailableThermogram.Id, cancellationToken);
        var transientSection = new ReportSectionRequest
        {
            Thermogram = SelectedAvailableThermogram,
            Measurements = measurements,
            Title = "Termograma selecionado",
            Observations = SelectedAvailableThermogram.Notes,
            Recommendation = RecommendedAction
        };

        return new ReportRequest
        {
            Inspection = request.Inspection,
            InstallationName = request.InstallationName,
            EquipmentName = request.EquipmentName,
            ReportDate = request.ReportDate,
            Sections = [transientSection],
            TechnicalOpinion = request.TechnicalOpinion,
            RecommendedAction = request.RecommendedAction
        };
    }

    public sealed partial class ReportSectionItemViewModel : ObservableObject
    {
        public Thermogram Thermogram { get; }

        [ObservableProperty]
        private string title = string.Empty;

        [ObservableProperty]
        private string observations = string.Empty;

        [ObservableProperty]
        private string recommendation = string.Empty;

        [ObservableProperty]
        private IReadOnlyList<ThermalMeasurement> measurements = [];

        public string ThermogramLabel => $"{Path.GetFileName(Thermogram.FilePath)} | {Thermogram.EquipmentTag}";
        public string MeasurementsLabel => $"{Measurements.Count} medição(ões)";

        public ReportSectionItemViewModel(Thermogram thermogram)
        {
            Thermogram = thermogram;
            title = string.IsNullOrWhiteSpace(thermogram.EquipmentTag) ? Path.GetFileNameWithoutExtension(thermogram.FilePath) : thermogram.EquipmentTag;
            observations = thermogram.Notes;
        }

        public void SetMeasurements(IReadOnlyList<ThermalMeasurement> value)
        {
            Measurements = value;
            OnPropertyChanged(nameof(MeasurementsLabel));
        }
    }

    private static string GetEmptyPreviewHtml()
        => "<!DOCTYPE html><html lang='pt-BR'><head><meta charset='utf-8'><style>body{font-family:Segoe UI,sans-serif;margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh;background:#f4f6fa;color:#5a6376;text-align:center;padding:32px}div{max-width:460px}h1{color:#1f2430;font-size:24px;margin:0 0 12px}p{line-height:1.5;margin:0}</style></head><body><div><h1>Pré-visualização técnica</h1><p>Renderização do relatório termográfico em preparação.</p></div></body></html>";
}