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
    Iron = 1,           // FLIR clássica
    Rainbow = 2,        // FLIR arco-íris
    Grayscale = 3,      // FLIR escala de cinza
    Hotmetal = 4,       // FLIR metal quente
    Arctic = 5,         // FLIR ártico
    Thermal = 6,        // FLIR térmica
    Jet = 7,            // Fluke / Hikvision - Jet
    Hot = 8,            // Fluke / Hikvision - Hot
    Cool = 9,           // Fluke / Hikvision - Cool
    Original = 99       // Paleta original FLIR embarcada (YCbCr convertida)
}

public enum ImageViewMode
{
    Original = 0,
    Thermal = 1,
    Visible = 2,
    Fusion = 3,
    Blending = 4,
    PiP = 5,
    Msx = 6
}

public enum IllustrationType
{
    Arrow = 1,
    Rectangle = 2,
    Ellipse = 3,
    Text = 4
}

/// <summary>
/// Interface para ilustração em termogramas (anotações de tipo geométrico/texto).
/// </summary>
public interface IIllustration
{
    Guid Id { get; set; }
    IllustrationType Type { get; set; }
    double X1 { get; set; }
    double Y1 { get; set; }
    double X2 { get; set; }
    double Y2 { get; set; }
    string Text { get; set; }
}

/// <summary>
/// Marca da câmera termográfica detectada pelo EXIF ou extensão do arquivo.
/// </summary>
public enum ThermalCameraBrand
{
    Unknown    = 0,
    Flir       = 1,
    Fluke      = 2,
    Hikvision  = 3,
    InfiRay    = 4,
    Guide      = 5,
    Bosch      = 6,
    Seek       = 7,
    Testo      = 8,
    Generic    = 99
}

public enum VisualScaleSource
{
    Unknown = 0,
    BurnedInScale = 1,
    VisualFitToReference = 2,
    ExifImageTemperature = 3,
    MatrixRange = 4,
    Manual = 5
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
    public DateTime CaptureAtUtc { get; set; } = DateTime.Now;
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
    public ushort[,]? RawValues { get; set; }
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
    public ImageViewMode? DetectedViewMode { get; set; }
    public string? PaletteName { get; set; }
    public ThermalPalette? DetectedPalette { get; set; }  // Mapeado de PaletteName via ExifTool
    public string? VisibleImagePath { get; set; }
    public double? PlanckR1 { get; set; }
    public double? PlanckR2 { get; set; }
    public double? PlanckB { get; set; }
    public double? PlanckF { get; set; }
    public double? PlanckO { get; set; }
    public string Notes { get; set; } = string.Empty;

    // ── Campos adicionais extraídos do EXIF (exibição no painel, não vão ao relatório) ──
    public string? DateTimeOriginal { get; set; }   // Data/hora real de captura com fuso horário
    public string? CameraSerialNumber { get; set; } // N° de série da câmera
    public double? FieldOfView { get; set; }        // Campo de visão em graus (°)
    public double? IRWindowTemperatureC { get; set; } // Temperatura da janela IR (°C)
    public double? IRWindowTransmission { get; set; } // Transmitância da janela IR (0–1)

    // ── Escala da barra FLIR (valores exibidos no LCD/JPEG: ex. 23,0 e 42,2 °C) ──
    // Derivada de ImageTemperatureMin/Max (inteiros em K no EXIF) pela regra de display da FLIR.
    // Usada para autoscale, render, Below/Above Color e UI — NÃO usar conversão −273,15 aqui.
    public double? PaletteScaleMinC { get; set; }
    public double? PaletteScaleMaxC { get; set; }
    public double? VisualScaleMinC { get; set; }
    public double? VisualScaleMaxC { get; set; }
    public VisualScaleSource VisualScaleSource { get; set; } = VisualScaleSource.Unknown;
    public double? VisualScaleConfidence { get; set; }
    /// <summary>ImageTemperatureMin do EXIF como inteiro em K (ex.: 295).</summary>
    public int? ImageTemperatureMinK { get; set; }
    /// <summary>ImageTemperatureMax do EXIF como inteiro em K (ex.: 317).</summary>
    public int? ImageTemperatureMaxK { get; set; }
    /// <summary>Faixa de contagem RAW da câmera (tag RawValueRangeMin/Max do EXIF).</summary>
    public int? RawValueRangeMin { get; set; }
    public int? RawValueRangeMax { get; set; }
    public int? RawValueMedian { get; set; }
    public int? RawValueRange { get; set; }
    public int? PaletteColors { get; set; }
    public string? PaletteFileName { get; set; }
    public int? PaletteMethod { get; set; }
    public int? PaletteStretch { get; set; }
    public int[]? PaletteAboveColorYCrCb { get; set; }
    public int[]? PaletteBelowColorYCrCb { get; set; }
    public int[]? PaletteOverflowColorYCrCb { get; set; }
    public int[]? PaletteUnderflowColorYCrCb { get; set; }
    public int[]? Isotherm1ColorYCrCb { get; set; }
    public int[]? Isotherm2ColorYCrCb { get; set; }
    public double? UnknownTemperature { get; set; }
    /// <summary>Paleta FLIR embutida (256×4 BGRA), extraída do JPEG no carregamento.</summary>
    public byte[]? EmbeddedPaletteBgra { get; set; }
    public double? SpotTemperatureC { get; set; }
    public double? SpotNormalizedX { get; set; }
    public double? SpotNormalizedY { get; set; }

