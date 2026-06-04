"""Extrai paletas brutas via exiftool com subprocess (sem artefato UTF-16LE do PowerShell)."""
import subprocess, os, struct, json

WORKSPACE = r'c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio'
TEMP = os.environ['TEMP']
EXIFTOOL = os.path.join(WORKSPACE, 'exiftool', 'exiftool.exe')

PALETTES = {
    'iron':    ('Paleta Iron.jpg',      r'src\ThermixStudio.App\paletas\iron_lut.json'),
    'rainbow': ('Paleta Arco-Iris.jpg', r'src\ThermixStudio.App\paletas\rainbow_lut.json'),
    'gray':    ('Paleta Cinzento.jpg',  r'src\ThermixStudio.App\paletas\grayscale_lut.json'),
}


def ycbcr_to_rgb(y, cr, cb):
    r = y + 1.402 * (cr - 128)
    g = y - 0.344 * (cb - 128) - 0.714 * (cr - 128)
    b = y + 1.772 * (cb - 128)
    return [
        max(0, min(255, round(r))),
        max(0, min(255, round(g))),
        max(0, min(255, round(b))),
    ]


for name, (jpg_rel, json_rel) in PALETTES.items():
    jpg_path = os.path.join(WORKSPACE, jpg_rel)
    json_path = os.path.join(WORKSPACE, json_rel)

    # Extrair com subprocess (stdout binário, sem PowerShell)
    result = subprocess.run(
        [EXIFTOOL, '-b', '-Palette', jpg_path],
        capture_output=True,
        timeout=15,
    )
    raw = result.stdout
    print(f'{name}: {len(raw)} bytes')

    # Debug: primeiros 15 bytes
    hex_first = ' '.join(f'{b:02X}' for b in raw[:15])
    print(f'  Primeiros 15 bytes: {hex_first}')

    if len(raw) < 6:
        print(f'  ERRO: dados muito curtos')
        continue

    # Verificar BOM UTF-16LE gerado pelo stdout do Windows
    if raw[:2] == b'\xff\xfe':
        print('  Detectado BOM UTF-16LE (artefato Windows)')
        # O dado real está em UTF-16LE: cada par de bytes [lo, hi] = um caractere Unicode
        # O valor real é o byte baixo (o byte alto é 0x00 para valores 0-255)
        raw = raw[2:]  # remove BOM
        # Remove trailing CRLF se presente
        if raw[-4:] == b'\x0d\x00\x0a\x00':
            raw = raw[:-4]
        
        # Parse como UTF-16LE
        if len(raw) % 2 != 0:
            raw = raw[:-1]
        values = struct.unpack(f'<{len(raw)//2}H', raw)

        # Verificar quantos valores > 255
        big = sum(1 for v in values if v > 255)
        print(f'  Valores 16-bit: {len(values)}, >255: {big}')
        
        if big > 0:
            # Tem valores > 255 → paleta armazenada como 16-bit real
            # Escalar para 0-255
            vmin = min(values)
            vmax = max(values)
            print(f'  Range 16-bit: {vmin} - {vmax}, bits: {vmax.bit_length()}')
            # Mapear para 0-255 preservando a distribuição
            ycrcb_8bit = []
            for i in range(0, len(values), 3):
                y = values[i]
                cr = values[i+1]
                cb = values[i+2]
                # Y: 0-255 mapeia diretamente (Y é luminância)
                # Cr/Cb: centrados em 128, mapear proporcionalmente
                y8 = min(255, max(0, round(y * 255 / vmax)))
                cr8 = min(255, max(0, round(128 + (cr - 128) * 127 / (vmax/2))))
                cb8 = min(255, max(0, round(128 + (cb - 128) * 127 / (vmax/2))))
                ycrcb_8bit.append((y8, cr8, cb8))
        else:
            # Todos valores 0-255 → já são bytes
            ycrcb_8bit = [(values[i], values[i+1], values[i+2]) for i in range(0, len(values)-2, 3)]
    else:
        # Bytes crus (sem BOM)
        if len(raw) % 3 != 0:
            print(f'  AVISO: tamanho {len(raw)} não é múltiplo de 3')
        ycrcb_8bit = [(raw[i], raw[i+1], raw[i+2]) for i in range(0, len(raw)-2, 3)]

    print(f'  Cores YCrCb 8-bit: {len(ycrcb_8bit)}')
    y, cr, cb = ycrcb_8bit[0]
    rgb0 = ycbcr_to_rgb(y, cr, cb)
    print(f'  1ª cor: YCrCb({y},{cr},{cb}) => RGB({rgb0[0]},{rgb0[1]},{rgb0[2]})')
    y, cr, cb = ycrcb_8bit[-1]
    rgbN = ycbcr_to_rgb(y, cr, cb)
    print(f'  Última: YCrCb({y},{cr},{cb}) => RGB({rgbN[0]},{rgbN[1]},{rgbN[2]})')

    # Reamostrar para 256 cores
    src_count = len(ycrcb_8bit)
    rgb_256 = []
    for i in range(256):
        si = round(i * (src_count - 1) / 255.0)
        rgb_256.append(ycbcr_to_rgb(*ycrcb_8bit[si]))

    # Backup do arquivo existente
    if os.path.exists(json_path):
        bak = json_path + '.bak'
        if not os.path.exists(bak):
            import shutil
            shutil.copy2(json_path, bak)
            print(f'  Backup: {os.path.basename(bak)}')

    # Gravar JSON
    output = {'name': name, 'rgb': rgb_256}
    os.makedirs(os.path.dirname(json_path), exist_ok=True)
    with open(json_path, 'w', encoding='utf-8') as f:
        json.dump(output, f, indent=2)
    print(f'  JSON: {json_rel} ({len(rgb_256)} cores) OK')
    print()

print('CONCLUÍDO!')
