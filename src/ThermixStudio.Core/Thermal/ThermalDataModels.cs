namespace ThermixStudio.Core;

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
    public ThermalPalette? DetectedPalette { get; set; }
    public string? VisibleImagePath { get; set; }
    public double? PlanckR1 { get; set; }
    public double? PlanckR2 { get; set; }
    public double? PlanckB { get; set; }
    public double? PlanckF { get; set; }
    public double? PlanckO { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string? DateTimeOriginal { get; set; }
    public string? CameraSerialNumber { get; set; }
    public double? FieldOfView { get; set; }
    public double? IRWindowTemperatureC { get; set; }
    public double? IRWindowTransmission { get; set; }
    public double? PaletteScaleMinC { get; set; }
    public double? PaletteScaleMaxC { get; set; }
    public double? VisualScaleMinC { get; set; }
    public double? VisualScaleMaxC { get; set; }
    public VisualScaleSource VisualScaleSource { get; set; } = VisualScaleSource.Unknown;
    public double? VisualScaleConfidence { get; set; }
    public int? ImageTemperatureMinK { get; set; }
    public int? ImageTemperatureMaxK { get; set; }
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
    public byte[]? EmbeddedPaletteBgra { get; set; }
    public double? SpotTemperatureC { get; set; }
    public double? SpotNormalizedX { get; set; }
    public double? SpotNormalizedY { get; set; }
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
    public string? EmbeddedPaletteData { get; set; }
    public double? VisualScaleMinC { get; set; }
    public double? VisualScaleMaxC { get; set; }
    public VisualScaleSource VisualScaleSource { get; set; } = VisualScaleSource.Unknown;
    public double? VisualScaleConfidence { get; set; }
    public ThermalCameraBrand DetectedBrand { get; set; } = ThermalCameraBrand.Unknown;
    public double? TargetDistanceM { get; set; }
    public double? AmbientTemperatureC { get; set; }
    public double? RelativeHumidityRh { get; set; }
    public List<ThermalIllustration> Illustrations { get; set; } = [];
    public bool VisualInferenceInitialized { get; set; }
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
