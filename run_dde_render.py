"""
Renderizador FLIR com DDE (Digital Detail Enhancement) — Hypothesis D.
Pipeline: RAW → [DDE Plateau] → RAW' → Planck → Temp → Level/Span → Palette → JPEG
Compara com e sem DDE para validação SSIM.
"""
import subprocess, struct, os, sys, json
import numpy as np
from PIL import Image

WORKSPACE = r'c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio'
EXIFTOOL = os.path.join(WORKSPACE, 'exiftool', 'exiftool.exe')


def run_exiftool_binary(jpg_path, tag):
    result = subprocess.run(
        [EXIFTOOL, '-b', f'-{tag}', jpg_path],
        capture_output=True, timeout=15
    )
    return result.stdout


def extract_palette_ycrcb(raw_bytes):
    if len(raw_bytes) < 6:
        raise ValueError(f"Palette muito pequena: {len(raw_bytes)} bytes")
    colors = []
    for i in range(0, len(raw_bytes) - 2, 3):
        y, cr, cb = raw_bytes[i], raw_bytes[i+1], raw_bytes[i+2]
        colors.append((y, cr, cb))
    return colors


def ycbcr_to_rgb(y, cr, cb):
    r = y + 1.402 * (cr - 128)
    g = y - 0.344 * (cb - 128) - 0.714 * (cr - 128)
    b = y + 1.772 * (cb - 128)
    return (
        max(0, min(255, round(r))),
        max(0, min(255, round(g))),
        max(0, min(255, round(b))),
    )


def build_lut_256(ycrcb_colors):
    src_count = len(ycrcb_colors)
    lut = []
    for i in range(256):
        si = round(i * (src_count - 1) / 255.0)
        lut.append(ycbcr_to_rgb(*ycrcb_colors[si]))
    return lut


def signal_from_temp(tempC, planck):
    R1, R2, B, F, O = planck
    if R1 <= 0 or R2 <= 0 or B <= 0 or F <= 0 or O is None:
        return tempC
    tk = tempC + 273.15
    if tk <= 0:
        return 0.0
    return R1 / (R2 * (np.exp(B / tk) - F)) - O


def apply_flir_palette_stretch(normalized):
    source = [0.000, 0.038, 0.062, 0.088, 0.113, 0.138, 0.163, 0.188, 0.213, 0.237,
              0.288, 0.388, 0.488, 0.588, 0.688, 0.788, 0.863, 0.913, 1.000]
    target = [0.000, 0.027, 0.058, 0.090, 0.148, 0.233, 0.314, 0.444, 0.511, 0.538,
              0.578, 0.641, 0.704, 0.762, 0.816, 0.865, 0.924, 0.951, 1.000]
    
    normalized = max(0.0, min(1.0, normalized))
    for i in range(len(source) - 1):
        if normalized > source[i + 1]:
            continue
        width = source[i + 1] - source[i]
        if width <= 0:
            return target[i]
        t = (normalized - source[i]) / width
        t = t * t * (3.0 - 2.0 * t)
        return target[i] + (target[i + 1] - target[i]) * t
    return 1.0


def smooth_step(edge0, edge1, value):
    if edge1 <= edge0:
        return 1.0 if value >= edge1 else 0.0
    t = max(0.0, min(1.0, (value - edge0) / (edge1 - edge0)))
    return t * t * (3.0 - 2.0 * t)


# ══════════════════════════════════════════════════════════════════════
#  DDE: Plateau Equalization (Hypothesis D)
#  Idêntico ao C# ApplyDdePlateau — modifica RAW, retorna RAW'
# ══════════════════════════════════════════════════════════════════════

