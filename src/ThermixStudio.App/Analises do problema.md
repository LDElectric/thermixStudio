Peguei o problema e levei para o Chat GPT e para o Gemini e para o Claude.
ChatGPT falou:
Pelo que você descreveu, o problema não é a LUT em si. O problema é a forma como ela foi aprendida.

Existem três coisas misturadas:

1. **Dados térmicos reais** (matriz radiométrica)

2. **Mapeamento de cores da câmera** (o que você quer preservar)

3. **Overlay da câmera** (o que você quer eliminar)

No pipeline antigo, a LUT foi treinada usando pixels do JPEG inteiro. O JPEG contém simultaneamente 2 e 3. Portanto a LUT aprendeu partes do overlay junto com as cores térmicas.

---

### O que eu faria

Em vez de eliminar a LUT, eu eliminaria apenas as regiões contaminadas durante a calibração.

Algo como:

```
```
JPEG original
    ↓
Mascarar overlay
    ↓
Amostrar apenas área térmica válida
    ↓
Construir LUT
    ↓
Aplicar LUT na matriz radiométrica
```
```

Assim você mantém:

```
```
Matriz → LUT calibrada → RGB
```
```

sem precisar copiar nenhum pixel do JPEG para o resultado.

---

### Por que o SSIM era tão alto?

Porque a LUT estava aprendendo a resposta exata daquela câmera:

```
```
Temperatura normalizada 0.00 → RGB(...)
Temperatura normalizada 0.01 → RGB(...)
Temperatura normalizada 0.02 → RGB(...)
...
Temperatura normalizada 1.00 → RGB(...)
```
```

Na prática ela estava reproduzindo:

- gama interno

- compressão tonal

- white boost

- contraste local

- ajustes da FLIR

Tudo embutido.

Por isso você chegava em:

```
```
SSIM 0.91
SSIM 0.95
```
```

Enquanto a Iron JSON produz apenas:

```
```
Temperatura → cor da paleta
```
```

sem os ajustes específicos daquele firmware.

---

### A solução mais robusta

Separar o processo em duas fases.

#### Fase 1 - Aprender LUT

Uma única vez:

```
```
Radiométrico + JPEG FLIR
         ↓
Detectar área térmica
         ↓
Ignorar overlay
         ↓
Gerar LUT
         ↓
Salvar LUT
```
```

Exemplo:

```
```
FLIR_E8_Iron.lut
FLIR_C5_Iron.lut
FLIR_T540_Iron.lut
```
```

---

#### Fase 2 - Render normal

Sempre:

```
```
Matriz radiométrica
        ↓
LUT já aprendida
        ↓
RGB final
```
```

Sem usar JPEG.

Sem overlay.

Sem contaminação.

---

### Outra possibilidade ainda melhor

Pelo que você vem investigando, a FLIR provavelmente não usa apenas uma paleta de 256 cores.

O fluxo parece mais próximo de:

```
```
Normalize
    ↓
Level/Span
    ↓
Gamma
    ↓
Contrast
    ↓
Palette
    ↓
White Boost
    ↓
RGB
```
```

Ou seja, sua LUT antiga talvez estivesse aprendendo:

```
```
Palette + Gamma + WhiteBoost
```
```

ao mesmo tempo.

Por isso a versão JSON parece "fosca".

Você está reproduzindo apenas:

```
```
Palette
```
```

e não:

```
```
Palette + Tone Mapping
```
```

---

### O que eu investigaria primeiro

Você mencionou um dado muito importante:

```
```
FLIR0192 = 0.91 → 0.40
2.jpg = 0.95 → 0.82
```
```

A queda gigantesca da FLIR0192 sugere que essa imagem depende fortemente de algum processamento tonal da câmera.

Eu tentaria reconstruir a LUT antiga, mas gerando-a apenas da região térmica central, excluindo:

- barra lateral

- temperaturas da escala

- logo FLIR

- crosshair

- indicadores

Se o SSIM voltar para algo como:

```
```
0.88 - 0.93
```
```

você terá provado que o problema nunca foi a LUT. O problema era o conjunto de pixels usado para treiná-la.

Nesse cenário, a melhor arquitetura seria:

```
```
Radiométrico
    ↓
LUT calibrada da câmera
    ↓
RGB térmico limpo
    ↓
Overlay recriado em vetor (SkiaSharp)
```
```

porque você mantém as cores "pixel-perfect" do pipeline antigo sem herdar nenhum artefato do JPEG original.

Gemini falou:

