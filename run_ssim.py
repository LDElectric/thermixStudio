"""
Comparador com SSIM (Structural Similarity) — compara PADRÕES espaciais,
não apenas distribuição de cores. Muito mais adequado para termogramas.
"""
import sys, os, cv2, numpy as np
from skimage.metrics import structural_similarity as ssim

WORKSPACE = r"c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio"

def crop_thermal_roi(img, margin_pct=0.08):
    h, w = img.shape[:2]
    y0, y1 = int(h*margin_pct), int(h*(1-margin_pct))
    x0, x1 = int(w*margin_pct), int(w*(1-margin_pct))
    return img[y0:y1, x0:x1]

PAIRS = [
    ("FLIR0192", "FLIR0192.jpg", "FLIR0192_render_limpo.jpg"),
    ("2.jpg", "2.jpg", "2_render_limpo.jpg"),
]

for name, orig_fn, render_fn in PAIRS:
    orig = cv2.cvtColor(cv2.imread(os.path.join(WORKSPACE, orig_fn)), cv2.COLOR_BGR2RGB)
    rend = cv2.cvtColor(cv2.imread(os.path.join(WORKSPACE, render_fn)), cv2.COLOR_BGR2RGB)

    # Crop
    orig_c = crop_thermal_roi(orig, 0.08)
    rend_c = crop_thermal_roi(rend, 0.08)

    # SSIM em cada canal + grayscale
    gray_orig = cv2.cvtColor(orig_c, cv2.COLOR_RGB2GRAY)
    gray_rend = cv2.cvtColor(rend_c, cv2.COLOR_RGB2GRAY)
    ssim_gray = ssim(gray_orig, gray_rend, data_range=255)

    print(f"\n{'='*60}")
    print(f"  {name}  |  SSIM (structural similarity)")
    print(f"{'='*60}")
    print(f"  SSIM Grayscale: {ssim_gray:.4f}  (1.0 = identico)")

    for ch_name, ch_idx in [('R',0),('G',1),('B',2)]:
        s = ssim(orig_c[:,:,ch_idx], rend_c[:,:,ch_idx], data_range=255)
        print(f"  SSIM {ch_name}:         {s:.4f}")

    # Também: correlação cruzada normalizada (template matching)
    # Mede o quão bem os padrões espaciais se alinham
    gray_o = gray_orig.astype(np.float32)
    gray_r = gray_rend.astype(np.float32)
    gray_o = (gray_o - gray_o.mean()) / gray_o.std()
    gray_r = (gray_r - gray_r.mean()) / gray_r.std()
    ncc = (gray_o * gray_r).mean()  # Normalized Cross-Correlation
    print(f"  NCC (cross-correlation): {ncc:.4f}  (1.0 = identico)")

    # Delta E (diferença perceptual de cor) médio
    orig_lab = cv2.cvtColor(orig_c, cv2.COLOR_RGB2LAB).astype(np.float32)
    rend_lab = cv2.cvtColor(rend_c, cv2.COLOR_RGB2LAB).astype(np.float32)
    deltae = np.sqrt(((orig_lab - rend_lab)**2).sum(axis=2)).mean()
    print(f"  Delta E medio:    {deltae:.1f}  (0 = identico, <3 = imperceptivel)")

    # PSNR
    mse = ((orig_c.astype(float) - rend_c.astype(float))**2).mean()
    psnr = 20 * np.log10(255.0 / np.sqrt(mse)) if mse > 0 else float('inf')
    print(f"  PSNR:             {psnr:.1f} dB  (>40 = excelente)")

print()