def apply_dde_plateau(raw_values, plateau_percent=2.0, gamma=0.85, knee=0.75):
    """
    Aplica DDE via Plateau Equalization nos valores RAW 14-bit.
    Retorna RAW' (modificado) no mesmo domínio.
    Hypothesis D: RAW → RAW' → Planck → Temp → Level/Span → Palette.
    """
    bins = 256
    h, w = raw_values.shape
    raw_min = raw_values.min()
    raw_max = raw_values.max()
    raw_range = max(raw_max - raw_min, 1)

    # 1. Histograma
    bin_indices = np.clip(((raw_values - raw_min) / raw_range * (bins - 1)).astype(np.int32), 0, bins - 1)
    hist = np.bincount(bin_indices.ravel(), minlength=bins).astype(np.float64)

    # 2. Plateau clipping
    total = h * w
    clip_level = total * plateau_percent / 100.0
    excess = 0.0
    for i in range(bins):
        if hist[i] > clip_level:
            excess += hist[i] - clip_level
            hist[i] = clip_level
    if excess > 0:
        hist += excess / bins

    # 3. CDF normalizada
    cdf = np.cumsum(hist)
    cdf /= cdf[-1]

    # 4. Two-zone curve + mapear de volta ao range RAW
    eq = cdf[bin_indices]
    
    # Two-zone curve: gamma para sombras, linear para highlights
    mask_shadows = eq <= knee
    eq_out = np.where(
        mask_shadows,
        knee * np.power(eq / knee, gamma),
        knee + (1.0 - knee) * (eq - knee) / (1.0 - knee)
    )

    # Mapear de volta ao range RAW original
    raw_prime = (raw_min + eq_out * raw_range + 0.5).astype(np.uint16)
    return raw_prime


def render_thermal(temperatures, lut_rgb, minT, maxT, planck):
    """Pipeline de renderização completo."""
    h, w = temperatures.shape
    pixels = np.zeros((h, w, 3), dtype=np.uint8)
    
    use_signal = planck[0] > 0 and planck[1] > 0 and planck[2] > 0 and planck[3] > 0 and planck[4] is not None
    
    if use_signal:
        signals = np.vectorize(lambda t: signal_from_temp(t, planck))(temperatures)
        minVal = signal_from_temp(minT, planck)
        maxVal = signal_from_temp(maxT, planck)
    else:
        signals = temperatures
        minVal = minT
        maxVal = maxT
    
    if maxVal <= minVal:
        maxVal = minVal + 0.01
    val_range = maxVal - minVal
    
    for y in range(h):
        for x in range(w):
            val = signals[y, x]
            linear_norm = max(0.0, min(1.0, (val - minVal) / val_range))
            normalized = apply_flir_palette_stretch(linear_norm)
            white_boost = smooth_step(0.94, 0.99, normalized)
            normalized = max(0.0, min(1.0, normalized + white_boost * 0.015))
            
            pos = max(0.0, min(1.0, normalized)) * (len(lut_rgb) - 1)
            lo = max(0, min(len(lut_rgb) - 1, int(np.floor(pos))))
            hi = max(0, min(len(lut_rgb) - 1, lo + 1))
            t = pos - lo
            
            c0 = lut_rgb[lo]
            c1 = lut_rgb[hi]
            pixels[y, x, 0] = int(round(c0[0] + (c1[0] - c0[0]) * t))
            pixels[y, x, 1] = int(round(c0[1] + (c1[1] - c0[1]) * t))
            pixels[y, x, 2] = int(round(c0[2] + (c1[2] - c0[2]) * t))
    
    return pixels


def raw_to_temperatures(raw_array, planck, emissivity, reflTemp, camMinC, camMaxC):
    """
    Converte RAW 14-bit → temperaturas (°C) via Planck (forward).
    Inclui correção de emissividade e temperatura refletida.
    Idêntico ao C# ConvertRawToTemperatures.
    """
    R1, R2, B, F, O = planck
    if R1 <= 0 or R2 <= 0 or B <= 0:
        # Sem Planck: escala linear
        raw_f = raw_array.astype(np.float64)
        raw_min = raw_f.min()
        raw_max = raw_f.max()
        if raw_max > raw_min:
            return camMinC + (raw_f - raw_min) / (raw_max - raw_min) * (camMaxC - camMinC)
        return np.full_like(raw_f, camMinC)

    treflK = reflTemp + 273.15
    rawRefl = R1 / (R2 * (np.exp(B / treflK) - F)) - O

    raw_f = raw_array.astype(np.float64)
    rawObj = (raw_f - (1.0 - emissivity) * rawRefl) / emissivity
    correctedRaw = np.maximum(rawObj, 1.0)
    
    denominator = R2 * (correctedRaw + O)
    lnInput = (R1 / np.maximum(denominator, 0.000001)) + F
    lnInput = np.maximum(lnInput, 1.000001)
    
    Tk = B / np.log(lnInput)
    temperatures = Tk - 273.15
    temperatures = np.clip(temperatures, camMinC, camMaxC)
    return temperatures


