"""
Fase 2 — Histogram Matching: aprende LUT temperatura→cor do JPEG original.
Pipeline: RAW → Planck → Temp → LUT(aprendida do JPEG) → Render
Bypass total do DDE/Palette/Stretch.
"""
import subprocess, os, json
import numpy as np
from PIL import Image
import cv2
from skimage.metrics import structural_similarity as ssim

WORKSPACE = r'c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio'
EXIFTOOL = os.path.join(WORKSPACE, 'exiftool', 'exiftool.exe')

# ══════════════════════════════════════════════════════
# Máscara overlay FLIR E8xt (mesma do TemperatureColorLut.cs)
# ══════════════════════════════════════════════════════
def is_overlay(x, y, w, h):
    """True se pixel está sob overlay da câmera."""
    cx, cy = w // 2, h // 2
    if y < 22 or y >= h - 38: return True
    if x < 60 and y < 42: return True  # temp sup-esq
    if x < 45 and y >= h - 38: return True  # logo
    if x >= w - 32: return True  # escala dir
    # Crosshair
    if abs(y - cy) <= 2 and abs(x - cx) <= 35: return True
    if abs(x - cx) <= 2 and abs(y - cy) <= 35: return True
    if (x - cx)**2 + (y - cy)**2 <= 64: return True
    return False


def run_exiftool_binary(jpg_path, tag):
    result = subprocess.run(
        [EXIFTOOL, '-b', f'-{tag}', jpg_path],
        capture_output=True, timeout=15
    )
    return result.stdout


def raw_to_temperatures(raw_array, planckR1, planckR2, planckB, planckF, planckO,
                         emissivity=0.95, reflTemp=20.0, camMinC=-40, camMaxC=280):
    """Planck forward: RAW 14-bit → °C com correção de emissividade."""
    if planckR1 <= 0 or planckR2 <= 0 or planckB <= 0:
        raw_f = raw_array.astype(np.float64)
        rmin, rmax = raw_f.min(), raw_f.max()
        return camMinC + (raw_f - rmin) / max(rmax - rmin, 1) * (camMaxC - camMinC)

    treflK = reflTemp + 273.15
    rawRefl = planckR1 / (planckR2 * (np.exp(planckB / treflK) - planckF)) - planckO

    raw_f = raw_array.astype(np.float64)
    rawObj = (raw_f - (1.0 - emissivity) * rawRefl) / emissivity
    correctedRaw = np.maximum(rawObj, 1.0)
    denominator = planckR2 * (correctedRaw + planckO)
    lnInput = (planckR1 / np.maximum(denominator, 0.000001)) + planckF
    lnInput = np.maximum(lnInput, 1.000001)
    Tk = planckB / np.log(lnInput)
    return np.clip(Tk - 273.15, camMinC, camMaxC)


def build_lut(temperatures, jpeg_bgr, width, height, num_bins=4096):
    """
    Constrói LUT temperatura→cor amostrando pixels LIMPOS do JPEG.
    Usa média por bin + smooth. Idêntico ao C# TemperatureColorLut.Build().
    """
    h, w = temperatures.shape
    
    sum_r = np.zeros(num_bins, dtype=np.float64)
    sum_g = np.zeros(num_bins, dtype=np.float64)
    sum_b = np.zeros(num_bins, dtype=np.float64)
    counts = np.zeros(num_bins, dtype=np.int32)
    
    t_min = temperatures.min()
    t_max = temperatures.max()
    t_range = max(t_max - t_min, 0.01)
    
    skipped = 0
    sampled = 0
    
    for y in range(h):
        for x in range(w):
            if is_overlay(x, y, w, h):
                skipped += 1
                continue
            t = temperatures[y, x]
            if not np.isfinite(t):
                continue
            sampled += 1
            
            bin_idx = int((t - t_min) / t_range * (num_bins - 1))
            if bin_idx < 0 or bin_idx >= num_bins:
                continue
            
            # JPEG está em BGR (OpenCV)
            b = int(jpeg_bgr[y, x, 0])
            g = int(jpeg_bgr[y, x, 1])
            r = int(jpeg_bgr[y, x, 2])
            
            sum_r[bin_idx] += r
            sum_g[bin_idx] += g
            sum_b[bin_idx] += b
            counts[bin_idx] += 1
    
    lut_r = np.zeros(num_bins, dtype=np.uint8)
    lut_g = np.zeros(num_bins, dtype=np.uint8)
    lut_b = np.zeros(num_bins, dtype=np.uint8)
    
    populated = 0
    for i in range(num_bins):
        if counts[i] > 0:
            lut_r[i] = int(sum_r[i] / counts[i] + 0.5)
            lut_g[i] = int(sum_g[i] / counts[i] + 0.5)
            lut_b[i] = int(sum_b[i] / counts[i] + 0.5)
            populated += 1
    
    # Fill empty bins (nearest neighbor)
    for arr in [lut_r, lut_g, lut_b]:
        # Forward fill
        last_val = 0
        for i in range(num_bins):
            if arr[i] != 0:
                last_val = arr[i]
            else:
                arr[i] = last_val
        # Backward fill (for leading zeros)
        for i in range(num_bins - 1, -1, -1):
            if arr[i] == 0:
                arr[i] = last_val
            else:
                last_val = arr[i]
    
    # Smooth (weighted moving average, radius=1)
    for arr in [lut_r, lut_g, lut_b]:
        smoothed = arr.copy().astype(np.int32)
        for i in range(num_bins):
            total_w = 0
            total_v = 0
            for r in [-1, 0, 1]:
                idx = max(0, min(num_bins - 1, i + r))
                w = 2 - abs(r)  # weights: 1, 2, 1
                total_v += int(arr[idx]) * w
                total_w += w
            smoothed[i] = total_v // total_w
        arr[:] = np.clip(smoothed, 0, 255).astype(np.uint8)
    
    print(f"  LUT: {num_bins} bins, {populated}/{num_bins} populated, "
          f"skipped={skipped}, sampled={sampled}, "
          f"range={t_min:.2f}~{t_max:.2f}°C")
    
    return lut_r, lut_g, lut_b, t_min, t_max