Esse é um excelente problema de processamento de imagens. Você está lidando com o clássico dilema entre um *ground truth* preciso, porém "sujo" (Pipeline antigo), e um processo limpo, porém genérico (Pipeline atual).

O problema central não é a arquitetura do pipeline atual (que é superior por ser previsível e não depender de amostragem em tempo de execução), mas sim o **dicionário de cores (JSON)** que o alimenta. A solução ideal é trazer a fidelidade do pipeline 0.9 para o pipeline atual, extraindo uma "LUT Dourada" (Golden LUT) limpa e estática.

Aqui estão as abordagens para resolver isso, da mais robusta à mais dinâmica:

### 1. Extração Offline e Limpeza da LUT (Abordagem Recomendada)
Como a paleta de cores da câmera (ex: FLIR0192) é estática e não muda de foto para foto, você não precisa aprender a LUT em tempo de execução para cada imagem. Você pode extrair a paleta perfeitamente calibrada uma única vez, limpá-la e salvá-la como um novo JSON para alimentar o seu Pipeline Atual.

**Como fazer:**

- Pegue a LUT bruta gerada pelo commit `3b49ab6`.
- Plote os valores RGB em função da temperatura (ou índice radiométrico). Uma paleta térmica real forma uma curva contínua e suave no espaço 3D (RGB).
- **Identifique os Outliers:** Os pixels do overlay (letras brancas, retículo preto, marcações em verde/vermelho puro) aparecerão como "espinhos" fora dessa curva suave.
- **Interpolação:** Remova esses outliers e interpole os valores RGB adjacentes para preencher as lacunas deixadas pelo overlay.
- Exporte essa curva suavizada como um novo `FLIR0192_Iron_Palette.json`.

### 2. Máscara de Exclusão Espacial (Spatial Masking)
Se você precisa continuar gerando a LUT dinamicamente (por exemplo, se cada câmera tem um mapeamento ligeiramente diferente que precisa ser lido do próprio JPEG), a maneira mais simples de evitar a contaminação é não olhar para o overlay.

**Como fazer:**

- A maioria dos overlays da FLIR tem posições fixas (a barra de escala fica na extremidade direita, o retículo cruza o centro exato, os textos de temperatura ficam nos cantos).
- No momento do *JPEG sampling* ($T \rightarrow \text{RGB}$), implemente *Bounding Boxes* de exclusão.
- Se a coordenada $(x, y)$ do pixel amostrado cair dentro das zonas conhecidas de overlay, simplesmente ignore esse pixel na construção da LUT.

### 3. Filtragem Estatística no Sampling (Moda / Mediana)
Se as posições do overlay forem dinâmicas e imprevisíveis, você pode usar a estatística a seu favor durante a amostragem, já que o overlay representa uma minoria absoluta dos pixels na imagem.

**Como fazer:**

- Em vez de atribuir a cor diretamente (`LUT[Temp] = RGB_amostrado`), agrupe todos os pixels da imagem pela mesma temperatura.
- Para uma dada temperatura $T_x$, você terá uma lista de cores (ex: 95% serão a cor térmica correta, 5% serão a cor do overlay que calhou de cair naquela isoterma).
- Calcule a **Mediana** (ou a cor mais frequente/Moda) dos canais R, G e B para aquela temperatura.
- A cor do overlay será descartada estatisticamente por ser um *outlier* numérico, resultando em uma LUT limpa construída dinamicamente.

O Pipeline Atual é o caminho correto arquiteturalmente para garantir manutenibilidade e evitar artefatos em produção. A chave é apenas fornecer a ele os parâmetros exatos do hardware.

Claude Falou:

Entendo o dilema perfeitamente. Você precisa da calibração de cores do pipeline 0.9 sem a contaminação do overlay. O caminho é **aprender a LUT apenas dos pixels "limpos"** do JPEG — ou seja, excluindo as regiões onde o overlay está baked-in.

Deixa eu entender melhor a estrutura antes de propor a solução:
e eu respondi:
P: O overlay do JPEG (crosshair, barra de escala, dígitos) ocupa regiões previsíveis/fixas na imagem?
R: Sim, posição fixa conhecida (bordas, cantos)

P: Você já tem como detectar pixels de overlay programaticamente (ex: cor sólida, contraste alto, clustering)?
R: Não, ainda não tenho isso

P: A LUT é aprendida como mapeamento 1D (temperatura → RGB via interpolação) ou 2D/3D (lookup por valor de pixel)?
R: Não sei exatamente como foi implementada

E eu enviei para o claude o ThermaPaletteEngine para ele analisar, ele analisou em falou:
Agora tenho o quadro completo. Análise cirúrgica:

