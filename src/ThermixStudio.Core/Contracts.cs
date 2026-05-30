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
    Task<ThermalMeasurement> UpdateMeasurementAsync(ThermalMeasurement measurement, CancellationToken cancellationToken = default);
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
    Task<byte[]?> TryExtractEmbeddedPaletteAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detecta a marca da câmera a partir do EXIF (Make/Model) ou extensão do arquivo.
    /// Rápido e síncrono — lê apenas o cabeçalho EXIF.
    /// </summary>
    ThermalCameraBrand DetectCameraBrand(string filePath);
}

public interface IVisualScaleDetector
{
    Task<VisualScaleDetectionResult> DetectAsync(
        string imagePath,
        ThermalImageData image,
        CancellationToken cancellationToken = default);
}

public interface IImageMetadataPreservationService
{
    Task<bool> CopyOriginalMetadataAsync(
        string originalImagePath,
        string exportedImagePath,
        CancellationToken cancellationToken = default);
}

public interface IThermalRenderEngine
{
    ThermalRenderResult Render(ThermalImageData image, ThermalRenderParameters parameters);
    void SetEmbeddedPalette(byte[]? paletteData);
}

public interface IReportService
{
    Task<string> BuildPreviewHtmlAsync(ReportRequest request, CancellationToken cancellationToken = default);
    Task<ReportResult> GenerateAsync(ReportRequest request, string outputDirectory, CancellationToken cancellationToken = default);
}
