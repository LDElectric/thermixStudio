# Relatório Comparativo: Ajuste de Escala em Termogramas
## FLIR Tools / Thermal Studio vs Thermix Studio

> **Data:** 2026-06-05
> **Objetivo:** Verificar se o Thermix implementa corretamente o ajuste de escala ao importar termogramas, comparando com o comportamento do FLIR Tools / Thermal Studio.

---

## 1. Metodologia da Pesquisa

A pesquisa baseou-se em três fontes:

| Fonte | Tipo | Conteúdo |
|-------|------|----------|
| **ExifTool TagNames/FLIR.html** | Documentação técnica | Estrutura completa dos metadados FLIR (FFF, FPF, CameraInfo, PaletteInfo) |
| **comportamento.txt** (workspace) | Análise do usuário | Comportamento observado do FLIR Tools ao importar e ajustar escala |
| **Código-fonte Thermix** | Leitura semântica | Pipeline completo: importação → detecção → renderização → ajuste manual |
| **Documentos internos** | Análises técnicas | CORRECOES_RENDERIZACAO, Fluxo_Termograma_Renderizacao, análises ChatGPT/Claude |

---

## 2. Como o FLIR Tools / Thermal Studio se Comporta

### 2.1 Metadados que a câmera grava no arquivo

Com base na documentação ExifTool (formato FLIR FFF/FPF), a câmera grava os seguintes campos relevantes para escala:

```
EXIF FLIR Maker Notes:
├── ImageTemperatureMin / ImageTemperatureMax    ← Level/Span em K (pode ser °C ou °F)
├── CameraTemperatureRangeMin / Max               ← Range do sensor
├── CameraTemperatureMinClip / MaxClip             ← Limites de clipping
├── CameraTemperatureMinSaturated / MaxSaturated   ← Limites de saturação
│
├── CameraInfo (FFF segmento 0x0020):
│   ├── PlanckR1, PlanckR2, PlanckB, PlanckF, PlanckO  ← Coeficientes radiométricos
│   ├── RawValueRangeMin / Max / Median / Range         ← Range dos valores RAW
│   └── CameraTemperatureRangeMin / Max                 ← Range do sensor
│
├── PaletteInfo (FFF segmento 0x0022):
│   ├── PaletteName, PaletteFileName, PaletteColors     ← Identificação da paleta
│   ├── PaletteMethod, PaletteStretch                   ← Método de stretch
│   ├── AboveColor, BelowColor                          ← Cores para fora da escala (YCrCb)
│   ├── OverflowColor, UnderflowColor                   ← Cores para clipping do sensor (YCrCb)
│   └── Palette                                         ← 224/256 entradas YCrCb
│
└── FPF (FLIR Public Format):
    ├── CameraScaleMin / Max           ← Escala configurada na câmera
    ├── CalculatedScaleMin / Max       ← Escala calculada pelo algoritmo da câmera
    └── ActualScaleMin / Max           ← Escala efetivamente aplicada
```

### 2.2 Comportamento na abertura do arquivo

**Regra de ouro documentada:** O FLIR Tools / Thermal Studio **sempre abre o termograma usando os parâmetros de Level/Span gravados no arquivo**, NÃO recalcula a escala automaticamente.

```
Fluxo FLIR Tools / Thermal Studio:

1. Lê ImageTemperatureMin/Max do EXIF
   ↓
2. Converte para °C se necessário (K → °C subtraindo 273.15)
   ↓
3. Usa esses valores como MinEscala / MaxEscala iniciais
   ↓
4. Aplica a paleta usando normalização linear:
     normalized = (TemperaturaPixel - MinEscala) / (MaxEscala - MinEscala)
   ↓
5. Valores < MinEscala → cor BelowColor (Priority 2)
   Valores > MaxEscala → cor AboveColor (Priority 2)
   Valores < SensorMinClip → cor UnderflowColor (Priority 1)
   Valores > SensorMaxClip → cor OverflowColor (Priority 1)
   ↓
6. Exibe a imagem — visualmente idêntica à da câmera no momento da captura
```

### 2.3 Comportamento ao ajustar a escala manualmente

Quando o usuário move os sliders de temperatura no FLIR Tools:

```
1. MinEscala e MaxEscala são alterados
2. NENHUM dado radiométrico é modificado
3. Apenas o mapeamento de cores é recalculado:
     normalized = (TemperaturaPixel - novoMin) / (novoMax - novoMin)
4. A matriz de temperaturas permanece intacta
5. Os metadados do arquivo original NÃO são alterados
   (a menos que o usuário salve explicitamente)
```

