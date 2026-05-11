"""
Extrai e aprimora a imagem em luz visível (foto digital) de um arquivo FLIR (.jpg).

A câmera visível do FLIR E8 frequentemente produz imagens subexpostas (muito escuras).
Este script extrai o JPEG embutido diretamente do FFF e aplica correção de exposição
automática para produzir uma imagem legível e com cores naturais.

Requisitos: pip install flyr pillow numpy opencv-python
"""
import sys
import os
import io
import struct
import json
import traceback
import numpy as np
from PIL import Image

# OpenCV é opcional — usado para CLAHE; cai para fallback PIL se não estiver disponível
try:
    import cv2
    _CV2_AVAILABLE = True
except ImportError:
    _CV2_AVAILABLE = False


# ---------------------------------------------------------------------------
# Extração direta do JPEG embutido via parsing FFF
# ---------------------------------------------------------------------------

def _extrair_fff_stream(caminho_arquivo: str) -> bytes | None:
    """
    Reconstrói o bloco FLIR FFF a partir dos segmentos APP1 do JPEG.
    Retorna os bytes do FFF ou None se não encontrar dados FLIR.
    """
    with open(caminho_arquivo, "rb") as f:
        data = f.read()

    chunks = {}
    i = 0
    while i < len(data) - 4:
        if data[i] == 0xFF and data[i + 1] == 0xE1:
            length = (data[i + 2] << 8) | data[i + 3]
            content_start = i + 4
            if data[content_start : content_start + 5] == b"FLIR\x00":
                chunk_num = data[content_start + 6]
                payload = data[content_start + 8 : i + 2 + length]
                chunks[chunk_num] = payload
            i += 2 + length
        else:
            i += 1

    if not chunks:
        return None

    return b"".join(v for _, v in sorted(chunks.items()))


def _extrair_jpeg_embutido(fff: bytes) -> bytes | None:
    """
    Parseia o FFF e retorna os bytes do record EMBEDDED_IMAGE (tipo 14),
    que contém a foto da câmera digital.
    """
    stream = io.BytesIO(fff)

    # Header FFF (big-endian, conforme documentação FLIR e código do flyr)
    fmt_id = stream.read(4)
    if fmt_id not in (b"FFF\x00", b"AFF\x00"):
        return None

    stream.seek(16, 1)   # pula 16 bytes do campo "creator"
    stream.seek(4, 1)    # pula file_format_version
    rec_dir_offset = int.from_bytes(stream.read(4), "big")
    rec_dir_count  = int.from_bytes(stream.read(4), "big")

    for rec_nr in range(rec_dir_count):
        stream.seek(rec_dir_offset + rec_nr * 32)
        rec_type   = int.from_bytes(stream.read(2), "big")
        stream.seek(10, 1)
        rec_offset = int.from_bytes(stream.read(4), "big")
        rec_length = int.from_bytes(stream.read(4), "big")

        if rec_type == 14:  # EMBEDDED_IMAGE
            # 32 bytes de header interno (largura, altura, etc.)
            stream.seek(rec_offset + 32)
            jpeg_bytes = stream.read(rec_length)
            # Verifica assinatura JPEG
            if jpeg_bytes[:2] == b"\xff\xd8":
                return jpeg_bytes

    return None


# ---------------------------------------------------------------------------
# Correção de exposição / realce de brilho
# ---------------------------------------------------------------------------

def _media_pixel(arr: np.ndarray) -> float:
    return float(arr.mean())


def _corrigir_com_clahe(arr_bgr: np.ndarray) -> np.ndarray:
    """
    Aplica CLAHE (Contrast Limited Adaptive Histogram Equalization)
    no canal L do espaço LAB. Preserva cores naturais enquanto aumenta contraste.
    """
    lab = cv2.cvtColor(arr_bgr, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8, 8))
    l_corrigido = clahe.apply(l)
    lab_corrigido = cv2.merge([l_corrigido, a, b])
    return cv2.cvtColor(lab_corrigido, cv2.COLOR_LAB2BGR)


