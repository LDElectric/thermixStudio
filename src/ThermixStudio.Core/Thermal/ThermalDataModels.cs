using ThermixStudio.Core.Thermal;

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

    /// <summary>
    /// LUT temperatura→cor calibrada pelo JPEG original (Hypothesis C).
    /// Quando disponível, substitui o pipeline de paleta tradicional.
    /// </summary>
    public TemperatureColorLut? CalibratedLut { get; set; }

    /// <summary>
    /// Paleta normalizada [0→1] extraída da LUT (256 cores RGB).
    /// Permite sliders dinâmicos mantendo as cores fiéis ao JPEG.
    /// </summary>
    public byte[]? CalibratedPalette { get; set; }

    /// <summary>
    /// LUTs calibradas para outras paletas, derivadas da LUT Ferro.
    /// Chave = nome da paleta ("Rainbow", "Grayscale", etc).
    /// </summary>
    public Dictionary<string, TemperatureColorLut> CalibratedLuts { get; } = new();
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
    public string? SpotLabel { get; set; }
    public double? Real2IR { get; set; }
    public int? OffsetX { get; set; }
    public int? OffsetY { get; set; }
    public int? PiPX1 { get; set; }
    public int? PiPX2 { get; set; }
    public int? PiPY1 { get; set; }
    public int? PiPY2 { get; set; }
    public double? CameraTemperatureMinClip { get; set; }
    public double? CameraTemperatureMaxClip { get; set; }

    /// <summary>
    /// Parâmetros decodificados da tag FLIR_0x0009.
    /// Usado pelo FusionModeDecider para distinguir MSX vs Blending vs Thermal puro.
    /// </summary>
    public FlirFusionParams? FusionParams { get; set; }

    /// <summary>
    /// Perfil tonal extraído do JPEG da câmera (Camada 2 — Curve256).
    /// Se null, o tone mapping opera como identidade.
    /// </summary>
    public ThermalToneProfile? ToneProfile { get; set; }

    /// <summary>°C — extremo frio do Level/Span detectado via reverse-lookup do JPEG.</summary>
    public double? DetectedLevelMin { get; set; }

    /// <summary>°C — extremo quente do Level/Span detectado via reverse-lookup do JPEG.</summary>
    public double? DetectedLevelMax { get; set; }

    /// <summary>0..1 — confiança da detecção (1 = baixo RMSE).</summary>
    public double DetectedLevelConfidence { get; set; }

    /// <summary>°C — Level/Span calculado via Planck inverso de RawValueMedian - Range/2.
    /// Substitui o DetectedLevel (pixel-analysis) com precisão superior e custo zero.</summary>
    public double? RawLevelMin { get; set; }

    /// <summary>°C — Level/Span calculado via Planck inverso de RawValueMedian + Range/2.</summary>
    public double? RawLevelMax { get; set; }
}

/// <summary>
/// Parâmetros decodificados do Record FLIR_0x0009 (assinatura MSX vs Blending).
/// </summary>
public sealed class FlirFusionParams
{
    /// <summary>Versão do record (sempre 1).</summary>
    public int Version { get; set; }

    /// <summary>Largura da EmbeddedImage (pixels) — uint16 LE nos bytes [1..2].</summary>
    public int EmbeddedImageWidth { get; set; }

    /// <summary>Altura da EmbeddedImage (pixels) — uint16 LE nos bytes [4..5].</summary>
    public int EmbeddedImageHeight { get; set; }

    /// <summary>Modo de fusão: 0=Thermal, 1=MSX, 2=Blending.</summary>
    public int FusionMode { get; set; }

    /// <summary>Força do blend (0..1), disponível quando FusionMode=2.</summary>
    public double? BlendStrength { get; set; }

    /// <summary>Modo de visualização inferido deterministicamente a partir do FusionMode.</summary>
    public ImageViewMode InferredViewMode => FusionMode switch
    {
        1 => ImageViewMode.Msx,
        2 => ImageViewMode.Blending,
        _ => ImageViewMode.Thermal,
    };

    /// <summary>
    /// Decodifica o record FLIR_0x0009 a partir do array de bytes bruto.
    /// Estrutura: [0]=version(1), [1..2]=embW(u16 LE), [3]=fusionMode,
    /// [4..5]=embH(u16 LE), [6..7]=flags, [8..11]=blendStrength(float opcional).
    /// </summary>
    public static FlirFusionParams? Decode(byte[] raw)
    {
        if (raw is null || raw.Length < 8)
            return null;

        var result = new FlirFusionParams
        {
            Version = raw[0],
            EmbeddedImageWidth = raw[1] | (raw[2] << 8),
            FusionMode = raw[3],
            EmbeddedImageHeight = raw[4] | (raw[5] << 8),
        };

        if (raw.Length >= 12 && result.FusionMode == 2)
        {
            result.BlendStrength = BitConverter.ToSingle(raw, 8);
        }

        return result;
    }
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