**Efeitos visuais de cada ajuste:**

| Ajuste | Efeito |
|--------|--------|
| ↓ Min | Mais detalhes nas áreas frias; contraste geral reduz |
| ↑ Min | Áreas frias colapsam para cor mínima; áreas médias ganham contraste |
| ↑ Max | Contraste geral reduz; transições mais suaves |
| ↓ Max | Áreas quentes saturam; contraste em áreas médias aumenta |

### 2.4 Auto vs Manual na câmera

- **Captura Auto:** A câmera calcula Level/Span baseado na cena (percentis, distribuição térmica) e **salva esses valores** no EXIF
- **Captura Manual:** O operador ajusta Level/Span e a câmera **salva esses valores** no EXIF
- **Em ambos os casos:** O software abre com os valores salvos — não recalcula

---

## 3. Como o Thermix Studio se Comporta

### 3.1 Arquitetura do Pipeline de Escala

O Thermix tem uma arquitetura em 3 camadas para determinação da escala:

```
┌─────────────────────────────────────────────────────┐
│               CAMADA 1: DETECÇÃO                     │
│          (na importação do termograma)                │
├─────────────────────────────────────────────────────┤
│  VisualScaleDetector.DetectAsync()                   │
│  ├── Prioridade 1: ImageTemperatureMin/Max (K→°C)   │
│  │   Confiança: 0.9 | Source: ExifImageTemperature  │
│  ├── Prioridade 2: Visual-fit contra JPEG original  │
│  │   Confiança: ≥0.3 | Source: VisualFitToReference │
│  └── Prioridade 3: Range da matriz radiométrica     │
│      Confiança: 0.2 | Source: MatrixRange           │
│                                                      │
│  Resultado armazenado em:                            │
│  metadata.VisualScaleMinC / VisualScaleMaxC          │
│  metadata.VisualScaleSource                          │
│  metadata.VisualScaleConfidence                      │
└─────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────┐
│               CAMADA 2: RENDERIZAÇÃO                 │
│          (MainViewModel.UpdateDisplayImage)           │
├─────────────────────────────────────────────────────┤
│  GetPreferredThermalRange()                          │
│  ├── 1. VisualScaleMinC/MaxC (da camada 1)          │
│  ├── 2. PaletteScaleMinC/MaxC (do EXIF)              │
│  └── 3. Range da matriz (fallback)                   │
│                                                      │
│  Se AutoScaleEnabled = true:                         │
│    appliedMin/Max = GetPreferredThermalRange()       │
│    E sobrescreve com VisualScale se disponível       │
│                                                      │
│  Se AutoScaleEnabled = false:                        │
│    appliedMin/Max = LevelMinC / LevelMaxC (sliders)  │
└─────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────┐
│               CAMADA 3: PERFIL DE RENDER             │
│          (RenderProfile + ThermalPaletteEngine)       │
├─────────────────────────────────────────────────────┤
│  RenderProfile.FromMetadata()                        │
│  ├── LevelMinC / LevelMaxC ← escala aplicada         │
│  ├── PaletteScaleMinC / MaxC ← thresholds Priority 2│
│  │   (BelowColor/AboveColor — do EXIF)               │
│  ├── SensorMinC / MaxC ← thresholds Priority 1       │
│  │   (UnderflowColor/OverflowColor — do EXIF)        │
│  ├── ApplyPlanckTransform ← se Planck R1/R2/B/F/O   │
│  ├── ApplyPaletteStretch ← PaletteStretch × 0.25    │
│  ├── StretchBlend ← interpolado                      │
│  └── ApplyWhiteBoost ← >94% do range                 │
└─────────────────────────────────────────────────────┘
```

### 3.2 Modelo de Dados — Duas Escalas Distintas

O Thermix distingue **explicitamente** dois conceitos de escala no `RadiometricMetadata`:

| Campo | Origem | Significado |
|-------|--------|-------------|
| `PaletteScaleMinC/MaxC` | EXIF `ImageTemperatureMin/Max` (K→°C) | Level/Span da câmera — thresholds para BelowColor/AboveColor |
| `VisualScaleMinC/MaxC` | `VisualScaleDetector` (pixel comparison) | Escala visual detectada da barra queimada no JPEG |

