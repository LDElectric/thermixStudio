"""
Análise precisa da tipografia do FLIR0060.jpg com threshold reduzido para detectar o prefixo.
"""
from PIL import Image
import numpy as np

img = Image.open("FLIR0060.jpg").convert("RGB")
arr = np.array(img)
W, H = img.width, img.height

# ── Análise detalhada da região top-left com threshold baixo ──────────────
print("── Análise detalhada top-left (y=0-30, x=0-130), threshold=80 ──")
for y in range(0, 30):
    line_pixels = []
    for x in range(0, 130):
        r,g,b = arr[y,x]
        bright = (int(r)+int(g)+int(b))//3
        if bright > 80:
            line_pixels.append((x, bright))
    if line_pixels:
        ranges = []
        grp_start = line_pixels[0][0]
        prev_x = line_pixels[0][0]
        for x, br in line_pixels[1:]:
            if x - prev_x > 3:
                ranges.append(f"x={grp_start}-{prev_x}")
                grp_start = x
            prev_x = x
        ranges.append(f"x={grp_start}-{prev_x}")
        print(f"  y={y:2d}: {', '.join(ranges)}")

# ── Medições de grupos com threshold=90 ──────────────────────────────────
print("\n── Grupos de colunas com texto (threshold brightness>90) ──")
col_info = {}  # x -> (y_min, y_max, brightness_list)
for x in range(0, 130):
    pixels = []
    for y in range(0, 30):
        r,g,b = arr[y,x]
        bright = (int(r)+int(g)+int(b))//3
        if bright > 90:
            pixels.append((y, bright))
    if pixels:
        ys = [p[0] for p in pixels]
        col_info[x] = (min(ys), max(ys), len(pixels))

# Agrupar por proximidade
groups = []
cur = []
prev_x = None
for x in sorted(col_info):
    if prev_x is None or x - prev_x <= 2:
        cur.append(x)
    else:
        if cur: groups.append(cur)
        cur = [x]
    prev_x = x
if cur: groups.append(cur)

for i, grp in enumerate(groups):
    ys = []
    for x in grp:
        y0, y1, _ = col_info[x]
        ys.extend([y0, y1])
    print(f"  Grupo {i+1}: x={grp[0]:3d}-{grp[-1]:3d} (w={grp[-1]-grp[0]+1:2d}px), y={min(ys):2d}-{max(ys):2d} (h={max(ys)-min(ys)+1:2d}px)")

# ── Caixa escura ao redor ──────────────────────────────────────────────────
print("\n── Bbox da caixa preta (pixels com brightness <60 na região top-left) ──")
dark_xs, dark_ys = [], []
for y in range(0, 35):
    for x in range(0, 130):
        r,g,b = arr[y,x]
        bright = (int(r)+int(g)+int(b))//3
        if bright < 60:
            dark_xs.append(x)
            dark_ys.append(y)

if dark_xs:
    print(f"  Box x={min(dark_xs)}-{max(dark_xs)}, y={min(dark_ys)}-{max(dark_ys)}")
    print(f"  Box width={max(dark_xs)-min(dark_xs)+1}, height={max(dark_ys)-min(dark_ys)+1}")
    print(f"  Margem topo entre box e texto: {min([col_info[x][0] for x in col_info if x > 0])-min(dark_ys)}px")
    print(f"  Margem esquerda entre box e texto: {min(col_info.keys())-min(dark_xs)}px")

# ── Informações adicionais sobre grupos ──────────────────────────────────
print("\n── Resumo para implementação ──")
if groups:
    all_ys = []
    for grp in groups:
        for x in grp:
            y0, y1, _ = col_info[x]
            all_ys.extend([y0, y1])
    print(f"  Texto total: y={min(all_ys)}-{max(all_ys)}, altura_efetiva={max(all_ys)-min(all_ys)+1}px")
    
    # Separar grupos: quais têm altura menor (prefix/suffix) vs maior (digits)
    tall_groups = [g for g in groups if (max([col_info[x][1] for x in g]) - min([col_info[x][0] for x in g]) + 1) >= 12]
    short_groups = [g for g in groups if (max([col_info[x][1] for x in g]) - min([col_info[x][0] for x in g]) + 1) < 12]
    
    print(f"\n  Grupos ALTOS (dígitos principais, h>=12px):")
    for g in tall_groups:
        ys = []
        for x in g:
            y0,y1,_ = col_info[x]
            ys.extend([y0,y1])
        print(f"    x={g[0]}-{g[-1]} (w={g[-1]-g[0]+1}px), y={min(ys)}-{max(ys)} (h={max(ys)-min(ys)+1}px)")
    
    print(f"\n  Grupos CURTOS (prefix/suffix, h<12px):")
    for g in short_groups:
        ys = []
        for x in g:
            y0,y1,_ = col_info[x]
            ys.extend([y0,y1])
        print(f"    x={g[0]}-{g[-1]} (w={g[-1]-g[0]+1}px), y={min(ys)}-{max(ys)} (h={max(ys)-min(ys)+1}px)")

# ── Caixas top-right e bottom-right ──────────────────────────────────────
print("\n── Caixa top-right (Tmax = '106') ──")
tr_groups = {}
for y in range(0, 30):
    for x in range(W-70, W):
        r,g,b = arr[y,x]
        bright = (int(r)+int(g)+int(b))//3
        if bright > 90:
            if x not in tr_groups:
                tr_groups[x] = [y, y]
            tr_groups[x][1] = y

if tr_groups:
    xs = sorted(tr_groups)
    all_ys_tr = []
    for x in xs:
        all_ys_tr.extend(tr_groups[x])
    print(f"  x={xs[0]}-{xs[-1]} (w={xs[-1]-xs[0]+1}px)")
    print(f"  y={min(all_ys_tr)}-{max(all_ys_tr)} (h={max(all_ys_tr)-min(all_ys_tr)+1}px)")

print("\n── Caixa bottom-right (Tmin = '39.0') ──")
br_groups = {}
for y in range(H-30, H):
    for x in range(W-80, W):
        r,g,b = arr[y,x]
        bright = (int(r)+int(g)+int(b))//3
        if bright > 90:
            if x not in br_groups:
                br_groups[x] = [y, y]
            br_groups[x][1] = y

if br_groups:
    xs_br = sorted(br_groups)
    all_ys_br = []
    for x in xs_br:
        all_ys_br.extend(br_groups[x])
    print(f"  x={xs_br[0]}-{xs_br[-1]} (w={xs_br[-1]-xs_br[0]+1}px)")
    print(f"  y={min(all_ys_br)}-{max(all_ys_br)} (h={max(all_ys_br)-min(all_ys_br)+1}px)")
