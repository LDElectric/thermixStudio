# Correções de Renderização — Thermix Studio

**Data:** 2026-05-31  
**Base:** Commit `947b8c2` (Fases 0-2 concluídas)

Correções aplicadas após análise de 33 termogramas FLIR E8xt Wifi (320×240, Iron 224 cores) e do FLIR0192 com discrepância EXIF vs barra visual.

---

## Correção #5: Borda preta de 1px na barra de escala

**Arquivo:** `FlirCameraUiOverlay.cs`

**Problema:** `DrawPaletteScaleBar` desenhava as cores da barra sem borda, diferente do comportamento das câmeras FLIR.

**Solução:**
- Adicionado método `DrawRectBorder` que desenha retângulo oco de 1px diretamente no buffer BGRA
- Chamado após ambas as ocorrências de `DrawPaletteScaleBar` (caminho programático e fallback)

---

## Correção #4: Sliders MSX/PiP/Blending mais sensíveis

**Arquivo:** `MainWindow.xaml`

**Problema:** Range 0–1 muito amplo para MSX (efeito satura em ~0.15). Sem `SmallChange`, os sliders pulavam em incrementos muito grandes.

**Solução:**
- **MSX:** `Minimum="0" Maximum="0.25" SmallChange="0.005" LargeChange="0.02" TickFrequency="0.01"`
- **Blending:** adicionado `SmallChange="0.01" LargeChange="0.1" TickFrequency="0.05"`
- **PiP:** adicionado `SmallChange="0.01" LargeChange="0.1" TickFrequency="0.05"`
- Todos com `IsSnapToTickEnabled="True"` para resposta tátil

---

## Correção #2: VisualScaleDetector — prioridade invertida

**Arquivos:** `VisualScaleDetector.cs`, `RadiometricMetadataExtractor.cs`

**Problema:** A hierarquia de detecção de escala visual estava invertida:
1. O visual-fit só era executado para câmeras FLIR (`IsFlir` guard)
2. `PaletteScaleMinC/MaxC` (do EXIF) era usado como fallback intermediário, mas pode divergir da barra queimada
3. `ImageTemperatureMin/Max` (Kelvin) não era convertido corretamente para °C como fallback

**Solução:**
- **Removido** o guard `IsFlir` — visual-fit agora roda para qualquer câmera com paleta embedded
- Nova hierarquia:
  1. Visual-fit da barra queimada (confiança ≥ 0.3)
  2. EXIF `ImageTemperatureMin/Max` (K → °C via `NormalizeExifTemperatureToCelsius`)
  3. Range da matriz radiométrica
- Método renomeado: `TryFitFlirVisualScaleToReference` → `TryFitVisualScaleToReference`
- `DetectorName`: "FLIR visual-fit" → "visual-fit"

---

## Correção #1: Logo FLIR — thresholds relaxados + connected-component

**Arquivo:** `FlirCameraUiOverlay.cs`

**Problema:** O logo FLIR do E8xt está burned-in **dentro** da área térmica (não em barra preta separada). JPEG compression e anti-aliasing faziam `sat > 35` nos pixels de borda, impedindo a preservação.

**Solução:**
- `OverlayFlirLogoOnly` totalmente reescrito:
  - Thresholds relaxados: `sat ≤ 55 && brightness ≥ 140` (eram `sat ≤ 35 && brightness > 170`)
  - Região expandida: y1=212→237, x1=1→62 (era y1=216→235, x1=2→57)
  - **Connected-component filter:** agrupa pixels candidatos via BFS 8-conectada; só preserva componentes com ≥ 6 pixels (elimina ruído)
- `CopyOriginalUiRectangleMasked` também atualizado:
  - `maxSaturation: 45 → 55`
  - `brightThreshold: 165 → 155`

---

## Correção #3: Renderização Thermal — 224 cores, DDE, limit colors

**Arquivos:** `ThermalPaletteEngine.cs`, `ThermalDataModels.cs`, `RadiometricMetadataExtractor.cs`

### 3a. Paleta de 224 cores

**Problema:** A câmera E8xt usa Iron com 224 entradas (`PaletteColors: 224`), mas o `TryBuildEmbeddedLut` sempre gerava 256 cores, causando deslocamento no mapeamento temperatura→cor.

**Solução:**
- `TryBuildEmbeddedLut` agora lê `metadata.PaletteColors` e reamostra a paleta embedded (interpolação linear de 256 → N cores)
- O nome da LUT reflete o tamanho real: `"Iron (embedded, 224c)"`

### 3b. Parâmetros DDE

**Problema:** Plateau fixo em 0.1% não replica fielmente o comportamento FLIR.

**Solução:**
- Plateau padrão: `0.1%` → `0.05%` (mais próximo do comportamento FLIR)
- Se `metadata.PaletteStretch` estiver disponível, mapeia para fração do plateau (`stretch / 50000`, range 0.01%-0.5%)

### 3c. Limites de clipping do sensor

**Problema:** Limites hardcoded (-40°C/280°C).

**Solução:**
- Adicionados campos `CameraTemperatureMinClip`/`CameraTemperatureMaxClip` ao `RadiometricMetadata`
- Extraídos do ExifTool JSON em `RadiometricMetadataExtractor`
- `RenderThermalWithPaletteAsync` agora usa os valores da câmera quando disponíveis, com fallback para os defaults FLIR

---

## Arquivos modificados

| Arquivo | Correção |
|--------|---------|
| `FlirCameraUiOverlay.cs` | #1, #5 |
| `MainWindow.xaml` | #4 |
| `VisualScaleDetector.cs` | #2 |
| `ThermalPaletteEngine.cs` | #3a, #3b, #3c |
| `ThermalDataModels.cs` | #3c (novos campos) |
| `RadiometricMetadataExtractor.cs` | #2, #3c |

**Compilação:** 0 erros, 0 avisos ✅