def apply_lut(temperatures, lut_r, lut_g, lut_b, t_min, t_max, display_min, display_max):
    """Aplica LUT temperatura→cor com interpolação linear."""
    h, w = temperatures.shape
    pixels = np.zeros((h, w, 3), dtype=np.uint8)
    num_bins = len(lut_r)
    display_range = max(display_max - display_min, 0.01)
    
    for y in range(h):
        for x in range(w):
            t = temperatures[y, x]
            if not np.isfinite(t):
                continue
            
            if t <= display_min:
                pixels[y, x] = [0, 0, 0]  # preto (below)
                continue
            if t >= display_max:
                pixels[y, x] = [255, 255, 255]  # branco (above)
                continue
            
            pos = (t - display_min) / display_range * (num_bins - 1)
            lo = max(0, min(num_bins - 1, int(pos)))
            hi = max(0, min(num_bins - 1, lo + 1))
            frac = pos - lo
            
            # BGR order (OpenCV) — cast to int to avoid uint8 overflow
            r_lo, r_hi = int(lut_r[lo]), int(lut_r[hi])
            g_lo, g_hi = int(lut_g[lo]), int(lut_g[hi])
            b_lo, b_hi = int(lut_b[lo]), int(lut_b[hi])
            pixels[y, x, 0] = max(0, min(255, int(b_lo + (b_hi - b_lo) * frac + 0.5)))
            pixels[y, x, 1] = max(0, min(255, int(g_lo + (g_hi - g_lo) * frac + 0.5)))
            pixels[y, x, 2] = max(0, min(255, int(r_lo + (r_hi - r_lo) * frac + 0.5)))
    
    return pixels


def process_image(jpg_path, output_path, num_bins=4096):
    """Pipeline completo: Planck → LUT(aprendida do JPEG) → Render."""
    
    # 1. Extrair RawThermalImage
    png_bytes = run_exiftool_binary(jpg_path, 'RawThermalImage')
    tmp_png = os.path.join(os.environ['TEMP'], f'lut_tmp_{os.getpid()}.png')
    with open(tmp_png, 'wb') as f:
        f.write(png_bytes)
    
    thermal_img = Image.open(tmp_png)
    raw_array = np.array(thermal_img, dtype=np.uint16)
    raw_array = raw_array.byteswap()
    h, w = raw_array.shape
    
    # 2. Metadados Planck
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
    emissivity = float(meta.get('MakerNotes:Emissivity', 0.95))
    reflTemp = float(meta.get('APP1:ReflectedApparentTemperature', 20))
    camMinC = float(meta.get('APP1:CameraTemperatureMinClip', -40))
    camMaxC = float(meta.get('APP1:CameraTemperatureMaxClip', 280))
    
    # 3. RAW → Temperaturas (Planck)
    temperatures = raw_to_temperatures(
        raw_array, planckR1, planckR2, planckB, planckF, planckO,
        emissivity, reflTemp, camMinC, camMaxC
    )
    print(f"  Temperaturas: {temperatures.min():.2f}~{temperatures.max():.2f}°C")
    
    # 4. Carregar JPEG original como ground truth
    jpeg_bgr = cv2.imread(jpg_path)
    if jpeg_bgr is None:
        raise FileNotFoundError(f"Não foi possível carregar {jpg_path}")
    
    # 5. Construir LUT a partir do JPEG
    print(f"  Construindo LUT ({num_bins} bins)...")
    lut_r, lut_g, lut_b, t_min, t_max = build_lut(
        temperatures, jpeg_bgr, w, h, num_bins
    )
    
    # 6. Escala de display
    imgTempMin = float(meta.get('MakerNotes:ImageTemperatureMin', 295))
    imgTempMax = float(meta.get('MakerNotes:ImageTemperatureMax', 317))
    if imgTempMin > 200:
        display_min = imgTempMin - 273.15
        display_max = imgTempMax - 273.15
    else:
        display_min = imgTempMin
        display_max = imgTempMax
    
    print(f"  Display: {display_min:.2f}~{display_max:.2f}°C")
    
    # 7. Aplicar LUT
    pixels = apply_lut(temperatures, lut_r, lut_g, lut_b, t_min, t_max,
                       display_min, display_max)
    
    # 8. Salvar
    result_img = Image.fromarray(cv2.cvtColor(pixels, cv2.COLOR_BGR2RGB))
    result_img.save(output_path, 'JPEG', quality=100)
    
    size_kb = os.path.getsize(output_path) / 1024
    print(f"  ✅ Salvo: {output_path} ({size_kb:.1f} KB)")
    
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
        print(f"🔬 Fase 2 - Histogram Matching: {jpg_name}")
        print(f"{'='*60}")
        process_image(jpg_path, output_path, num_bins=4096)
    
    print(f"\n{'='*60}")
    print("✅ Fase 2 concluída! Execute run_ssim_masked.py para comparar.")
    print(f"{'='*60}")


if __name__ == '__main__':
    main()