def _corrigir_com_gamma(arr_rgb: np.ndarray, gamma: float) -> np.ndarray:
    """
    Aplica correção gamma com offset para garantir que pixels zero (completamente
    pretos) também sejam transformados em meios-tons visíveis.

    A maioria das câmeras FLIR E8 grava ~86% dos pixels como exatamente zero.
    Sem o offset, qualquer função de potência mantém 0 → 0.
    Com offset=8 e gamma=0.22: pixel_orig=0 → ~118 (meio-tom neutro).
    """
    offset = 8
    arr_f = arr_rgb.astype(np.float32) + offset
    arr_norm = arr_f / (255.0 + offset)
    arr_gamma = np.power(np.clip(arr_norm, 0.0, 1.0), gamma) * 255.0
    return np.clip(arr_gamma, 0, 255).astype(np.uint8)


def _corrigir_com_autolevels(arr_rgb: np.ndarray, saturacao: float = 0.005) -> np.ndarray:
    """
    Estica o histograma ignorando os 'saturacao'% de pixels mais escuros e mais claros.
    Equivale ao "Auto Levels" do Photoshop.
    """
    resultado = np.zeros_like(arr_rgb)
    for canal in range(arr_rgb.shape[2]):
        c = arr_rgb[:, :, canal].flatten()
        p_low  = np.percentile(c, saturacao * 100)
        p_high = np.percentile(c, 100 - saturacao * 100)
        if p_high > p_low:
            resultado[:, :, canal] = np.clip(
                (arr_rgb[:, :, canal].astype(np.float32) - p_low)
                / (p_high - p_low)
                * 255,
                0, 255,
            ).astype(np.uint8)
        else:
            resultado[:, :, canal] = arr_rgb[:, :, canal]
    return resultado


def _aprimorar_imagem(img_pil: Image.Image) -> Image.Image:
    """
    Corrige a exposição da imagem visível extraída do FLIR.

    Problema: câmeras FLIR E8 gravam ~86% dos pixels como exatamente zero.
    Técnicas lineares (normalize, equalizeHist) preservam 0 → 0, produzindo imagem escura.
    Solução: offset + gamma agressivo garante que pixels zero virem meios-tons (~118).

    Níveis de correção:
      - Imagem boa (mean > 60): CLAHE leve para realçar contraste local
      - Subexposta (mean > 10): Auto Levels + CLAHE
      - Muito escura (mean <= 10, típico FLIR E8): Offset+Gamma + CLAHE
    """
    arr = np.array(img_pil.convert("RGB"))
    media = _media_pixel(arr)

    print(f"  Luminosidade média da imagem original: {media:.2f}/255")

    if media > 60:
        # Imagem bem exposta — CLAHE leve para realçar contraste local
        if _CV2_AVAILABLE:
            print("  Aplicando CLAHE leve (imagem bem exposta)...")
            arr_bgr = cv2.cvtColor(arr, cv2.COLOR_RGB2BGR)
            clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
            lab = cv2.cvtColor(arr_bgr, cv2.COLOR_BGR2LAB)
            l_ch, a_ch, b_ch = cv2.split(lab)
            lab2 = cv2.merge([clahe.apply(l_ch), a_ch, b_ch])
            arr_resultado = cv2.cvtColor(
                cv2.cvtColor(lab2, cv2.COLOR_LAB2BGR), cv2.COLOR_BGR2RGB
            )
        else:
            arr_resultado = arr

    elif media > 10:
        # Subexposta — Auto Levels + CLAHE
        print("  Aplicando Auto Levels + CLAHE (imagem subexposta)...")
        arr = _corrigir_com_autolevels(arr, saturacao=0.01)
        if _CV2_AVAILABLE:
            arr_bgr = cv2.cvtColor(arr, cv2.COLOR_RGB2BGR)
            arr_bgr = _corrigir_com_clahe(arr_bgr)
            arr_resultado = cv2.cvtColor(arr_bgr, cv2.COLOR_BGR2RGB)
        else:
            arr_resultado = arr

    else:
        # Muito escura: FLIR E8 grava maioria como zero absoluto (mean ~1).
        # Offset+Gamma (0.22) transforma:  0 → ~118,  189 → ~241
        # Seguido de CLAHE leve para realçar detalhes locais.
        print("  Aplicando Offset+Gamma(0.22) + CLAHE (imagem com zeros absolutos)...")
        gamma = 0.22
        arr = _corrigir_com_gamma(arr, gamma)
        if _CV2_AVAILABLE:
            arr_bgr = cv2.cvtColor(arr, cv2.COLOR_RGB2BGR)
            lab = cv2.cvtColor(arr_bgr, cv2.COLOR_BGR2LAB)
            l_ch, a_ch, b_ch = cv2.split(lab)
            clahe = cv2.createCLAHE(clipLimit=2.5, tileGridSize=(8, 8))
            lab_out = cv2.merge([clahe.apply(l_ch), a_ch, b_ch])
            arr_resultado = cv2.cvtColor(
                cv2.cvtColor(lab_out, cv2.COLOR_LAB2BGR), cv2.COLOR_BGR2RGB
            )
        else:
            arr_resultado = arr

    media_final = _media_pixel(arr_resultado)
    print(f"  Luminosidade média após correção: {media_final:.2f}/255")
    return Image.fromarray(arr_resultado, "RGB")