**Esta distinção é correta e importante.** O comentário no `RenderProfile.cs` (linha 101-102) documenta isso explicitamente:

> "BelowColor para t < PaletteScaleMin, AboveColor para t > PaletteScaleMax. NÃO confundir com VisualScaleMinC/MaxC (detectado da barra de escala na UI)."

### 3.3 Comportamento na Abertura (AutoScale)

```csharp
// MainViewModel.Rendering.cs ~linha 100-122
if (AutoScaleEnabled)
{
    var (autoMin, autoMax) = GetPreferredThermalRange(_loadedImage);
    appliedMin = autoMin;
    appliedMax = autoMax;

    // Se região de auto-ajuste existe, usa-a
    if (_autoAdjustRegion.HasValue) { ... }

    LevelMinC = appliedMin;
    LevelMaxC = appliedMax;

    // SOBRESCREVE com VisualScale se disponível
    if (_loadedImage.Metadata.VisualScaleMinC.HasValue &&
        _loadedImage.Metadata.VisualScaleMaxC.HasValue)
    {
        appliedMin = _loadedImage.Metadata.VisualScaleMinC.Value;
        appliedMax = _loadedImage.Metadata.VisualScaleMaxC.Value;
        LevelMinC = appliedMin;
        LevelMaxC = appliedMax;
    }
}
```

### 3.4 Comportamento no Ajuste Manual

```csharp
// MainViewModel.Rendering.cs ~linha 690-700
[RelayCommand]
private void IncrementLevelMin()
{
    AutoScaleEnabled = false;  // ← Desliga auto ao mover slider
    LevelMinC += 0.1;
}
```

---

## 4. Análise Comparativa

### 4.1 O que o Thermix faz IGUAL ao FLIR Tools ✅

| Aspecto | FLIR Tools | Thermix | Status |
|---------|-----------|---------|--------|
| Lê Level/Span do EXIF | `ImageTemperatureMin/Max` | `PaletteScaleMinC/MaxC` ← mesma fonte | ✅ Correto |
| Converte K→°C | Sim | `NormalizeExifTemperatureToCelsius()` | ✅ Correto |
| Abre com escala do arquivo | Sim (AutoScaleEnabled=true) | `GetPreferredThermalRange()` prioriza VisualScale ou PaletteScale | ✅ Correto |
| Não recalcula auto na abertura | Sim | Só recalcula se `AutoScaleEnabled=true` E sem VisualScale | ✅ Correto |
| Ajuste manual só altera mapeamento | Sim | Apenas `LevelMinC/LevelMaxC` mudam | ✅ Correto |
| Dados radiométricos intactos | Sim | Matriz `Temperatures[,]` nunca alterada | ✅ Correto |
| Usa BelowColor/AboveColor do EXIF | Sim | Priority 2 no `RenderProfile` | ✅ Correto |
| Usa UnderflowColor/OverflowColor | Sim | Priority 1 no `RenderProfile` | ✅ Correto |
| Respeita PaletteStretch da câmera | Sim | `stretchBlend = PaletteStretch * 0.25` | ✅ Correto |
| Usa paleta embedded quando disponível | Sim | `ShouldUseFlirEmbeddedDisplayMapping()` | ✅ Correto |

### 4.2 O que o Thermix faz DIFERENTE (e correto) ✅

| Aspecto | FLIR Tools | Thermix | Vantagem |
|---------|-----------|---------|----------|
| **Visual-fit da barra de escala** | Não tem | `VisualScaleDetector` com 3 prioridades | Recupera escala mesmo sem EXIF completo |
| **Separação VisualScale vs PaletteScale** | Não distingue | Dois campos separados no modelo | Claridade arquitetural; evita confusão |
| **Confiança da detecção** | Não reporta | Score 0.0–0.95 armazenado | Transparência para o usuário |
| **Fallback para matriz** | Provável mas não documentado | `MatrixRange` com confiança 0.2 | Funciona para qualquer formato |

### 4.3 Pontos de Atenção ⚠️

#### 4.3.1 Dupla sobrescrita no AutoScale

**Local:** `MainViewModel.Rendering.cs` linhas 100-122

```csharp
// Problema: VisualScale sobrescreve o resultado do GetPreferredThermalRange
// mesmo que este já tenha retornado VisualScale (prioridade 1).
// Isso é redundante mas não causa erro — apenas confirma o valor.
```

