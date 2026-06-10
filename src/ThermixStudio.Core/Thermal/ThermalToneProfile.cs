namespace ThermixStudio.Core.Thermal;

/// <summary>
/// Perfil tonal extraído do JPEG da câmera via reverse-lookup.
/// A Curve256 mapeia um índice físico normalizado [0..1] para um
/// índice ajustado [0..1] que replica o tone mapping do JPEG.
///
/// Camada 2 do pipeline de 3 camadas (Física → Tone → LUT).
/// </summary>
public sealed class ThermalToneProfile
{
    /// <summary>Curva tonal de 256 entradas. Índice = posição física, valor = posição ajustada.</summary>
    public float[] Curve256 { get; }

    public bool IsValid { get; }

    public ThermalToneProfile(float[] curve)
    {
        Curve256 = curve ?? throw new ArgumentNullException(nameof(curve));
        IsValid = curve.Length == 256 && curve[0] == 0f && curve[255] == 1f;
    }

    private ThermalToneProfile()
    {
        Curve256 = [];
        IsValid = false;
    }

    /// <summary>Perfil identidade (sem alteração tonal).</summary>
    public static ThermalToneProfile Identity { get; } = new();

    /// <summary>
    /// Mapeia um índice físico normalizado [0..1] para o índice ajustado
    /// usando interpolação linear sobre a Curve256.
    /// </summary>
    public double Map(double normalized)
    {
        if (!IsValid || Curve256.Length != 256)
            return normalized;

        double pos = Math.Clamp(normalized, 0.0, 1.0) * 255.0;
        int lo = Math.Clamp((int)pos, 0, 255);
        int hi = Math.Clamp(lo + 1, 0, 255);
        double frac = pos - lo;

        return Curve256[lo] + (Curve256[hi] - Curve256[lo]) * frac;
    }
}
