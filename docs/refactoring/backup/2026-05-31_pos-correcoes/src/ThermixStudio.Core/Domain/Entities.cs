namespace ThermixStudio.Core;

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
    public DateTime CaptureAtUtc { get; set; } = DateTime.Now;
    public string CameraModel { get; set; } = "FLIR";
    public string MetadataJson { get; set; } = "{}";
    public string ProcessingJson { get; set; } = "{}";
    public string Status { get; set; } = "processed";
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
    public double? MaxAdmissibleC { get; set; }
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
