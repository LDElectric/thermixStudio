"""
Comparador de fidelidade térmica — com crop de bordas (overlay UI).
Remove 8% das bordas antes de comparar, eliminando interferência
da escala, logo, spot meter e barra de cores da câmera.
"""
import sys
import os
import cv2
import numpy as np
from scipy.stats import wasserstein_distance

WORKSPACE = r"c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio"
sys.path.insert(0, WORKSPACE)

from deepseek_python_20260602_3848b7 import (
    brightness_contrast_stats,
    export_full_report_to_txt,
)

def crop_thermal_roi(img, margin_pct=0.08):
    """Remove bordas com UI da câmera (escala, logo, spot, barra)."""
    h, w = img.shape[:2]
    y0 = int(h * margin_pct)
    y1 = int(h * (1.0 - margin_pct))
    x0 = int(w * margin_pct)
    x1 = int(w * (1.0 - margin_pct))
    return img[y0:y1, x0:x1]

def compute_histogram_stats(img):
    hsv = cv2.cvtColor(img, cv2.COLOR_RGB2HSV)
    v_channel = hsv[:, :, 2]
    hist_r = cv2.calcHist([img], [0], None, [256], [0, 256])
    hist_g = cv2.calcHist([img], [1], None, [256], [0, 256])
    hist_b = cv2.calcHist([img], [2], None, [256], [0, 256])
    hist_v = cv2.calcHist([v_channel], [0], None, [256], [0, 256])
    for h in [hist_r, hist_g, hist_b, hist_v]:
        h /= h.sum() if h.sum() > 0 else 1.0
    return hist_r, hist_g, hist_b, hist_v

def compare_histograms(hist_orig, hist_render, method='correlation'):
    methods = {
        'correlation': cv2.HISTCMP_CORREL,
        'chi_square': cv2.HISTCMP_CHISQR,
        'intersection': cv2.HISTCMP_INTERSECT,
        'bhattacharyya': cv2.HISTCMP_BHATTACHARYYA
    }
    return cv2.compareHist(hist_orig, hist_render, methods[method])

# ─── Pares ──────────────────────────────────────────────────
PAIRS = [
    ("FLIR0192",
     os.path.join(WORKSPACE, "FLIR0192.jpg"),
     os.path.join(WORKSPACE, "FLIR0192_render_limpo.jpg")),
    ("2.jpg",
     os.path.join(WORKSPACE, "2.jpg"),
     os.path.join(WORKSPACE, "2_render_limpo.jpg")),
]

CROP_MARGIN = 0.08  # 8% das bordas removidas

for name, path_orig, path_render in PAIRS:
    print(f"\n{'='*70}")
    print(f"  {name}  |  crop {CROP_MARGIN*100:.0f}% borders")
    print(f"{'='*70}")

    if not os.path.exists(path_orig):
        print(f"  ERRO: Original nao encontrado: {path_orig}")
        continue
    if not os.path.exists(path_render):
        print(f"  ERRO: Render nao encontrado: {path_render}")
        continue

    img_orig = cv2.cvtColor(cv2.imread(path_orig), cv2.COLOR_BGR2RGB)
    img_render = cv2.cvtColor(cv2.imread(path_render), cv2.COLOR_BGR2RGB)

    # Crop das bordas com UI
    img_orig_crop = crop_thermal_roi(img_orig, CROP_MARGIN)
    img_render_crop = crop_thermal_roi(img_render, CROP_MARGIN)

    print(f"  Full size:  {img_orig.shape[1]}x{img_orig.shape[0]}")
    print(f"  Crop size:  {img_orig_crop.shape[1]}x{img_orig_crop.shape[0]}")

    # Brilho (canal V)
    stats_orig = brightness_contrast_stats(img_orig_crop)
    stats_render = brightness_contrast_stats(img_render_crop)

    print(f"\n  [BRILHO - ROI termica (sem UI)]")
    print(f"  {'Metrica':<10} {'Original':>10} {'Render':>10} {'Diff':>10}")
    for key in ['mean', 'std', 'median', 'min', 'max']:
        diff = stats_render[key] - stats_orig[key]
        print(f"  {key:<10} {stats_orig[key]:>10.2f} {stats_render[key]:>10.2f} {diff:>+10.2f}")

    # Histogramas
    h_r_o, h_g_o, h_b_o, h_v_o = compute_histogram_stats(img_orig_crop)
    h_r_r, h_g_r, h_b_r, h_v_r = compute_histogram_stats(img_render_crop)

    print(f"\n  [CORRELACAO HISTOGRAMAS - ROI termica]")
    print(f"  {'Canal':<12} {'Correlacao':>12} {'Intersecao':>12} {'Bhattach.':>12} {'Wasserstein':>12}")
    for ch_name, (h_o, h_r) in [('R',(h_r_o,h_r_r)),('G',(h_g_o,h_g_r)),('B',(h_b_o,h_b_r)),('Lum',(h_v_o,h_v_r))]:
        corr = compare_histograms(h_o, h_r, 'correlation')
        inter = compare_histograms(h_o, h_r, 'intersection')
        bhat = compare_histograms(h_o, h_r, 'bhattacharyya')
        wass = wasserstein_distance(h_o.flatten(), h_r.flatten())
        print(f"  {ch_name:<12} {corr:>12.6f} {inter:>12.4f} {bhat:>12.6f} {wass:>12.6f}")

    # Correlação média
    avg_corr = (compare_histograms(h_r_o, h_r_r, 'correlation') +
                compare_histograms(h_g_o, h_g_r, 'correlation') +
                compare_histograms(h_b_o, h_b_r, 'correlation')) / 3.0
    print(f"\n  >>> Correlacao RGB media: {avg_corr:.4f}")

    # Salva crops para inspeção visual
    out_dir = os.path.dirname(path_orig)
    cv2.imwrite(os.path.join(out_dir, f"{name}_crop_orig.jpg"),
                cv2.cvtColor(img_orig_crop, cv2.COLOR_RGB2BGR))
    cv2.imwrite(os.path.join(out_dir, f"{name}_crop_render.jpg"),
                cv2.cvtColor(img_render_crop, cv2.COLOR_RGB2BGR))

print(f"\n{'='*70}")
print("Comparacao concluida. Crops salvos como *_crop_*.jpg")
