namespace ThermixStudio.Core;

public sealed class ReportSectionRequest
{
    public required Thermogram Thermogram { get; set; }
    public required IReadOnlyList<ThermalMeasurement> Measurements { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Observations { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string? AnnotatedThermalImagePath { get; set; }
}

public sealed class ReportRequest
{
    public required Inspection Inspection { get; set; }
    public string InstallationName { get; set; } = string.Empty;
    public string EquipmentName { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; } = DateTime.Now;
    public string TechnicianName { get; set; } = string.Empty;
    public string CertificationNumber { get; set; } = string.Empty;
    public IReadOnlyList<ReportSectionRequest> Sections { get; set; } = [];
    public string TechnicalOpinion { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;
}

public sealed class ReportResult
{
    public required string HtmlPath { get; set; }
    public required string PdfPath { get; set; }
}
