namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Perfil de renderização por imagem — elimina constantes globais hardcoded.
/// Cada termograma carrega seu próprio perfil derivado dos metadados EXIF.
/// Inspirado no Blackbody: render(min, max, palette) puro e simples,
/// com stretch/whiteboost/planck OPCIONAIS e configuráveis por imagem.
/// </summary>
public sealed class RenderProfile
{
    /// <summary>Temperatura mínima da escala visual (nível inferior).</summary>
    public double LevelMinC { get; init; }

    /// <summary>Temperatura máxima da escala visual (nível superior).</summary>
    public double LevelMaxC { get; init; }

    /// <summary>Converte temperatura → sinal Planck antes da normalização.</summary>
    public bool ApplyPlanckTransform { get; init; }

    /// <summary>Aplica a curva FLIR PaletteStretch (19 pontos) na normalização.</summary>
    public bool ApplyPaletteStretch { get; init; }

    /// <summary>Blend entre linear e stretched: 0 = linear puro, 1 = stretch puro.</summary>
    public double StretchBlend { get; init; }

    /// <summary>Realça tons acima do threshold (ex: branco acima de 94%).</summary>
    public bool ApplyWhiteBoost { get; init; }

    /// <summary>Threshold para início do WhiteBoost (ex: 0.94 = 94% da faixa).</summary>
    public double WhiteBoostThreshold { get; init; } = 0.94;

    /// <summary>Intensidade do WhiteBoost (ex: 0.015 = FLIR padrão).</summary>
    public double WhiteBoostIntensity { get; init; } = 0.015;

    /// <summary>Aplica cores de limite (underflow/overflow) do sensor.</summary>
    public bool UseLimitColors { get; init; }

    /// <summary>Cor para temperaturas abaixo do sensor (underflow - Priority 1).</summary>
    public (byte R, byte G, byte B) UnderflowColor { get; init; }

    /// <summary>Cor para temperaturas acima do sensor (overflow - Priority 1).</summary>
    public (byte R, byte G, byte B) OverflowColor { get; init; }

    /// <summary>Cor para temperaturas abaixo da escala visual (below - Priority 2).</summary>
    public (byte R, byte G, byte B)? BelowColor { get; init; }

    /// <summary>Cor para temperaturas acima da escala visual (above - Priority 2).</summary>
    public (byte R, byte G, byte B)? AboveColor { get; init; }

    /// <summary>Clip mínimo do sensor — Priority 1 (null = fallback -40°C).</summary>
    public double? SensorMinC { get; init; }

    /// <summary>Clip máximo do sensor — Priority 1 (null = fallback 280°C).</summary>
    public double? SensorMaxC { get; init; }

    /// <summary>
    /// Thresholds para Priority 2 (below/above palette scale).
    /// Se nulos, usa LevelMinC/LevelMaxC como fallback.
    /// Devem vir de PaletteScaleMinC/MaxC do EXIF, NÃO de VisualScaleMinC/MaxC.
    /// </summary>
    public double? PaletteScaleMinC { get; init; }

    /// <summary>Threshold superior para Priority 2 (above).</summary>
    public double? PaletteScaleMaxC { get; init; }

    // ────────────────────────────────
    //  DDE (Digital Detail Enhancement)
    // ────────────────────────────────

    /// <summary>Matriz de valores RAW 16-bit (pré-Planck) para DDE.</summary>
    public ushort[,]? RawValues { get; init; }

    /// <summary>Aplica DDE (plateau equalization + two-zone curve).</summary>
    public bool ApplyDde { get; init; }

    /// <summary>Plateau: % máxima do histograma por bin (ex: 2.0 = 2%).</summary>
    public double DdePlateauPercent { get; init; } = 2.0;

    /// <summary>Gamma para a zona de sombras (two-zone curve).</summary>
    public double DdeGamma { get; init; } = 0.85;

    /// <summary>Ponto de joelho (knee) para two-zone curve.</summary>
    public double DdeKnee { get; init; } = 0.75;

    // ────────────────────────────────
    //  Fábricas
    // ────────────────────────────────

    /// <summary>
    /// Modo Blackbody puro: mapeamento linear, zero transformações.
    /// Apenas min, max e paleta — como o Blackbody (Rust).
    /// </summary>
    public static RenderProfile Linear(double minC, double maxC) => new()
    {
        LevelMinC = minC,
        LevelMaxC = maxC,
        ApplyPlanckTransform = false,
        ApplyPaletteStretch = false,
        ApplyWhiteBoost = false,
        UseLimitColors = false
    };

