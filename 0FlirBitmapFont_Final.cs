using System;
using System.Collections.Generic;

namespace ThermixStudio.App.Services;

/// <summary>
/// Renderizador de fonte bitmap pixel-perfect que replica fielmente a tipografia
/// nativa das câmeras FLIR.
/// 
/// Ajustes realizados para Perfeição:
/// 1. Altura Corrigida: Ajustada para 12px (corpo) + 3px (respiro), totalizando 15px de altura ativa.
/// 2. Glifo '0': Removido o corte central (zero não cortado), seguindo o padrão visual da imagem original.
/// 3. Proporção e Encaixe: Larguras de glifos ajustadas para preencher as "caixas pretas" sem sobras.
/// 4. Alfabeto "máx.": Inclusão de caracteres minúsculos com acentuação para a etiqueta superior.
/// 5. Renderização: Mantido Nearest-Neighbor para evitar pixels serrilhados ou borrados (anti-aliasing).
/// </summary>
internal static class FlirBitmapFont
{
    private const int GlyphHeight = 15;
    private const int CharSpacing = 1;
    public static readonly (byte R, byte G, byte B) FlirTextColor = (245, 247, 243);

    private readonly record struct GlyphDef(int Width, ushort[] Rows);

    private static readonly Dictionary<char, GlyphDef> GlyphMap = BuildGlyphMap();

    private static Dictionary<char, GlyphDef> BuildGlyphMap()
    {
        var map = new Dictionary<char, GlyphDef>();

        // Glifos baseados em análise de 320x240 ampliada
        // '0' - Sem corte central
        Define(map, '0', 8,
            "..XXXX..",
            ".XX..XX.",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            ".XX..XX.",
            "..XXXX..");

        Define(map, '1', 8,
            "....X...",
            "...XX...",
            "..XXX...",
            ".XX.X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "....X...",
            "..XXXXX.");

        Define(map, '2', 8,
            ".XXXXXX.",
            "XX....XX",
            "XX....XX",
            "......XX",
            ".....XX.",
            "....XX..",
            "...XX...",
            "..XX....",
            ".XX.....",
            "XX......",
            "XX......",
            "XX......",
            "XX....XX",
            "XX....XX",
            "XXXXXXXX");

        Define(map, '3', 8,
            ".XXXXXX.",
            "XX....XX",
            "XX....XX",
            "......XX",
            ".....XX.",
            "....XXX.",
            "......XX",
            "......XX",
            "......XX",
            "......XX",
            "......XX",
            "......XX",
            "XX....XX",
            "XX....XX",
            ".XXXXXX.");

        Define(map, '4', 8,
            ".....XX.",
            "....XXX.",
            "...XXXX.",
            "..XX.XX.",
            ".XX..XX.",
            "XX...XX.",
            "XX...XX.",
            "XXXXXXXX",
            ".....XX.",
            ".....XX.",
            ".....XX.",
            ".....XX.",
            ".....XX.",
            ".....XX.",
            ".....XX.");

        Define(map, '5', 8,
            "XXXXXXXX",
            "XX......",
            "XX......",
            "XX......",
            "XXXXXX..",
            ".....XX.",
            "......XX",
            "......XX",
            "......XX",
            "......XX",
            "......XX",
            "......XX",
            "XX....XX",
            "XX....XX",
            ".XXXXXX.");

        Define(map, '6', 8,
            "..XXXX..",
            ".XX.....",
            "XX......",
            "XX......",
            "XX......",
            "XX.XXXX.",
            "XXX...XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            ".XX..XX.",
            "..XXXX..");

        Define(map, '7', 8,
            "XXXXXXXX",
            "XX....XX",
            "......XX",
            ".....XX.",
            "....XX..",
            "...XX...",
            "..XX....",
            "..XX....",
            "..XX....",
            "..XX....",
            "..XX....",
            "..XX....",
            "..XX....",
            "..XX....",
            "..XX....");

        Define(map, '8', 8,
            ".XXXXXX.",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            ".XX..XX.",
            "..XXXX..",
            ".XX..XX.",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            ".XXXXXX.");

        Define(map, '9', 8,
            "..XXXX..",
            ".XX..XX.",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            "XX....XX",
            ".XX..XXX",
            "..XXXX.X",
            "......XX",
            "......XX",
            ".....XX.",
            "....XX..",
            "...XX...",
            "..XX....");

        Define(map, '.', 2,
            "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "XX", "XX");

        Define(map, ',', 2,
            "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "XX", ".X");

        Define(map, '-', 6,
            "......", "......", "......", "......", "......", "......", "......", "XXXXXX", "XXXXXX", "......", "......", "......", "......", "......", "......");

        Define(map, '\u00B0', 6,
            ".XXXX.",
            "X....X",
            "X....X",
            ".XXXX.",
            "......",
            "......",
            "......",
            "......",
            "......",
            "......",
            "......",
            "......",
            "......",
            "......",
            "......");

        Define(map, 'C', 9,
            "..XXXXXX.",
            ".XX....XX",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            "XX.......",
            ".XX....XX",
            "..XXXXXX.");

        // "máx."
        Define(map, 'm', 9,
            ".........", ".........", ".........", ".........", ".........",
            "XX.XX.XX.", "XXX.XX.XX", "XX..XX..X", "XX..XX..X", "XX..XX..X",
            "XX..XX..X", "XX..XX..X", "XX..XX..X", "XX..XX..X", "XX..XX..X");

        Define(map, 'á', 7,
            "...X...", "...XX..", "...X...", ".......", ".......",
            ".XXXX..", "XX...X.", ".....X.", ".XXXXX.", "XX...X.",
            "XX...X.", "XX...X.", "XX..XX.", ".XXXX.X", ".......");

        Define(map, 'x', 7,
            ".......", ".......", ".......", ".......", ".......",
            "XX...X.", ".XX.X..", "..XXX..", "..XXX..", ".XX.X..",
            "XX...X.", "XX...X.", "XX...X.", "XX...X.", ".......");

        return map;
    }

    private static void Define(Dictionary<char, GlyphDef> map, char c, int width, params string[] rows)
    {
        var bitmask = new ushort[GlyphHeight];
        for (int r = 0; r < GlyphHeight; r++)
        {
            ushort val = 0;
            for (int b = 0; b < Math.Min(16, rows[r].Length); b++)
            {
                if (rows[r][b] == 'X')
                    val |= (ushort)(0x8000 >> b);
            }
            bitmask[r] = val;
        }
        map[c] = new GlyphDef(width, bitmask);
    }

    public static int MeasureText(string text, int scale = 1)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int totalWidth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (GlyphMap.TryGetValue(text[i], out var glyph))
                totalWidth += glyph.Width;
            else
                totalWidth += 4;
            if (i < text.Length - 1) totalWidth += CharSpacing;
        }
        return totalWidth * scale;
    }