# ---------------------------------------------------------------------------
# Função principal de extração
# ---------------------------------------------------------------------------

def extrair_visivel(caminho_arquivo: str, saida_json: bool = False) -> bool:
    """
    Extrai a imagem visível do termograma FLIR e aplica correção de exposição.

    Parâmetros
    ----------
    caminho_arquivo : str
        Caminho para o arquivo .jpg do termograma FLIR.
    saida_json : bool
        Se True, imprime resultado como JSON (para chamada por C#).
    """
    if not os.path.isfile(caminho_arquivo):
        _erro("Arquivo não encontrado: " + caminho_arquivo, saida_json)
        return False

    print(f"Processando: {caminho_arquivo}")

    try:
        # 1. Tenta extração direta via FFF (mais confiável, sem flyr)
        img_pil = None
        fff = _extrair_fff_stream(caminho_arquivo)
        if fff:
            jpeg_bytes = _extrair_jpeg_embutido(fff)
            if jpeg_bytes:
                img_pil = Image.open(io.BytesIO(jpeg_bytes)).convert("RGB")
                print(f"  ✓ Imagem extraída via FFF direto ({len(jpeg_bytes):,} bytes, {img_pil.size[0]}×{img_pil.size[1]})")

        # 2. Fallback: usa a biblioteca flyr
        if img_pil is None:
            print("  FFF direto falhou, tentando via flyr...")
            from flyr import unpack
            termograma = unpack(caminho_arquivo)
            img_pil = termograma.optical_pil
            if img_pil is None:
                _erro("Nenhuma imagem visível encontrada no arquivo.", saida_json)
                return False
            img_pil = img_pil.convert("RGB")
            print(f"  ✓ Imagem extraída via flyr ({img_pil.size[0]}×{img_pil.size[1]})")

        # 3. Aprimora a imagem se necessária
        img_aprimorada = _aprimorar_imagem(img_pil)

        # 4. Salva o resultado
        base = os.path.splitext(caminho_arquivo)[0]
        nome_original   = base + "_visivel_original.jpg"
        nome_aprimorado = base + "_visivel.jpg"

        img_pil.save(nome_original, "JPEG", quality=95)
        img_aprimorada.save(nome_aprimorado, "JPEG", quality=95)

        print(f"  ✓ Imagem original salva:   {nome_original}")
        print(f"  ✓ Imagem aprimorada salva: {nome_aprimorado}")

        if saida_json:
            print(json.dumps({
                "status": "sucesso",
                "caminho_visivel": nome_aprimorado,
                "caminho_visivel_original": nome_original,
            }))

        return True

    except Exception as e:
        _erro(str(e) + "\n" + traceback.format_exc(), saida_json)
        return False


def _erro(mensagem: str, saida_json: bool):
    if saida_json:
        print(json.dumps({"status": "erro", "mensagem": mensagem}))
    else:
        print(f"ERRO: {mensagem}")


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    # Modo JSON quando chamado com flag --json (para integração com C#)
    args = [a for a in sys.argv[1:] if a != "--json"]
    modo_json = "--json" in sys.argv[1:]

    if not args:
        print("Uso: python extrair_imagens_flir.py <arquivo.jpg> [--json]")
        print("  --json  Imprime resultado como JSON (para chamada por C#)")
        sys.exit(1)

    sucesso = extrair_visivel(args[0], saida_json=modo_json)
    sys.exit(0 if sucesso else 1)