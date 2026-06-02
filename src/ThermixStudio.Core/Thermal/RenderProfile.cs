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

        // ══════════════════════════════════════════════════════════════
        // TESTE: Desliga stretch e whiteboost para isolar causa do shift
        // Se as cores melhorarem, o problema está nestes parâmetros.
        // ══════════════════════════════════════════════════════════════
        return new RenderProfile
        {
            LevelMinC = minC,
            LevelMaxC = maxC,
            ApplyPlanckTransform = hasPlanck,
            ApplyPaletteStretch = false,     // TESTE: desligado
            StretchBlend = 0.0,              // TESTE: zerado
            ApplyWhiteBoost = false,          // TESTE: desligado
            WhiteBoostThreshold = 0.94,
            WhiteBoostIntensity = 0.0,
            UseLimitColors = FlirColorUtils.UsesFlirLimitColors(m),
            UnderflowColor = underflow,
            OverflowColor = overflow,
            BelowColor = below,
            AboveColor = above,
            PaletteScaleMinC = m.PaletteScaleMinC,  // Threshold para Priority 2 below
            PaletteScaleMaxC = m.PaletteScaleMaxC,  // Threshold para Priority 2 above
            SensorMinC = m.CameraTemperatureMinClip,
            SensorMaxC = m.CameraTemperatureMaxClip
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