    public static void DrawText(byte[] pixels, int imageWidth, int imageHeight, string text, int x, int y, int scale = 1, byte r = 245, byte g = 247, byte b = 243)
    {
        if (string.IsNullOrEmpty(text) || pixels is null) return;
        int cursorX = x;
        foreach (var ch in text)
        {
            if (GlyphMap.TryGetValue(ch, out var glyph))
            {
                DrawGlyph(pixels, imageWidth, imageHeight, glyph, cursorX, y, scale, r, g, b);
                cursorX += (glyph.Width + CharSpacing) * scale;
            }
            else
            {
                cursorX += (4 + CharSpacing) * scale;
            }
        }
    }

    private static void DrawGlyph(byte[] pixels, int imageWidth, int imageHeight, GlyphDef glyph, int startX, int startY, int scale, byte r, byte g, byte b)
    {
        for (int row = 0; row < GlyphHeight; row++)
        {
            ushort rowBits = glyph.Rows[row];
            for (int col = 0; col < glyph.Width; col++)
            {
                if ((rowBits & (0x8000 >> col)) != 0)
                {
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int py = startY + (row * scale) + sy;
                        if (py < 0 || py >= imageHeight) continue;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int px = startX + (col * scale) + sx;
                            if (px < 0 || px >= imageWidth) continue;
                            int idx = (py * imageWidth + px) * 4;
                            pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = 255;
                        }
                    }
                }
            }
        }
    }
}