**Impacto:** Nenhum erro funcional, mas o código é redundante. `GetPreferredThermalRange()` já retorna `VisualScale` na prioridade 1, então a segunda verificação nas linhas 117-122 é desnecessária.

**Recomendação:** Simplificar removendo a segunda verificação ou consolidando a lógica.

#### 4.3.2 PaletteScale vs VisualScale para AutoScale

**Análise:** O `GetPreferredThermalRange()` retorna `VisualScaleMinC/MaxC` como prioridade 1 e `PaletteScaleMinC/MaxC` como prioridade 2. Isso está **correto para fidelidade visual**, mas vale notar:

- `VisualScale` = escala queimada no JPEG (operador pode ter ajustado manualmente)
- `PaletteScale` = Level/Span original do EXIF
- Se o VisualScaleDetector teve alta confiança (≥0.9 via EXIF), ambos devem coincidir
- Se divergem (confiança <0.9), o visual-fit pode estar errado e o PaletteScale é mais confiável

**Recomendação:** Considerar usar `PaletteScale` quando `VisualScaleConfidence < 0.9`, pois a fonte EXIF é determinística.

#### 4.3.3 RenderProfile: BelowColor/AboveColor usam PaletteScaleMinC/MaxC

**Local:** `RenderProfile.cs` linhas 59-65 e `ThermalPaletteEngine.cs` linhas ~395-405

```csharp
// Doc FLIR: BelowColor para t < PaletteScaleMin, AboveColor para t > PaletteScaleMax
// NÃO confundir com VisualScale (detectado da barra de UI na imagem)
double belowThreshold = profile.PaletteScaleMinC ?? minT;
double aboveThreshold = profile.PaletteScaleMaxC ?? maxT;
```

**Análise:** Correto. Os thresholds de BelowColor/AboveColor devem vir do EXIF (`PaletteScale`), não da detecção visual (`VisualScale`). Se o usuário ajusta a escala manualmente, os novos valores (`LevelMinC/LevelMaxC`) são usados como fallback.

#### 4.3.4 Ajuste manual desliga AutoScale imediatamente

**Local:** `MainViewModel.Rendering.cs` linhas 690-700

```csharp
private void IncrementLevelMin()
{
    AutoScaleEnabled = false;  // ← Correto: FLIR Tools também faz isso
    LevelMinC += 0.1;
}
```

**Análise:** Correto. No FLIR Tools, mover qualquer slider de temperatura também desativa o modo "Automático" e passa para "Manual".

---

## 5. Verificação do Pipeline de Renderização

### 5.1 Caminho principal (RenderWithProfileAsync)

```
Temperatura[y,x]
    │
    ├─ Priority 1: t < SensorMinC? → UnderflowColor (YCrCb→RGB do EXIF)
    ├─ Priority 1: t > SensorMaxC? → OverflowColor  (YCrCb→RGB do EXIF)
    │
    ├─ [Planck] SignalFromTemp(t) → val
    │    └─ Se ApplyPlanckTransform e Planck R1/R2/B/F/O presentes
    │
    ├─ Normalização: (val - minVal) / range → [0, 1]
    │
    ├─ Priority 2: t < PaletteScaleMinC? → BelowColor
    ├─ Priority 2: t > PaletteScaleMaxC? → AboveColor
    │
    ├─ [PaletteStretch] ApplyFlirPaletteStretch(normalized)
    │    └─ Blend: stretched × StretchBlend + linear × (1 − StretchBlend)
    │
    ├─ [WhiteBoost] SmoothStep(0.94, 0.99, normalized) × 0.015
    │
    └─ LUT: WriteInterpolatedLutColor(lut, normalized) → BGRA
```

### 5.2 Caminho Embedded FLIR (RenderFlirEmbeddedDisplayWithProfile)

Ativado quando:
1. `EmbeddedPaletteBgra` tem 256×4 bytes
2. `IsFlir(metadata)` = true
3. `paletteName` = "Original" ou coincide com `PaletteName`/`DetectedPalette`

Este caminho é **mais simples e fiel** — não aplica Planck (a paleta embedded já está no espaço de sinal da câmera), aplica apenas PaletteStretch + WhiteBoost.

### 5.3 Verificação da correção DDE/Stretch

