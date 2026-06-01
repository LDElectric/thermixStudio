"""
Analisa a imagem original FLIR0060.jpg para medir precisamente:
- Posição e tamanho das caixas de temperatura
- Altura, posição e alinhamento dos caracteres
- Gap entre prefix, dígitos e sufixo
"""
from PIL import Image, ImageDraw
import numpy as np
import sys

img = Image.open("FLIR0060.jpg").convert("RGB")
arr = np.array(img)
W, H = img.width, img.height
print(f"Dimensões: {W}x{H}")

# ── Análise da caixa do spot (topo-esquerda) ──────────────────────────────
# Encontrar a caixa preta no canto superior esquerdo
# A caixa deve ser escura (RGB < 60) com bordas arredondadas

def is_dark(r,g,b, thresh=80):
    return r < thresh and g < thresh and b < thresh

def is_light_text(r,g,b):
    return r > 180 and g > 180 and b > 180

# Varredura para encontrar caixa preta
print("\n── Caixa do spot (top-left) ──")
box_x1, box_y1, box_x2, box_y2 = None, None, None, None
# Procura coluna com pixels escuros na região top-left (x:0-160, y:0-40)
for y in range(0, 40):
    for x in range(0, 160):
        r,g,b = arr[y,x]
        if is_dark(r,g,b, 60):
            if box_y1 is None:
                box_y1 = y
                box_x1 = x
            box_y2 = y
            box_x2 = max(box_x2 or 0, x)

print(f"  Estimativa caixa: x={box_x1}-{box_x2}, y={box_y1}-{box_y2}")
print(f"  Altura estimada da caixa: {(box_y2 or 0) - (box_y1 or 0) + 1}px")
print(f"  Largura estimada da caixa: {(box_x2 or 0) - (box_x1 or 0) + 1}px")

# ── Análise linha a linha dos pixels claros na região da caixa ────────────
print("\n── Pixels claros (texto) na região top-left (x:0-140, y:0-35) ──")
text_rows = {}
for y in range(0, 35):
    light_xs = []
    for x in range(0, 140):
        r,g,b = arr[y,x]
        if is_light_text(r,g,b):
            light_xs.append(x)
    if light_xs:
        text_rows[y] = (min(light_xs), max(light_xs), len(light_xs))

for y, (x1,x2,cnt) in sorted(text_rows.items()):
    print(f"  y={y:2d}: x={x1:3d}-{x2:3d} ({cnt} pixels claro)")

# Calcular altura efetiva do texto
if text_rows:
    ys = sorted(text_rows.keys())
    text_top = ys[0]
    text_bottom = ys[-1]
    print(f"\n  Texto: top={text_top}, bottom={text_bottom}, altura_efetiva={text_bottom-text_top+1}px")

# ── Identificar regiões de cada elemento (prefix, digits, suffix) ──────────
print("\n── Colunas com texto por região ──")
col_has_text = {}
for x in range(0, 140):
    light_ys = []
    for y in range(0, 35):
        r,g,b = arr[y,x]
        if is_light_text(r,g,b):
            light_ys.append(y)
    if light_ys:
        col_has_text[x] = (min(light_ys), max(light_ys), len(light_ys))

# Encontrar grupos de colunas (palavras/glifos separados)
prev_x = None
groups = []
cur_group = []
for x in sorted(col_has_text.keys()):
    if prev_x is None or x - prev_x <= 2:
        cur_group.append(x)
    else:
        if cur_group:
            groups.append(cur_group)
        cur_group = [x]
    prev_x = x
if cur_group:
    groups.append(cur_group)

print(f"  Grupos de texto encontrados: {len(groups)}")
for i, g in enumerate(groups):
    xs = g
    ys_in_group = []
    for x in xs:
        ys_in_group.extend([col_has_text[x][0], col_has_text[x][1]])
    y_top = min(ys_in_group)
    y_bot = max(ys_in_group)
    print(f"  Grupo {i+1}: x={xs[0]}-{xs[-1]} (w={xs[-1]-xs[0]+1}px), y={y_top}-{y_bot} (h={y_bot-y_top+1}px)")

# ── Caixa top-right (Tmax = "106") ────────────────────────────────────────
print("\n── Caixa top-right (Tmax) ──")
for y in range(0, 35):
    light_xs = []
    for x in range(W-60, W):
        r,g,b = arr[y,x]
        if is_light_text(r,g,b):
            light_xs.append(x)
    if light_xs:
        print(f"  y={y:2d}: x={min(light_xs):3d}-{max(light_xs):3d}")

# ── Caixa bottom-right (Tmin = "39.0") ─────────────────────────────────────
print("\n── Caixa bottom-right (Tmin) ──")
for y in range(H-35, H):
    light_xs = []
    for x in range(W-80, W):
        r,g,b = arr[y,x]
        if is_light_text(r,g,b):
            light_xs.append(x)
    if light_xs:
        print(f"  y={y:3d}: x={min(light_xs):3d}-{max(light_xs):3d}")

# ── Salvar imagem anotada ──────────────────────────────────────────────────
out = img.copy()
draw = ImageDraw.Draw(out)
# Marcar cada pixel claro na região do texto
for y in range(0, 35):
    for x in range(0, 140):
        r,g,b = arr[y,x]
        if is_light_text(r,g,b):
            draw.point((x,y), fill=(255,0,0))
out.save("tests/ThermixStudio.RenderingChecks/flir0060_analysis.png")
print("\n  Imagem anotada salva em tests/.../flir0060_analysis.png")