def process_image(jpg_path, output_path, apply_dde=False, label=""):
    """Processa uma imagem FLIR com pipeline completo, opcionalmente com DDE."""
    
    # 1. Extrair RawThermalImage
    png_bytes = run_exiftool_binary(jpg_path, 'RawThermalImage')
    tmp_png = os.path.join(os.environ['TEMP'], f'thermal_tmp_{os.getpid()}.png')
    with open(tmp_png, 'wb') as f:
        f.write(png_bytes)
    
    thermal_img = Image.open(tmp_png)
    raw_array = np.array(thermal_img, dtype=np.uint16)
    raw_array = raw_array.byteswap()  # FLIR E8xt byte order
    h, w = raw_array.shape
    
    # 2. Metadados
    meta_json = subprocess.run(
        [EXIFTOOL, '-j', '-n', '-G', '-a', jpg_path],
        capture_output=True, timeout=15, text=True
    ).stdout
    meta = json.loads(meta_json)[0]
    
    planckR1 = float(meta.get('APP1:PlanckR1', 0))
    planckR2 = float(meta.get('APP1:PlanckR2', 0))
    planckB  = float(meta.get('APP1:PlanckB', 0))
    planckF  = float(meta.get('APP1:PlanckF', 0))
    planckO  = float(meta.get('APP1:PlanckO', 0))
    planck = (planckR1, planckR2, planckB, planckF, planckO)
    
    emissivity = float(meta.get('MakerNotes:Emissivity', 0.95))
    reflTemp = float(meta.get('APP1:ReflectedApparentTemperature', 20))
    camMinC = float(meta.get('APP1:CameraTemperatureMinClip', -40))
    camMaxC = float(meta.get('APP1:CameraTemperatureMaxClip', 280))
    
    # 3. Paleta embedded
    palette_bytes = run_exiftool_binary(jpg_path, 'Palette')
    ycrcb_colors = extract_palette_ycrcb(palette_bytes)
    lut_256 = build_lut_256(ycrcb_colors)
    
    # 4. DDE (se ativado)
    if apply_dde:
        print(f"  [DDE] Aplicando Plateau Equalization...")
        raw_processed = apply_dde_plateau(raw_array, plateau_percent=2.0, gamma=0.85, knee=0.75)
        print(f"  [DDE] RAW range: {raw_array.min()}-{raw_array.max()} → RAW': {raw_processed.min()}-{raw_processed.max()}")
    else:
        raw_processed = raw_array
    
    # 5. RAW → Temperaturas (Planck)
    temperatures = raw_to_temperatures(raw_processed, planck, emissivity, reflTemp, camMinC, camMaxC)
    print(f"  Temperaturas: min={temperatures.min():.2f}°C, max={temperatures.max():.2f}°C")
    
    # 6. Escala visual
    imgTempMin = float(meta.get('MakerNotes:ImageTemperatureMin', 295))
    imgTempMax = float(meta.get('MakerNotes:ImageTemperatureMax', 317))
    if imgTempMin > 200:
        minT = imgTempMin - 273.15
        maxT = imgTempMax - 273.15
    else:
        minT = imgTempMin
        maxT = imgTempMax
    
    print(f"  Escala: {minT:.2f}°C a {maxT:.2f}°C")
    
    # 7. Renderizar
    pixels = render_thermal(temperatures, lut_256, minT, maxT, planck)
    
    # 8. Salvar
    result_img = Image.fromarray(pixels, 'RGB')
    result_img.save(output_path, 'JPEG', quality=100)
    
    size_kb = os.path.getsize(output_path) / 1024
    print(f"  ✅ {label} salvo: {output_path} ({size_kb:.1f} KB)")
    
    # Cleanup
    try:
        os.remove(tmp_png)
    except:
        pass


def main():
    images = [
        ('FLIR0192.jpg', 'FLIR0192_analise.jpg'),
        ('2.jpg', '2_analise.jpg'),
    ]
    
    for jpg_name, out_name in images:
        jpg_path = os.path.join(WORKSPACE, jpg_name)
        output_path = os.path.join(WORKSPACE, out_name)
        
        if not os.path.exists(jpg_path):
            print(f"⚠️  {jpg_path} não encontrado, pulando...")
            continue
        
        print(f"\n{'='*60}")
        print(f"🔬 Processando {jpg_name} COM DDE...")
        print(f"{'='*60}")
        process_image(jpg_path, output_path, apply_dde=True, label=f"DDE {jpg_name}")
    
    print(f"\n{'='*60}")
    print("✅ Renderização DDE concluída! Execute run_ssim_masked.py para comparar.")
    print(f"{'='*60}")


if __name__ == '__main__':
    main()
