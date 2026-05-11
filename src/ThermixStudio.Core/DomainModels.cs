namespace ThermixStudio.Core;

public enum EquipmentCriticality
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum InspectionStatus
{
    Draft = 1,
    InProgress = 2,
    Completed = 3
}

public enum MeasurementType
{
    Spot = 1,
    Area = 2,
    Line = 3,
    Isotherm = 4,
    Circle = 5,
    Difference = 6
}

public enum SeverityClass
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum ThermalPalette
{
    Iron = 1,
    Rainbow = 2,
    Grayscale = 3,
    Hotmetal = 4
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = "Thermographer";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

public sealed class Inspection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OsNumber { get; set; } = string.Empty;
    public DateTime StartAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndAtUtc { get; set; }
    public string TechnicianName { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public string Plant { get; set; } = string.Empty;
    public InspectionStatus Status { get; set; } = InspectionStatus.Draft;
    public string Notes { get; set; } = string.Empty;
}

public sealed class Thermogram
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? InspectionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime CaptureAtUtc { get; set; } = DateTime.UtcNow;
    public string CameraModel { get; set; } = "FLIR";
    public string MetadataJson { get; set; } = "{}";
    public string ProcessingJson { get; set; } = "{}";
    public string Status { get; set; } = "processed";

    // Equipment information associated directly with the thermogram
    public string EquipmentTag { get; set; } = string.Empty;
    public string EquipmentDescription { get; set; } = string.Empty;
    public string EquipmentLocation { get; set; } = string.Empty;
    public EquipmentCriticality Criticality { get; set; } = EquipmentCriticality.Medium;
    public string Notes { get; set; } = string.Empty;
}

public sealed class ThermalMeasurement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ThermogramId { get; set; }
    public MeasurementType Type { get; set; } = MeasurementType.Spot;
    public double Tmin { get; set; }
    public double Tmax { get; set; }
    public double Tavg { get; set; }
    public double DeltaT { get; set; }
    public string CoordinatesJson { get; set; } = "{}";
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ReportRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ReportType { get; set; } = "individual";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "generated";
    public string TemplateName { get; set; } = "Default";
}

public sealed class TrendPoint
{
    public DateTime DateUtc { get; set; }
    public double Temperature { get; set; }
}

public sealed class ThermalStatistics
{
    public double Tmin { get; set; }
    public double Tmax { get; set; }
    public double Tavg { get; set; }
    public double DeltaT => Tmax - Tmin;
}

public sealed class ThermalImageData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double[,] Temperatures { get; set; } = new double[1, 1];
    public string SourceFormat { get; set; } = "Unknown";
    public bool IsRadiometricLikely { get; set; }
    public RadiometricMetadata Metadata { get; set; } = new();
}

public sealed class RadiometricMetadata
{
    public string CameraModel { get; set; } = "Unknown";
    public string Manufacturer { get; set; } = "Unknown";
    public double? Emissivity { get; set; }
    public double? ReflectedTemperatureC { get; set; }
    public double? AmbientTemperatureC { get; set; }
    public double? RelativeHumidity { get; set; }
    public double? ObjectDistanceM { get; set; }
    public string Detector { get; set; } = "Unknown";
    public string? PaletteName { get; set; }
    public string? VisibleImagePath { get; set; }
    public double? PlanckR1 { get; set; }
    public double? PlanckR2 { get; set; }
    public double? PlanckB { get; set; }
    public double? PlanckF { get; set; }
    public double? PlanckO { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class ThermalProcessingState
{
    public bool AutoScale { get; set; } = true;
    public double? LevelMinC { get; set; }
    public double? LevelMaxC { get; set; }
    public double? MaxAdmissibleC { get; set; }
    public double Emissivity { get; set; } = 0.95;
    public ThermalPalette Palette { get; set; } = ThermalPalette.Iron;
    public double MsxStrength { get; set; } = 0.10;
    public string? VisibleImagePath { get; set; }
}

public sealed class ThermalRenderParameters
{
    public bool AutoScale { get; set; } = true;
    public double? LevelMinC { get; set; }
    public double? LevelMaxC { get; set; }
    public ThermalPalette Palette { get; set; } = ThermalPalette.Iron;
}

public sealed class ThermalRenderResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] BgraPixels { get; set; } = [];
    public double AppliedMinC { get; set; }
    public double AppliedMaxC { get; set; }
}

public sealed class LineProfileResult
{
    public IReadOnlyList<double> Temperatures { get; set; } = [];
    public ThermalStatistics Statistics { get; set; } = new();
}

public sealed class ReportSectionRequest
{
    public required Thermogram Thermogram { get; set; }
    public required IReadOnlyList<ThermalMeasurement> Measurements { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Observations { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}

public sealed class ReportRequest
{
    public required Inspection Inspection { get; set; }
    public string InstallationName { get; set; } = string.Empty;
    public string EquipmentName { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; } = DateTime.Now;
    public IReadOnlyList<ReportSectionRequest> Sections { get; set; } = [];
    public string TechnicalOpinion { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;
}

public sealed class ReportResult
{
    public required string HtmlPath { get; set; }
    public required string PdfPath { get; set; }
}