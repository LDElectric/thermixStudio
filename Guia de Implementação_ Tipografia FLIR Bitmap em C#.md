# Guia de Implementação: Tipografia FLIR Bitmap em C#

## Análise Técnica
A fonte utilizada nos termogramas FLIR E8-xt é de fato uma **fonte bitmap (raster)** de tamanho fixo, otimizada para displays de baixa resolução (320x240). 

### Características Confirmadas:
- **Altura do Glyph:** 16 pixels (corpo principal).
- **Largura:** Variável (Proporcional), média de 7-10px.
- **Anti-aliasing:** Praticamente inexistente na origem, mas o JPEG introduz artefatos. A reconstrução deve ser binária (pixel ligado/desligado) ou com tons de cinza muito limitados.
- **Cor:** RGB(245, 247, 243) para o texto, sobre fundo preto semitransparente.

## Reconstrução dos Glyphs (Exemplos)

Abaixo, a representação pixel a pixel baseada na análise dos termogramas:

### Número '2' (7x16px)
```text
. . X X X . .
. X . . . X .
. . . . . X .
. . . . X . .
. . . X . . .
. . X . . . .
. X . . . . .
. X X X X X .
```

### Número '7' (8x16px)
```text
X X X X X X X
. . . . . . X
. . . . . X .
. . . . X . .
. . . X . . .
. . X . . . .
. X . . . . .
. X . . . . .
```

## Como aplicar no C# (WinForms/WPF/SkiaSharp)

Como não é uma fonte `.ttf`, a melhor forma de aplicar é usando um **SpriteSheet** ou definindo os **Bitmaps manualmente**.

### Opção A: Usando System.Drawing (GDI+)

```csharp
public void DrawFlirText(Graphics g, string text, int x, int y) {
    // Carregue uma imagem contendo todos os caracteres (0-9, ., °, C)
    Bitmap spriteSheet = new Bitmap("flir_font.png");
    int charWidth = 8;
    int charHeight = 16;

    for (int i = 0; i < text.Length; i++) {
        char c = text[i];
        int index = GetCharIndex(c); // Mapeia o caractere para a posição no sprite
        Rectangle srcRect = new Rectangle(index * charWidth, 0, charWidth, charHeight);
        g.DrawImage(spriteSheet, new Rectangle(x + (i * charWidth), y, charWidth, charHeight), srcRect, GraphicsUnit.Pixel);
    }
}
```

### Opção B: Matriz de Pixels (Para precisão absoluta)

Você pode armazenar os caracteres como arrays de bytes (bitmasks) e desenhar pixel a pixel:

```csharp
byte[] digit2 = new byte[] { 0x3E, 0x41, 0x01, 0x02, 0x04, 0x08, 0x10, 0x7F };
// Lógica para iterar os bits e pintar pixels no Bitmap
```

## Nota
Adicionalmente confira o arquivo `flir_font_sprite_final_grid.png no workspace