**Histórico (CORRECOES_RENDERIZACAO_2026-05-31.md):**
- Correção #3b: Plateau padrão reduzido de 0.1% → 0.05%
- PaletteStretch mapeado como `stretch / 50000` (range 0.01%-0.5%)
- Análise ChatGPT/Claude: pipeline anterior com HE+DDE+MidLift degradava fidelidade

**Estado atual:** O pipeline foi simplificado. HE foi removido. Apenas `ApplyFlirPaletteStretch` (curva não-linear FLIR) + WhiteBoost opcional. Isso está **muito mais próximo do comportamento real do FLIR Tools**.

---

## 6. Conclusões

### 6.1 O Thermix está fazendo certo?

**Sim, na essência.** O comportamento do Thermix é consistente com o FLIR Tools / Thermal Studio nos seguintes aspectos fundamentais:

1. ✅ **Abre com a escala do arquivo** (não recalcula automaticamente)
2. ✅ **Usa ImageTemperatureMin/Max do EXIF** como fonte primária de Level/Span
3. ✅ **Ajuste manual não modifica dados radiométricos**
4. ✅ **Below/Above/Underflow/Overflow colors** são respeitadas conforme documentação FLIR
5. ✅ **PaletteStretch** é aplicado com intensidade proporcional ao metadado
6. ✅ **Mover slider desliga AutoScale** (comportamento idêntico ao FLIR Tools)

### 6.2 Diferenciais do Thermix

O Thermix vai **além** do FLIR Tools em:

- **VisualScaleDetector com 3 níveis de fallback** — robustez para arquivos com metadados danificados
- **Separação arquitetural clara** entre PaletteScale (EXIF) e VisualScale (detectado)
- **Modelo de confiança** para cada fonte de escala
- **Perfil de render por imagem** (`RenderProfile`) — sem hardcodes globais

### 6.3 Recomendações

| # | Recomendação | Prioridade | Impacto |
|---|-------------|-----------|---------|
| 1 | Remover dupla verificação de VisualScale nas linhas 117-122 de `MainViewModel.Rendering.cs` (redundante) | Baixa | Limpeza de código |
| 2 | Usar `PaletteScale` como fallback quando `VisualScaleConfidence < 0.9` | Média | Precisão em edge cases |
| 3 | Documentar que `VisualScaleSource.ExifImageTemperature` (confiança 0.9) é o equivalente funcional ao comportamento do FLIR Tools | Baixa | Documentação |
| 4 | Validar com termogramas de câmeras não-FLIR (Fluke, Hikvision, InfiRay) se os metadados equivalentes existem | Média | Compatibilidade |

### 6.4 Verdicto Final

> **O Thermix implementa corretamente o ajuste de escala de termogramas.** O comportamento é fiel ao FLIR Tools / Thermal Studio nos aspectos essenciais: abertura com escala do arquivo, preservação de dados radiométricos, uso correto dos metadados EXIF FLIR (Planck, PaletteStretch, limit colors), e desacoplamento entre ajuste de escala e dados de temperatura.
>
> As correções aplicadas em 2026-05-31 (simplificação do pipeline DDE, correção da hierarquia VisualScale, paleta 224 cores) alinharam ainda mais o comportamento com o esperado.

---

## 7. Referências

| Fonte | Descrição |
|-------|-----------|
| [ExifTool FLIR Tag Names](https://exiftool.org/TagNames/FLIR.html) | Documentação completa dos metadados FLIR (FFF, FPF, PaletteInfo, CameraInfo) |
| `comportamento.txt` | Análise do usuário sobre comportamento FLIR Tools — cenários de Level/Span manual e automático |
| `Fluxo_Termograma_Renderizacao.md` | Documentação da arquitetura de importação e renderização do Thermix |
| `CORRECOES_RENDERIZACAO_2026-05-31.md` | Histórico de correções aplicadas ao pipeline |
| `Análise técnica do ChatGPT.txt` / `Claude Ai.txt` | Análises externas do pipeline de renderização |
| `PARECER_TECNICO_BLACKBODY_THERMIX.md` | Comparação de arquitetura com implementação de referência |
| `RenderProfile.cs` | Perfil de render por imagem — thresholds de Below/Above e sensor clips |
| `VisualScaleDetector.cs` | Detector de escala visual com 3 níveis de prioridade |
| `ThermalPaletteEngine.cs` | Motor de renderização com suporte a Planck, PaletteStretch, limit colors |
| `MainViewModel.Rendering.cs` | Lógica de AutoScale e ajuste manual de escala |
