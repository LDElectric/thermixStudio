using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ThermixStudio.App.Services;

/// <summary>
/// Manipulação binária de JPEG para preservação de segmentos APP1 FLIR.
/// Extrai todos os segmentos APP1 do arquivo original e os injeta no JPEG do render,
/// garantindo preservação TOTAL dos dados radiométricos (Planck, Palette, RawThermalImage, etc.).
/// </summary>
internal static class FlirJpegMerger
{
    /// <summary>
    /// Extrai todos os segmentos APP1 (0xFF 0xE1) de um arquivo JPEG.
    /// </summary>
    public static List<byte[]> ExtractApp1Segments(string jpegPath)
    {
        var segments = new List<byte[]>();
        var bytes = File.ReadAllBytes(jpegPath);
        var i = 0;

        while (i < bytes.Length - 1)
        {
            // Procurar marcador APP1: 0xFF 0xE1
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xE1)
            {
                if (i + 3 >= bytes.Length) break;

                // Tamanho do segmento = 2 bytes big-endian (inclui os 2 bytes do tamanho)
                var length = (bytes[i + 2] << 8) | bytes[i + 3];
                if (length < 2 || i + 2 + length > bytes.Length) break;

                // Extrair o segmento completo (incluindo marker e length)
                var segment = new byte[length + 2];
                Array.Copy(bytes, i, segment, 0, length + 2);
                segments.Add(segment);

                i += 2 + length;
            }
            else
            {
                i++;
            }
        }

        return segments;
    }

    /// <summary>
    /// Injeta segmentos APP1 do original no JPEG do render.
    /// Os APP1 são inseridos logo após o marker SOI (0xFF 0xD8) e antes de qualquer outro segmento.
    /// </summary>
    public static void InjectApp1Segments(string renderJpgPath, string outputJpgPath, List<byte[]> app1Segments)
    {
        var bytes = File.ReadAllBytes(renderJpgPath);

        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            // Não é um JPEG válido — apenas copia o render
            File.Copy(renderJpgPath, outputJpgPath, overwrite: true);
            return;
        }

        using var ms = new MemoryStream();
        // Escrever SOI
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD8);

        // Injetar todos os APP1 do original (ANTES dos segmentos do render)
        foreach (var seg in app1Segments)
        {
            ms.Write(seg, 0, seg.Length);
        }

        // Escrever o resto do render (após o SOI)
        ms.Write(bytes, 2, bytes.Length - 2);

        File.WriteAllBytes(outputJpgPath, ms.ToArray());
    }

    /// <summary>
    /// Cria um JPEG final com a imagem do render e TODOS os segmentos APP1 do original.
    /// </summary>
    public static bool MergeFlirMetadata(string originalJpgPath, string renderJpgPath, string outputJpgPath)
    {
        try
        {
            if (!File.Exists(originalJpgPath) || !File.Exists(renderJpgPath))
                return false;

            var app1Segments = ExtractApp1Segments(originalJpgPath);
            if (app1Segments.Count == 0)
                return false;

            InjectApp1Segments(renderJpgPath, outputJpgPath, app1Segments);
            return File.Exists(outputJpgPath) && new FileInfo(outputJpgPath).Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FLIR_MERGER] Erro: {ex.Message}");
            return false;
        }
    }
}
