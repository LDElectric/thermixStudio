namespace ThermixStudio.Core.Thermal;

/// <summary>
/// LUT de calibração por imagem: 256 entradas por canal (R,G,B).
/// Construída 1x na importação comparando o render Linear com o JPEG original.
/// Aplicada em todo render subsequente — instantâneo, sem artefatos.
/// </summary>
public sealed class CalibratedLut
{
    public byte[] LutR { get; } = new byte[256];
    public byte[] LutG { get; } = new byte[256];
    public byte[] LutB { get; } = new byte[256];

    public bool IsValid { get; private set; }

    /// <summary>Aplica a LUT nos pixels BGRA (modifica in-place).</summary>
    public void Apply(byte[] bgraPixels)
    {
        if (!IsValid) return;

        for (int i = 0; i < bgraPixels.Length; i += 4)
        {
            bgraPixels[i]     = LutB[bgraPixels[i]];     // B
            bgraPixels[i + 1] = LutG[bgraPixels[i + 1]]; // G
            bgraPixels[i + 2] = LutR[bgraPixels[i + 2]]; // R
        }
    }

    /// <summary>Marca a LUT como válida após construção.</summary>
    public void Validate() => IsValid = true;
}
