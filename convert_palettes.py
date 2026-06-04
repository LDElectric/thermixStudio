"""
Extrai paletas YCrCb embarcadas de JPEGs FLIR e converte para JSON RGB (256 cores).
Uso: python convert_palettes.py
"""
import json
import struct
import os
import sys

# Mapeamento: nome_paleta -> (arquivo_jpg, arquivo_json_saida)
PALETTES = {
    "Iron":      (r"Paleta Iron.jpg",      r"src\ThermixStudio.App\paletas\iron_lut.json"),
    "Rainbow":   (r"Paleta Arco-Iris.jpg", r"src\ThermixStudio.App\paletas\rainbow_lut.json"),
    "Grayscale": (r"Paleta Cinzento.jpg",  r"src\ThermixStudio.App\paletas\grayscale_lut.json"),
}

EXIFTOOL = r"exiftool\exiftool.exe"
WORKSPACE = os.path.dirname(os.path.abspath(__file__))


def ycbcr_to_rgb(y, cr, cb):
    """Conversão YCrCb -> RGB (JPEG padrão, mesma do FlirFffParser)."""
    r = y + 1.402 * (cr - 128)
    g = y - 0.344 * (cb - 128) - 0.714 * (cr - 128)
    b = y + 1.772 * (cb - 128)
    return (
        max(0, min(255, round(r))),
        max(0, min(255, round(g))),
        max(0, min(255, round(b))),
    )


def extract_palette_ycrcb(jpg_path):
    """Extrai a paleta YCrCb bruta via exiftool -b -Palette e faz parse UTF-16LE."""
    import subprocess
    import tempfile

    exiftool_path = os.path.join(WORKSPACE, EXIFTOOL)
    jpg_full = os.path.join(WORKSPACE, jpg_path)

    # Extrair binário
    result = subprocess.run(
        [exiftool_path, "-b", "-Palette", jpg_full],
        capture_output=True,
        timeout=15,
    )

    raw = result.stdout
    if len(raw) < 6:
        raise RuntimeError(f"Extração falhou para {jpg_path}: {len(raw)} bytes")

    # Remover BOM UTF-16LE (FF FE) se presente
    if raw[:2] == b'\xff\xfe':
        raw = raw[2:]

    # Remover trailing CR+LF se presente
    if raw[-4:] == b'\x0d\x00\x0a\x00':
        raw = raw[:-4]

    # Decodificar como UTF-16LE (cada valor Y/Cr/Cb é um word de 16 bits)
    if len(raw) % 2 != 0:
        raw = raw[:-1]  # padding ímpar

    values = struct.unpack(f"<{len(raw)//2}H", raw)

    # Cada cor = 3 valores (Y, Cr, Cb)
    if len(values) % 3 != 0:
        raise RuntimeError(f"Número ímpar de valores: {len(values)}")

    colors = []
    for i in range(0, len(values), 3):
        y, cr, cb = values[i], values[i + 1], values[i + 2]
        colors.append((y, cr, cb))

    return colors


def resample_to_256(ycrcb_colors):
    """Reamostra a paleta para 256 cores (mesma lógica do FlirPaletteConverter)."""
    src_count = len(ycrcb_colors)
    rgb_colors = []

    for i in range(256):
        src_index = round(i * (src_count - 1) / 255.0)
        y, cr, cb = ycrcb_colors[src_index]
        r, g, b = ycbcr_to_rgb(y, cr, cb)
        rgb_colors.append([r, g, b])

    return rgb_colors


def main():
    for name, (jpg_file, json_file) in PALETTES.items():
        print(f"\n{'='*60}")
        print(f"Processando: {name} ({jpg_file})")
        print(f"{'='*60}")

        # Extrair YCrCb
        ycrcb = extract_palette_ycrcb(jpg_file)
        print(f"  Cores YCrCb extraídas: {len(ycrcb)}")

        # Mostrar primeiras e últimas cores YCrCb
        print(f"  1ª cor YCrCb: ({ycrcb[0][0]}, {ycrcb[0][1]}, {ycrcb[0][2]})")
        r, g, b = ycbcr_to_rgb(*ycrcb[0])
        print(f"  1ª cor RGB:   ({r}, {g}, {b})")
        print(f"  Última cor YCrCb: ({ycrcb[-1][0]}, {ycrcb[-1][1]}, {ycrcb[-1][2]})")
        r, g, b = ycbcr_to_rgb(*ycrcb[-1])
        print(f"  Última cor RGB:   ({r}, {g}, {b})")

        # Reamostrar para 256
        rgb_256 = resample_to_256(ycrcb)
        print(f"  Cores RGB (reamostradas 256): {len(rgb_256)}")

        # Gerar JSON
        output = {
            "name": name.lower(),
            "rgb": rgb_256,
        }

        output_path = os.path.join(WORKSPACE, json_file)
        os.makedirs(os.path.dirname(output_path), exist_ok=True)

        # Backup do arquivo existente
        if os.path.exists(output_path):
            backup = output_path + ".bak"
            with open(output_path, 'r', encoding='utf-8') as f:
                with open(backup, 'w', encoding='utf-8') as bf:
                    bf.write(f.read())
            print(f"  Backup salvo em: {os.path.basename(backup)}")

        # Gravar novo JSON
        with open(output_path, 'w', encoding='utf-8') as f:
            json.dump(output, f, indent=2)
        
        size_kb = os.path.getsize(output_path) / 1024
        print(f"  JSON gerado: {json_file} ({size_kb:.1f} KB)")

    print(f"\n{'='*60}")
    print("CONVERSÃO CONCLUÍDA!")
    print(f"{'='*60}")


if __name__ == "__main__":
    main()
