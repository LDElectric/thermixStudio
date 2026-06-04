"""
SSIM com máscara — compara apenas pixels FORA do overlay (área limpa).
Justo: ambos original e render têm overlay excluído da comparação.
"""
import os, cv2, numpy as np
from skimage.metrics import structural_similarity as ssim

WS = r"c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio"

# Máscara FLIR E8xt
def create_mask(w, h):
    mask = np.zeros((h, w), dtype=np.uint8)
    cx, cy = w // 2, h // 2
    # Barras fixas
    mask[:22, :] = 255
    mask[h-38:, :] = 255
    mask[22:42, :60] = 255       # temp sup-esq
    mask[h-38:, :45] = 255       # logo inf-esq
    mask[:, w-32:] = 255         # escala dir
    # Crosshair
    t, L = 2, 35  # thickness/2, length
    mask[cy-t:cy+t, cx-L:cx+L] = 255
    mask[cy-L:cy+L, cx-t:cx+t] = 255
    cv2.circle(mask, (cx, cy), 8, 255, -1)
    return mask == 0  # True = pixel limpo

pairs = [('FLIR0192', 'FLIR0192.jpg', 'FLIR0192_analise.jpg'),
         ('2.jpg', '2.jpg', '2_analise.jpg')]

for name, orig_fn, render_fn in pairs:
    orig = cv2.imread(os.path.join(WS, orig_fn))
    rend = cv2.imread(os.path.join(WS, render_fn))
    h, w = orig.shape[:2]
    clean = create_mask(w, h)

    gray_o = cv2.cvtColor(orig, cv2.COLOR_BGR2GRAY)[clean]
    gray_r = cv2.cvtColor(rend, cv2.COLOR_BGR2GRAY)[clean]
    g = ssim(gray_o, gray_r, data_range=255)
    print(f'{name}: SSIM Gray (masked) = {g:.4f}')
    for ch, i in [('R', 2), ('G', 1), ('B', 0)]:
        s = ssim(orig[:,:,i][clean], rend[:,:,i][clean], data_range=255)
        print(f'  {ch}: {s:.4f}')
    print()
