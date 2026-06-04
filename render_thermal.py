"""
Renderiza um termograma FLIR usando o MESMO pipeline do Thermix Studio (pós-correções).
Pipeline: Planck → linearNorm → ApplyFlirPaletteStretch → WhiteBoost → LUT embedded → JPEG
"""
import subprocess, struct, os, sys, json
import numpy as np
from PIL import Image

WORKSPACE = r'c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio'
EXIFTOOL = os.path.join(WORKSPACE, 'exiftool', 'exiftool.exe')


def run_exiftool_binary(jpg_path, tag):
    """Extrai dado binário de uma tag via exiftool -b (sem artefato PowerShell)."""
    result = subprocess.run(
        [EXIFTOOL, '-b', f'-{tag}', jpg_path],
        capture_output=True, timeout=15
    )
    return result.stdout


def extract_palette_ycrcb(raw_bytes):
    """Parse dos bytes YCrCb da paleta (672 bytes = 224 cores × 3)."""
    if len(raw_bytes) < 6:
        raise ValueError(f"Palette muito pequena: {len(raw_bytes)} bytes")
    # A paleta tem 224 cores × 3 bytes = 672 bytes YCrCb
    # Cada Y, Cr, Cb é 1 byte (0-255)
    colors = []
    for i in range(0, len(raw_bytes) - 2, 3):
        y, cr, cb = raw_bytes[i], raw_bytes[i+1], raw_bytes[i+2]
        colors.append((y, cr, cb))
    return colors


def ycbcr_to_rgb(y, cr, cb):
    """Conversão YCrCb → RGB (idêntica ao FlirFffParser)."""
    r = y + 1.402 * (cr - 128)
    g = y - 0.344 * (cb - 128) - 0.714 * (cr - 128)
    b = y + 1.772 * (cb - 128)
    return (
        max(0, min(255, round(r))),
        max(0, min(255, round(g))),
        max(0, min(255, round(b))),
    )


def build_lut_256(ycrcb_colors):
    """Reamostra paleta YCrCb → 256 cores RGB (idêntico ao FlirPaletteConverter)."""
    src_count = len(ycrcb_colors)
    lut = []
    for i in range(256):
        si = round(i * (src_count - 1) / 255.0)
        lut.append(ycbcr_to_rgb(*ycrcb_colors[si]))
    return lut


def signal_from_temp(tempC, planck):
    """Planck: temperatura → sinal (idêntico ao C# SignalFromTemp)."""
    R1, R2, B, F, O = planck
    if R1 <= 0 or R2 <= 0 or B <= 0 or F <= 0 or O is None:
        return tempC  # sem Planck → usa temperatura bruta
    tk = tempC + 273.15
    if tk <= 0:
        return 0.0
    return R1 / (R2 * (np.exp(B / tk) - F)) - O


def apply_flir_palette_stretch(normalized):
    """Stretch não-linear da FLIR (idêntico ao C# ApplyFlirPaletteStretch)."""
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
        t = t * t * (3.0 - 2.0 * t)  # smoothstep
        return target[i] + (target[i + 1] - target[i]) * t
    return 1.0


def smooth_step(edge0, edge1, value):
    """Smoothstep (idêntico ao C#)."""
    if edge1 <= edge0:
        return 1.0 if value >= edge1 else 0.0
    t = max(0.0, min(1.0, (value - edge0) / (edge1 - edge0)))
    return t * t * (3.0 - 2.0 * t)


def render_thermal(temperatures, lut_rgb, minT, maxT, planck):
    """Pipeline de renderização completo (idêntico ao C# atualizado)."""
    h, w = temperatures.shape
    pixels = np.zeros((h, w, 3), dtype=np.uint8)
    
    use_signal = planck[0] > 0 and planck[1] > 0 and planck[2] > 0 and planck[3] > 0 and planck[4] is not None
    
    # Converter temps → sinal (Planck)
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
            
            # linearNorm
            linear_norm = max(0.0, min(1.0, (val - minVal) / val_range))
            
            # ApplyFlirPaletteStretch
            normalized = apply_flir_palette_stretch(linear_norm)
            
            # WhiteBoost (>94%)
            white_boost = smooth_step(0.94, 0.99, normalized)
            normalized = max(0.0, min(1.0, normalized + white_boost * 0.015))
            
            # LUT lookup
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


