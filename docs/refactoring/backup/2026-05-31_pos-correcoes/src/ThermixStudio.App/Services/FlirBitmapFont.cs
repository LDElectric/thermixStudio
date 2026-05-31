namespace ThermixStudio.App.Services;

/// <summary>
/// Renderizador de fonte bitmap pixel-perfect que replica a tipografia
/// nativa das câmeras FLIR (E8-xt e similares).
/// 
/// Características da fonte FLIR original:
/// - Altura do glyph: 10 pixels ativos (corpo principal) em resolução nativa 320×240
/// - Largura: variável (proporcional), média de 6–8px
/// - Anti-aliasing: inexistente (binário: pixel ligado/desligado)
/// - Cor do texto: RGB(245, 247, 243) sobre fundo preto
/// 
/// Os glyphs são armazenados como bitmasks (1 byte por linha, MSB = pixel mais à esquerda).
/// O escalonamento usa nearest-neighbor para preservar a estética bitmap/raster.
/// </summary>
internal static class FlirBitmapFont
{
    /// <summary>Altura ativa de cada glyph em pixels (resolução base).</summary>
    private const int GlyphHeight = 10;

    /// <summary>Espaçamento entre caracteres em pixels (resolução base).</summary>
    private const int CharSpacing = 1;

    /// <summary>Cor padrão do texto FLIR: RGB(245, 247, 243).</summary>
    public static readonly (byte R, byte G, byte B) FlirTextColor = (245, 247, 243);

    /// <summary>
    /// Definição de um glyph bitmap: largura em pixels e array de linhas (bitmask).
    /// </summary>
    private readonly record struct GlyphDef(int Width, byte[] Rows);

    /// <summary>
    /// Mapa de glyphs: caractere → definição bitmap.
    /// Cada byte[] tem exatamente GlyphHeight elementos.
    /// Bits são alinhados à esquerda (MSB = pixel mais à esquerda).
    /// </summary>
    private static readonly Dictionary<char, GlyphDef> GlyphMap = BuildGlyphMap();

    private static Dictionary<char, GlyphDef> BuildGlyphMap()
    {
        // Padrões definidos como strings para fácil verificação visual.
        // 'X' = pixel ligado, '.' = pixel desligado.
        // Baseados na análise pixel-a-pixel de termogramas FLIR E8-xt originais.

        var map = new Dictionary<char, GlyphDef>();

        Define(map, '0', 7,
            "..XXX..",
            ".XX.XX.",
            "XX...XX",
            "XX...XX",
            "XX.X.XX",
            "XX...XX",
            "XX...XX",
            "XX...XX",
            ".XX.XX.",
            "..XXX..");

        Define(map, '1', 5,
            "..X..",
            ".XX..",
            "X.X..",
            "..X..",
            "..X..",
            "..X..",
            "..X..",
            "..X..",
            "..X..",
            "XXXXX");

        Define(map, '2', 7,
            ".XXXXX.",
            "XX...XX",
            ".....XX",
            "....XX.",
            "...XX..",
            "..XX...",
            ".XX....",
            "XX.....",
            "XX...XX",
            "XXXXXXX");

        Define(map, '3', 7,
            ".XXXXX.",
            "XX...XX",
            ".....XX",
            ".....XX",
            "..XXXX.",
            ".....XX",
            ".....XX",
            ".....XX",
            "XX...XX",
            ".XXXXX.");

        Define(map, '4', 7,
            "....XX.",
            "...XXX.",
            "..X.XX.",
            ".X..XX.",
            "X...XX.",
            "XXXXXXX",
            "....XX.",
            "....XX.",
            "....XX.",
            "....XX.");

        Define(map, '5', 7,
            "XXXXXXX",
            "XX.....",
            "XX.....",
            "XXXXXX.",
            ".....XX",
            ".....XX",
            ".....XX",
            ".....XX",
            "XX...XX",
            ".XXXXX.");

        Define(map, '6', 7,
            "..XXXX.",
            ".XX....",
            "XX.....",
            "XX.....",
            "XXXXXX.",
            "XX...XX",
            "XX...XX",
            "XX...XX",
            ".XX.XX.",
            "..XXX..");

        Define(map, '7', 7,
            "XXXXXXX",
            "XX...XX",
            ".....XX",
            "....XX.",
            "...XX..",
            "..XX...",
            "..XX...",
            "..XX...",
            "..XX...",
            "..XX...");

        Define(map, '8', 7,
            ".XXXXX.",
            "XX...XX",
            "XX...XX",
            "XX...XX",
            ".XXXXX.",
            "XX...XX",
            "XX...XX",
            "XX...XX",
            "XX...XX",
            ".XXXXX.");

        Define(map, '9', 7,
            ".XXXXX.",
            "XX...XX",
            "XX...XX",
            "XX...XX",
            ".XXXXXX",
            ".....XX",
            ".....XX",
            ".....XX",
            "....XX.",
            ".XXXX..");

        Define(map, '.', 2,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "XX",
            "XX");

        Define(map, ',', 2,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "XX",
            ".X");

        Define(map, '-', 5,
            ".....",
            ".....",
            ".....",
            ".....",
            "XXXXX",
            "XXXXX",
            ".....",
            ".....",
            ".....",
            ".....");

        // Símbolo de grau (°)
        Define(map, '\u00B0', 5,
            ".XXX.",
            "X...X",
            "X...X",
            ".XXX.",
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".....");

        Define(map, 'C', 7,
            "..XXXX.",
            ".XX..XX",
            "XX.....",
            "XX.....",
            "XX.....",
            "XX.....",
            "XX.....",
            "XX.....",
            ".XX..XX",
            "..XXXX.");

        Define(map, 'F', 6,
            "XXXXXX",
            "XX....",
            "XX....",
            "XX....",
            "XXXXX.",
            "XX....",
            "XX....",
            "XX....",
            "XX....",
            "XX....");

        Define(map, '~', 7,
            ".......",
            ".......",
            ".......",
            ".XX..X.",
            "X..XX..",
            ".......",
            ".......",
            ".......",
            ".......",
            ".......");

        Define(map, ' ', 4,
            "....",
            "....",
            "....",
            "....",
            "....",
            "....",
            "....",
            "....",
            "....",
            "....");

        return map;
    }