---

### O diagnóstico real

O pipeline 0.9 **não tinha uma LUT aprendida do JPEG**. O que ele fazia era diferente — e o problema é mais sutil:

**O fluxo era:**

```
JPEG original → DetectPaletteAsync() → amostra 500px do centro → identifica paleta
Matriz radiométrica → RenderWithProfileAsync() → LUT JSON → RGB
FlirCameraUiOverlay → copia regiões do JPEG (barra de escala, retículo) → cola no render
```

A LUT **nunca aprendeu cores do JPEG diretamente**. O que acontecia era que o `FlirCameraUiOverlay` **copiava blocos de pixels do JPEG original** (com overlay baked-in) direto para o render final — daí as manchas e pixels pretos.

**O SSIM 0.91 vinha de outra coisa:** o `ProcessSmartHD` fazia remapeamento pixel-a-pixel do JPEG via nearest-neighbor na LUT source → ratio → LUT target. Isso produzia cores fiéis porque usava o próprio JPEG renderizado pela câmera como referência — e o JPEG da câmera tem o mapeamento radiométrico→cor exato da câmera.

---

### A solução: LUT calibrada por regressão JPEG→temperatura, sem overlay

O que você precisa é um **calibrador** que:

1. Amostra pixels do JPEG **apenas nas regiões limpas** (excluindo bordas/overlay)

2. Para cada pixel JPEG amostrado, consulta a temperatura correspondente na matriz radiométrica

3. Constrói o mapeamento `temperatura → RGB` como LUT 1D de 256 entradas por interpolação

Isso dá calibração pixel-perfect sem depender do overlay:

csharp

```
/// 
/// Constrói uma LUT de 256 entradas calibrada pelos pixels limpos do JPEG,
/// excluindo regiões de overlay (posição fixa conhecida nas bordas).
/// 
public static ThermalPaletteLutData BuildCalibratedLutFromJpeg(
    Bitmap jpeg,
    double[,] temperatures,
    OverlayMask mask)          // define as regiões a excluir
{
    int w = jpeg.Width;
    int h = jpeg.Height;

    // Coleta amostras: (temperatura normalizada → RGB)
    var samples = new List();

    double tMin = double.MaxValue, tMax = double.MinValue;
    for (int y = 0; y  tMax) tMax = t;
        }

    double range = Math.Max(0.01, tMax - tMin);

    var rand = new Random(42);
    for (int i = 0; i  a.norm.CompareTo(b.norm));

    var colors = new List(256);
    for (int bin = 0; bin  Math.Abs(s.norm - binCenter)  0)
        {
            // Média ponderada (mais próximos pesam mais)
            double sumW = 0, sumR = 0, sumG = 0, sumB = 0;
            foreach (var s in nearby)
            {
                double w2 = 1.0 / (Math.Abs(s.norm - binCenter) + 1e-6);
                sumW += w2;
                sumR += s.r * w2;
                sumG += s.g * w2;
                sumB += s.b * w2;
            }
            colors.Add([(int)(sumR/sumW), (int)(sumG/sumW), (int)(sumB/sumW)]);
        }
        else
        {
            // Interpola do bin anterior/posterior
            colors.Add(colors.Count > 0 ? colors[^1] : [0, 0, 0]);
        }
    }

    return new ThermalPaletteLutData { Name = "Calibrated", Rgb = colors };
}
```

E o `OverlayMask` para posição fixa:

csharp

```
public record OverlayMask(
    int TopRows,      // ex: 20px — barra de título/data
    int BottomRows,   // ex: 40px — barra de temperatura + escala
    int LeftCols,     // ex: 0
    int RightCols)    // ex: 30px — escala lateral
{
    public bool IsOverlay(int x, int y, int w, int h) =>
        y = h - BottomRows ||
        x = w - RightCols;
}
```

---

### Onde encaixar no pipeline atual

```
JPEG limpo (sem FlirCameraUiOverlay)
    │
    ▼
BuildCalibratedLutFromJpeg(jpeg, temperatures, mask)
    │ → ThermalPaletteLutData com 256 entradas calibradas
    │
    ▼
ThermalRenderEngine.Render()  ← usa essa LUT no lugar da JSON genérica
    │
    ▼
RGB pixel-perfect, sem overlay
```

A LUT calibrada substitui diretamente a `iron_lut.json` — a assinatura `ThermalPaletteLutData` já é compatível com `WriteInterpolatedLutColor`. Você usa a LUT calibrada como `ThermalPalette.Original` ou cria um `ThermalPalette.Calibrated` novo.