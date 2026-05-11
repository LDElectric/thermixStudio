namespace ThermixStudio.Core;

public interface IAppDataService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Inspection>> GetInspectionsAsync(CancellationToken cancellationToken = default);
    Task<Inspection> UpsertInspectionAsync(Inspection inspection, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Thermogram>> GetAllThermogramsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Thermogram>> GetThermogramsByInspectionAsync(Guid inspectionId, CancellationToken cancellationToken = default);
    Task<Thermogram> AddThermogramAsync(Thermogram thermogram, CancellationToken cancellationToken = default);
    Task<Thermogram> UpdateThermogramAsync(Thermogram thermogram, CancellationToken cancellationToken = default);
    Task<bool> RemoveThermogramAsync(Guid thermogramId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThermalMeasurement>> GetMeasurementsByThermogramAsync(Guid thermogramId, CancellationToken cancellationToken = default);
    Task<ThermalMeasurement> AddMeasurementAsync(ThermalMeasurement measurement, CancellationToken cancellationToken = default);
    Task<bool> RemoveMeasurementAsync(Guid measurementId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrendPoint>> GetThermogramTrendAsync(Guid thermogramId, CancellationToken cancellationToken = default);

    Task<ReportRecord> AddReportRecordAsync(ReportRecord reportRecord, CancellationToken cancellationToken = default);
}

public interface IThermalAnalysisService
{
    Task<ThermalImageData> LoadImageAsync(string imagePath, CancellationToken cancellationToken = default);
    double GetTemperatureAt(ThermalImageData image, int x, int y);
    ThermalStatistics GetAreaStatistics(ThermalImageData image, int x, int y, int width, int height);
    LineProfileResult GetHorizontalLineProfile(ThermalImageData image, int y);
    ThermalStatistics GetIsothermStatistics(ThermalImageData image, double thresholdC);
}

public interface IThermalRenderEngine
{
    ThermalRenderResult Render(ThermalImageData image, ThermalRenderParameters parameters);
}

public interface IReportService
{
    Task<string> BuildPreviewHtmlAsync(ReportRequest request, CancellationToken cancellationToken = default);
    Task<ReportResult> GenerateAsync(ReportRequest request, string outputDirectory, CancellationToken cancellationToken = default);
}