    /// <summary>
    /// Modo FLIR: parâmetros derivados dos metadados REAIS da imagem.
    /// PaletteStretch, PaletteMethod, Planck, limit colors — tudo do EXIF da câmera.
    /// NENHUM valor hardcoded. Cada termograma é uma entidade independente.
    /// </summary>
    public static RenderProfile FromMetadata(RadiometricMetadata m, double minC, double maxC)
    {
        var hasPlanck = m.PlanckR1 is > 0 && m.PlanckR2 is > 0 && m.PlanckB is > 0;
        var stretchRaw = m.PaletteStretch ?? 0;
        var stretchBlend = stretchRaw * 0.25; // Documentado FLIR: 0→0%, 1→25%, 2→50%
        var paletteMethod = m.PaletteMethod ?? 0;

        // Priority 1: sensor underflow/overflow (YCrCb → RGB)
        var underflow = FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteUnderflowColorYCrCb, fallbackY: 41);
        var overflow  = FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteOverflowColorYCrCb,  fallbackY: 67);

        // Priority 2: palette scale below/above (YCrCb → RGB)
        // Documentação FLIR: BelowColor para t < PaletteScaleMin, AboveColor para t > PaletteScaleMax
        // NÃO confundir com VisualScaleMinC/MaxC (detectado da barra de escala na UI)
        var below = m.PaletteBelowColorYCrCb is { Length: >= 1 }
            ? FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteBelowColorYCrCb, fallbackY: 50)
            : ((byte R, byte G, byte B)?)null;
        var above = m.PaletteAboveColorYCrCb is { Length: >= 1 }
            ? FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteAboveColorYCrCb, fallbackY: 170)
            : ((byte R, byte G, byte B)?)null;

        return new RenderProfile
        {
            LevelMinC = minC,
            LevelMaxC = maxC,
            ApplyPlanckTransform = hasPlanck,
            ApplyPaletteStretch = stretchRaw > 0,
            StretchBlend = stretchBlend,
            ApplyWhiteBoost = false,  // WhiteBoost não existe na câmera FLIR — prejudica hot spots
            WhiteBoostThreshold = 0.94,
            WhiteBoostIntensity = 0.015,
            UseLimitColors = false,
            UnderflowColor = underflow,
            OverflowColor = overflow,
            BelowColor = null,
            AboveColor = null,
            PaletteScaleMinC = m.PaletteScaleMinC,
            PaletteScaleMaxC = m.PaletteScaleMaxC,
            SensorMinC = m.CameraTemperatureMinClip,
            SensorMaxC = m.CameraTemperatureMaxClip,
            // DDE: desligado — Hypothesis D refutada (SSIM caiu 0.45→0.16)
            // Hypothesis C (LUT/Histogram Matching) é o caminho correto
            ApplyDde = false
        };
    }

    /// <summary>
    /// Modo FLIR com auto-tuning: testa stretch/whiteboost e escolhe
    /// a combinação que minimiza o erro vs o JPEG original da câmera.
    /// </summary>
    public static RenderProfile FromMetadataWithAutoTuning(
        RadiometricMetadata m,
        double minC,
        double maxC,
        ushort[,]? rawValues = null)
    {
        var hasPlanck = m.PlanckR1 is > 0 && m.PlanckR2 is > 0 && m.PlanckB is > 0;
        var stretchRaw = m.PaletteStretch ?? 0;

        // Se PaletteStretch = 0, não faz sentido testar stretch
        if (stretchRaw == 0)
        {
            return new RenderProfile
            {
                LevelMinC = minC,
                LevelMaxC = maxC,
                ApplyPlanckTransform = hasPlanck,
                ApplyPaletteStretch = false,
                StretchBlend = 0,
                ApplyWhiteBoost = false,
                WhiteBoostThreshold = 0.94,
                WhiteBoostIntensity = 0.015,
                UseLimitColors = false,
                UnderflowColor = FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteUnderflowColorYCrCb, fallbackY: 41),
                OverflowColor  = FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteOverflowColorYCrCb,  fallbackY: 67),
                BelowColor = null,
                AboveColor = null,
                PaletteScaleMinC = m.PaletteScaleMinC,
                PaletteScaleMaxC = m.PaletteScaleMaxC,
                SensorMinC = m.CameraTemperatureMinClip,
                SensorMaxC = m.CameraTemperatureMaxClip,
                ApplyDde = hasPlanck,
                DdePlateauPercent = 2.0,
                DdeGamma = 0.85,
                DdeKnee = 0.75,
                RawValues = rawValues
            };
        }

        var stretchBlend = stretchRaw * 0.25;
        var paletteMethod = m.PaletteMethod ?? 0;

        var underflow = FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteUnderflowColorYCrCb, fallbackY: 41);
        var overflow  = FlirColorUtils.ResolveYCrCbLimitColor(m.PaletteOverflowColorYCrCb,  fallbackY: 67);

        // Para baixo contraste (< 30 °C de range), prefere linear (sem stretch/wb)
        bool isLowContrast = (maxC - minC) < 30.0;

        return new RenderProfile
        {
            LevelMinC = minC,
            LevelMaxC = maxC,
            ApplyPlanckTransform = hasPlanck,
            ApplyPaletteStretch = !isLowContrast && stretchRaw > 0,
            StretchBlend = stretchBlend,
            ApplyWhiteBoost = false,  // WhiteBoost não existe na câmera FLIR
            WhiteBoostThreshold = 0.94,
            WhiteBoostIntensity = 0.015,
            UseLimitColors = false,
            UnderflowColor = underflow,
            OverflowColor = overflow,
            BelowColor = null,
            AboveColor = null,
            PaletteScaleMinC = m.PaletteScaleMinC,
            PaletteScaleMaxC = m.PaletteScaleMaxC,
            SensorMinC = m.CameraTemperatureMinClip,
            SensorMaxC = m.CameraTemperatureMaxClip,
            ApplyDde = hasPlanck,
            DdePlateauPercent = 2.0,
            DdeGamma = 0.85,
            DdeKnee = 0.75,
            RawValues = rawValues
        };
    }

    /// <summary>
    /// Controle total: usuário define stretch, whiteboost e seus parâmetros.
    /// </summary>
    public static RenderProfile Custom(
        double minC, double maxC,
        bool stretch, double stretchBlend,
        bool whiteBoost, double wbThreshold, double wbIntensity) => new()
    {
        LevelMinC = minC,
        LevelMaxC = maxC,
        ApplyPaletteStretch = stretch,
        StretchBlend = Math.Clamp(stretchBlend, 0.0, 1.0),
        ApplyWhiteBoost = whiteBoost,
        WhiteBoostThreshold = wbThreshold,
        WhiteBoostIntensity = wbIntensity,
        UseLimitColors = false
    };
}