def main():
    jpg_path = os.path.join(WORKSPACE, 'FLIR0192.jpg')
    output_path = os.path.join(WORKSPACE, 'FLIR0192_render_python.jpg')
    
    print("🔬 Extraindo dados do FLIR0192.jpg...")
    
    # 1. Extrair RawThermalImage (PNG 16-bit)
    png_bytes = run_exiftool_binary(jpg_path, 'RawThermalImage')
    print(f"  RawThermalImage: {len(png_bytes)} bytes")
    
    # Salvar e carregar como imagem
    tmp_png = os.path.join(os.environ['TEMP'], 'thermal_tmp.png')
    with open(tmp_png, 'wb') as f:
        f.write(png_bytes)
    
    thermal_img = Image.open(tmp_png)
    print(f"  Thermal PNG: {thermal_img.size} mode={thermal_img.mode}")
    
    # Converter para array numpy de temperaturas
    raw_array = np.array(thermal_img, dtype=np.uint16)
    
    # FLIR E8xt escreve PNG com byte order trocado (documentado pela ExifTool).
    # Corrigir: byte-swap para little-endian correto.
    raw_array = raw_array.byteswap()
    
    h, w = raw_array.shape
    print(f"  Dimensões: {w}×{h}, raw range (swapped): {raw_array.min()} - {raw_array.max()}")
    
    # 2. Extrair metadados JSON
    meta_json = subprocess.run(
        [EXIFTOOL, '-j', '-n', '-G', '-a', jpg_path],
        capture_output=True, timeout=15, text=True
    ).stdout
    meta = json.loads(meta_json)[0]
    
    # Planck constants
    planckR1 = float(meta.get('APP1:PlanckR1', 0))
    planckR2 = float(meta.get('APP1:PlanckR2', 0))
    planckB  = float(meta.get('APP1:PlanckB', 0))
    planckF  = float(meta.get('APP1:PlanckF', 0))
    planckO  = float(meta.get('APP1:PlanckO', 0))
    planck = (planckR1, planckR2, planckB, planckF, planckO)
    print(f"  Planck: R1={planckR1}, R2={planckR2}, B={planckB}, F={planckF}, O={planckO}")
    
    emissivity = float(meta.get('MakerNotes:Emissivity', 0.95))
    objDist = float(meta.get('APP1:ObjectDistance', 0))
    reflTemp = float(meta.get('APP1:ReflectedApparentTemperature', 20))
    atmTemp = float(meta.get('APP1:AtmosphericTemperature', 20))
    relHum = float(meta.get('APP1:RelativeHumidity', 50))
    irWinTemp = float(meta.get('APP1:IRWindowTemperature', 20))
    irWinTrans = float(meta.get('APP1:IRWindowTransmission', 1))
    
    camMinC = float(meta.get('APP1:CameraTemperatureMinClip', -40))
    camMaxC = float(meta.get('APP1:CameraTemperatureMaxClip', 280))
    
    # 3. Extrair paleta embedded
    palette_bytes = run_exiftool_binary(jpg_path, 'Palette')
    print(f"  Palette raw: {len(palette_bytes)} bytes")
    
    ycrcb_colors = extract_palette_ycrcb(palette_bytes)
    print(f"  Palette YCrCb: {len(ycrcb_colors)} cores")
    print(f"  1ª cor YCrCb: ({ycrcb_colors[0][0]}, {ycrcb_colors[0][1]}, {ycrcb_colors[0][2]})")
    r0, g0, b0 = ycbcr_to_rgb(*ycrcb_colors[0])
    print(f"  1ª cor RGB: ({r0}, {g0}, {b0})")
    rN, gN, bN = ycbcr_to_rgb(*ycrcb_colors[-1])
    print(f"  Última RGB: ({rN}, {gN}, {bN})")
    
    lut_256 = build_lut_256(ycrcb_colors)
    print(f"  LUT 256: {len(lut_256)} cores")
    
    # 4. Converter raw → temperatura usando Planck
    print("\n🌡️  Convertendo raw → temperatura (Planck)...")
    
    # O raw do FLIR E8xt é signal (não temperatura). Precisamos inverter o Planck.
    # signal = R1/(R2*(exp(B/Tk)-F)) - O
    # => exp(B/Tk) = R1/((signal+O)*R2) + F
    # => B/Tk = ln(R1/((signal+O)*R2) + F)
    # => Tk = B / ln(R1/((signal+O)*R2) + F)
    # => Tc = Tk - 273.15
    
    temperatures = np.zeros((h, w), dtype=np.float64)
    
    if planckR1 > 0 and planckR2 > 0 and planckB > 0:
        raw_f = raw_array.astype(np.float64)
        signal_plus_O = raw_f + planckO
        
        # Clamp: sinal+O precisa ser positivo para evitar log de número negativo
        signal_plus_O = np.maximum(signal_plus_O, 1.0)
        
        inner = planckR1 / (planckR2 * signal_plus_O) + planckF
        inner = np.maximum(inner, 1.001)  # exp(B/T) > 1
        
        Tk = planckB / np.log(inner)
        temperatures = Tk - 273.15
        
        # Clamp para range físico da câmera
        temperatures = np.clip(temperatures, camMinC, camMaxC)
    else:
        # Sem Planck: usar escala linear do raw
        raw_f = raw_array.astype(np.float64)
        temp_min_img = float(meta.get('APP1:ImageTemperatureMin', 295))
        temp_max_img = float(meta.get('APP1:ImageTemperatureMax', 317))
        # Converter de Kelvin para Celsius
        if temp_min_img > 200:  # está em Kelvin
            temp_min_img -= 273.15
            temp_max_img -= 273.15
        raw_min = raw_f.min()
        raw_max = raw_f.max()
        if raw_max > raw_min:
            temperatures = temp_min_img + (raw_f - raw_min) / (raw_max - raw_min) * (temp_max_img - temp_min_img)
        else:
            temperatures = np.full_like(raw_f, temp_min_img)
    
    print(f"  Temperaturas: min={temperatures.min():.2f}°C, max={temperatures.max():.2f}°C")
    
    # 5. Escala visual — usar ImageTemperatureMin/Max do metadata (MakerNotes, em Kelvin)
    imgTempMin = float(meta.get('MakerNotes:ImageTemperatureMin', 295))
    imgTempMax = float(meta.get('MakerNotes:ImageTemperatureMax', 317))
    if imgTempMin > 200:
        minT = imgTempMin - 273.15
        maxT = imgTempMax - 273.15
    else:
        minT = imgTempMin
        maxT = imgTempMax
    
    print(f"  Escala: {minT:.2f}°C a {maxT:.2f}°C")
    
    # 6. Aplicar correção atmosférica (simplificada)
    # A câmera FLIR já aplica isso internamente nos raw values
    # Mas podemos refinar se necessário
    
    # 7. Renderizar!
    print("\n🎨 Renderizando com pipeline atualizado...")
    pixels = render_thermal(temperatures, lut_256, minT, maxT, planck)
    
    # 8. Salvar como JPEG
    result_img = Image.fromarray(pixels, 'RGB')
    result_img.save(output_path, 'JPEG', quality=100)
    
    size_kb = os.path.getsize(output_path) / 1024
    print(f"\n✅ Render salvo: {output_path} ({size_kb:.1f} KB)")
    print(f"   Dimensões: {w}×{h}")
    print(f"   Pipeline: Planck → linearNorm → FlirPaletteStretch → WhiteBoost → LUT(embedded 224c)")


if __name__ == '__main__':
    main()