    // FLIR PiP/MSX alignment metadata. Real2IR is the visible-to-IR scale factor;
    // OffsetX/OffsetY are insertion offsets from the image center.
    public double? Real2IR { get; set; }
    public int? OffsetX { get; set; }
    public int? OffsetY { get; set; }
    public int? PiPX1 { get; set; }
    public int? PiPX2 { get; set; }
    public int? PiPY1 { get; set; }
    public int? PiPY2 { get; set; }
}

public sealed class ThermalProcessingState
{
    public ImageViewMode ViewMode { get; set; } = ImageViewMode.Thermal;
    public bool AutoScale { get; set; } = true;
    public double? LevelMinC { get; set; }
    public double? LevelMaxC { get; set; }
    public double? MaxAdmissibleC { get; set; }
    public double Emissivity { get; set; } = 0.95;
    public ThermalPalette Palette { get; set; } = ThermalPalette.Iron;
    public double BlendFactor { get; set; } = 0.55;
    public double PipScale { get; set; } = 0.55;
    public double MsxStrength { get; set; } = 0.10;
    public ImageViewMode? MetadataDetectedMode { get; set; }
    public string? VisibleImagePath { get; set; }
    public bool UseEmbeddedPalette { get; set; } = false;
    public string? EmbeddedPaletteData { get; set; }  // Base64-encoded 256×4 BGRA bytes
    public double? VisualScaleMinC { get; set; }
    public double? VisualScaleMaxC { get; set; }
    public VisualScaleSource VisualScaleSource { get; set; } = VisualScaleSource.Unknown;
    public double? VisualScaleConfidence { get; set; }
    public ThermalCameraBrand DetectedBrand { get; set; } = ThermalCameraBrand.Unknown;
    public double? TargetDistanceM { get; set; }
    public double? AmbientTemperatureC { get; set; }
    public double? RelativeHumidityRh { get; set; }
    public List<ThermalIllustration> Illustrations { get; set; } = [];

    /// <summary>
    /// Indica se a inferência visual de modo de captura já foi executada para este termograma.
    /// </summary>
    public bool VisualInferenceInitialized { get; set; }

    /// <summary>
    /// Versão das regras de inferência visual usadas. Permite re-executar quando regras mudam.
    /// </summary>
    public int VisualInferenceRuleVersion { get; set; }
}

public sealed class ThermalIllustration : IIllustration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public IllustrationType Type { get; set; } = IllustrationType.Arrow;
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class ThermalRenderParameters
{
    public bool AutoScale { get; set; } = true;
    public double? LevelMinC { get; set; }
    public double? LevelMaxC { get; set; }
    public ThermalPalette Palette { get; set; } = ThermalPalette.Iron;
    public bool PreferEmbeddedPalette { get; set; } = true;
    public bool UseFlirLimitColors { get; set; } = true;
    public bool ApplyDde { get; set; } = true;
}

public sealed class VisualScaleDetectionResult
{
    public bool Success { get; set; }
    public double? MinC { get; set; }
    public double? MaxC { get; set; }
    public VisualScaleSource Source { get; set; } = VisualScaleSource.Unknown;
    public double Confidence { get; set; }
    public string DetectorName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
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
    /// <summary>Caminho para imagem IR com marcadores (spots/áreas) sobrepostos. Quando nulo, usa Thermogram.FilePath.</summary>
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