    /// <summary>
    /// Converte padrões de string em bitmask e registra no mapa.
    /// </summary>
    private static void Define(Dictionary<char, GlyphDef> map, char c, int width, params string[] rows)
    {
        if (rows.Length != GlyphHeight)
            throw new ArgumentException($"Glyph '{c}' deve ter exatamente {GlyphHeight} linhas, tem {rows.Length}.");

        var bitmask = new byte[GlyphHeight];
        for (int r = 0; r < GlyphHeight; r++)
        {
            byte val = 0;
            for (int b = 0; b < Math.Min(8, rows[r].Length); b++)
            {
                if (rows[r][b] == 'X')
                    val |= (byte)(0x80 >> b);
            }
            bitmask[r] = val;
        }
        map[c] = new GlyphDef(width, bitmask);
    }

    /// <summary>
    /// Mede a largura total em pixels de uma string renderizada na escala indicada.
    /// </summary>
    public static int MeasureText(string text, int scale = 1)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int totalWidth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (GlyphMap.TryGetValue(text[i], out var glyph))
                totalWidth += glyph.Width;
            else
                totalWidth += 4; // fallback largura para caractere desconhecido

            if (i < text.Length - 1)
                totalWidth += CharSpacing;
        }
        return totalWidth * scale;
    }

    /// <summary>
    /// Desenha texto bitmap diretamente no buffer BGRA com escalonamento nearest-neighbor.
    /// </summary>
    /// <param name="pixels">Buffer BGRA de destino.</param>
    /// <param name="imageWidth">Largura da imagem em pixels.</param>
    /// <param name="imageHeight">Altura da imagem em pixels.</param>
    /// <param name="text">Texto a renderizar.</param>
    /// <param name="x">Posição X do canto superior esquerdo.</param>
    /// <param name="y">Posição Y do canto superior esquerdo.</param>
    /// <param name="scale">Fator de escala (1 = tamanho nativo 320×240).</param>
    /// <param name="r">Componente vermelho da cor do texto (0–255).</param>
    /// <param name="g">Componente verde da cor do texto (0–255).</param>
    /// <param name="b">Componente azul da cor do texto (0–255).</param>
    public static void DrawText(
        byte[] pixels,
        int imageWidth,
        int imageHeight,
        string text,
        int x,
        int y,
        int scale = 1,
        byte r = 245,
        byte g = 247,
        byte b = 243)
    {
        if (string.IsNullOrEmpty(text) || pixels is null) return;
        if (pixels.Length != imageWidth * imageHeight * 4) return;
        if (scale < 1) scale = 1;

        int cursorX = x;
        foreach (var ch in text)
        {
            if (!GlyphMap.TryGetValue(ch, out var glyph))
            {
                cursorX += (4 + CharSpacing) * scale; // skip unknown
                continue;
            }

            DrawGlyph(pixels, imageWidth, imageHeight, glyph, cursorX, y, scale, r, g, b);
            cursorX += (glyph.Width + CharSpacing) * scale;
        }
    }

    /// <summary>
    /// Desenha um glyph individual no buffer BGRA.
    /// Usa nearest-neighbor scaling para manter a estética bitmap.
    /// </summary>
    private static void DrawGlyph(
        byte[] pixels,
        int imageWidth,
        int imageHeight,
        GlyphDef glyph,
        int startX,
        int startY,
        int scale,
        byte r,
        byte g,
        byte b)
    {
        for (int row = 0; row < GlyphHeight; row++)
        {
            byte rowBits = glyph.Rows[row];
            if (rowBits == 0) continue; // linha vazia, pular

            for (int col = 0; col < glyph.Width; col++)
            {
                bool pixelOn = (rowBits & (0x80 >> col)) != 0;
                if (!pixelOn) continue;

                // Nearest-neighbor: cada pixel base vira um bloco scale×scale
                for (int sy = 0; sy < scale; sy++)
                {
                    int py = startY + (row * scale) + sy;
                    if (py < 0 || py >= imageHeight) continue;

                    for (int sx = 0; sx < scale; sx++)
                    {
                        int px = startX + (col * scale) + sx;
                        if (px < 0 || px >= imageWidth) continue;

                        int idx = (py * imageWidth + px) * 4;
                        pixels[idx]     = b;   // B
                        pixels[idx + 1] = g;   // G
                        pixels[idx + 2] = r;   // R
                        pixels[idx + 3] = 255; // A
                    }
                }
            }
        }
    }
}
