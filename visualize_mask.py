"""
Visualizador de máscara de overlay para FLIR.
Gera overlay_mask.png mostrando regiões excluídas em vermelho.
Edite os parâmetros abaixo para refinar a máscara.
"""
import cv2, numpy as np, os

WORKSPACE = r"c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio"
IMAGE = "2.jpg"  # ou "FLIR0192.jpg"

# ===== PARÂMETROS DA MÁSCARA (edite aqui) =====
TOP_ROWS = 22
BOTTOM_ROWS = 38
LEFT_COLS_TOP = 60
LOGO_ROWS = 38
LOGO_COLS = 45
RIGHT_COLS = 32
CENTER_RADIUS = 4
# ==============================================

img = cv2.imread(os.path.join(WORKSPACE, IMAGE))
if img is None:
    print(f"Erro: {IMAGE} não encontrado")
    exit(1)

h, w = img.shape[:2]
mask = np.zeros((h, w), dtype=np.uint8)

# Top rows
mask[:TOP_ROWS, :] = 255
# Bottom rows (logo + escala)
mask[h - BOTTOM_ROWS:, :] = 255
# Top-left temp reading
mask[TOP_ROWS:TOP_ROWS + 20, :LEFT_COLS_TOP] = 255
# Bottom-left logo
mask[h - LOGO_ROWS:, :LOGO_COLS] = 255
# Right scale bar
mask[:, w - RIGHT_COLS:] = 255
# Center crosshair
cv2.circle(mask, (w // 2, h // 2), CENTER_RADIUS, 255, -1)

# Overlay vermelho
overlay = img.copy()
overlay[mask == 255] = [0, 0, 255]
result = cv2.addWeighted(img, 0.5, overlay, 0.5, 0)

out = os.path.join(WORKSPACE, "overlay_mask.png")
cv2.imwrite(out, result)
print(f"Salvo: {out}")
print(f"Pixels mascarados: {np.sum(mask == 255)} / {w * h} ({100.0 * np.sum(mask == 255) / (w * h):.1f}%)")
