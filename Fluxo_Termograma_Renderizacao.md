# Fluxo de Importação e Renderização de Termogramas — Thermix Studio

> **Versão:** 2026-05-31
> Manual de arquitetura descrevendo o funcionamento interno do programa.

Este documento descreve como o Thermix Studio importa, processa e renderiza imagens térmicas. Serve como guia de manutenção para desenvolvedores que precisem entender ou modificar o sistema.

---

## 1. Importação de Termogramas

### Fluxo geral

A importação começa no `MainViewModel` (`OpenFileAsync` / `ImportFilesAsync`) e é orquestrada pelo `ThermalAnalysisService.LoadImageAsync`, que roteia o arquivo conforme a extensão:

```
MainViewModel
  └─ ThermalAnalysisService.LoadImageAsync (orquestrador)
       ├─ .csv     → CsvThermalParser.LoadCsvTemperatureMatrix()
       ├─ .is2     → FlukeIs2Parser.Load()
       ├─ .irg/.rjpeg → InfiRayThermalParser.Load()
       └─ FLIR JPG / outros →
            ├─ RadiometricMetadataExtractor.ExtractMetadata()
            ├─ FlirVisibleImageExtractor.TryExtractVisibleImageAsync()
            ├─ IExifToolService.TryGetMetadataJsonNumericAsync()
            │    └─ RadiometricMetadataExtractor.TryApplyExifToolMetadata()
            ├─ IExifToolService.TryExtractRawThermalAsync()
            └─ RadiometricConverter   (Planck → escala visual → fallback)
```

### Componentes envolvidos

| Módulo | Local | Responsabilidade |
|--------|-------|-----------------|
| `ThermalAnalysisService` | `App/Services/` | Orquestrador — roteia por extensão e coordena os parsers |
| `CsvThermalParser` | `App/Services/Thermal/` | Parsing de arquivos CSV com matriz de temperaturas |
| `FlukeIs2Parser` | `App/Services/Thermal/` | Parsing de containers IS2 (ZIP com XML de calibração + raw 16-bit) |
| `InfiRayThermalParser` | `App/Services/Thermal/` | Parsing de .irg e .rjpeg (InfiRay e OEMs) |
| `RadiometricMetadataExtractor` | `App/Services/Thermal/` | Extração de metadados EXIF/XMP (MetadataExtractor) e JSON (ExifTool) |
| `RadiometricConverter` | `App/Services/Thermal/` | Conversão de raw para temperatura: Planck, escala visual FLIR, fallback |
| `FlirVisibleImageExtractor` | `App/Services/Thermal/` | Extração da foto visível (Python sidecar, ExifTool, FFF parser, cache OpenCV) |
| `FlirFffParser` | `App/Services/Thermal/` | Parsing binário FLIR APP1/FFF (paleta, alinhamento, JPEG visível) |
| `FlirPaletteConverter` | `App/Services/Thermal/` | Conversão YCbCr → BGRA para paletas embutidas |
| `IExifToolService` | `Core/Services/` | Interface unificada de acesso ao ExifTool |

### Hierarquia de conversão de temperatura

A conversão do RAW 16-bit para °C segue uma hierarquia de prioridade:

1. **Planck radiométrico** — se `PlanckR1/R2/B/F/O` estiverem presentes nos metadados, aplica a equação de Planck com byte-swap adaptativo.
2. **Escala visual FLIR** — usa `ImageTemperatureMin/Max` do EXIF para mapeamento relativo.
3. **Fallback relativo** — normaliza pelos valores min/max dos pixels da imagem.

---

## 2. Metadados e Ajuste Automático de Escala

### Estrutura dos modelos

```
ThermixStudio.Core/
├── Domain/
│   ├── InspectionEnums.cs    (EquipmentCriticality, InspectionStatus, SeverityClass)
│   └── Entities.cs           (User, Inspection, Thermogram, ThermalMeasurement)
├── Thermal/
│   ├── ThermalEnums.cs       (ThermalPalette, ImageViewMode, ThermalCameraBrand, VisualScaleSource)
│   ├── ThermalDataModels.cs  (ThermalImageData, RadiometricMetadata, ThermalProcessingState, etc.)
│   ├── ExifModeMapper.cs     (ThermalImageType → ImageViewMode)
│   ├── FlirColorUtils.cs     (YCbCr limit colors, IsFlir)
│   └── TemperatureRangeCalculator.cs
└── Reports/
    └── ReportModels.cs
```

### Auto-Scale

O `MainViewModel.GetPreferredThermalRange` busca os limites originais do momento da foto (`PaletteScaleMinC` / `PaletteScaleMaxC`) armazenados no Exif. Se ausentes, varre a matriz de temperaturas para obter mín/máx reais da cena. O `TemperatureRangeCalculator` fornece o range da matriz para fallback.

O estado de exibição do usuário (modo, paleta, blend factor) é preservado no banco via `ProcessingJson`.

---

## 3. Pipeline de Visualização

**Arquivo:** `ThermalViewPipeline.cs`

Fachada entre o WPF (`MainViewModel`) e os motores de processamento. Orquestra o fluxo de renderização:

```
ThermalViewPipeline
  ├─ ThermalModeDetectionService  → detecta modo de imagem (EXIF)
  ├─ ThermalPaletteEngine         → renderiza cores da paleta térmica
  ├─ ThermalModeEngine            → compõe modos (Blending, PiP, MSX, Thermal)
  └─ FlirCameraUiOverlay          → restaura UI burned-in da câmera
```

---

## 4. Motor de Paletas

**Arquivo:** `ThermalPaletteEngine.cs`

### Responsabilidades

- **Carregamento de LUTs:** Lê arquivos JSON do diretório `/paletas` e mantém cache em memória. Suporta 24 paletas (Iron, Rainbow, Grayscale, Hotmetal, Arctic, Viridis, Plasma, Inferno, Magma, Jet, Hot, Cool, Turbo, etc.).
- **`RenderThermalWithPaletteAsync`:** Função principal de renderização. Converte matriz de temperaturas em pixels coloridos.
- **`DetectPaletteAsync`:** Amostra 500 pixels da imagem original e encontra a paleta mais próxima por distância Euclidiana.
- **`ProcessSmartHD`:** Remapeia cores entre paletas preservando elementos de UI.

### Correção logarítmica (DDE)

O mapeamento temperatura → cor **não é linear** nas câmeras FLIR. As câmeras mapeiam o "Sinal" (radiação 14-bits) na paleta. Para reproduzir as cores originais:

1. **Com Planck:** Temperaturas são convertidas reversamente para Sinal via `PlanckR1/R2/B/F/O`, e o Sinal é interpolado linearmente sobre a LUT.
2. **Sem Planck:** Mapeamento linear das temperaturas em °C sobre a LUT.
3. **DDE (Digital Detail Enhancement):** Histograma com plateau equalization + CDF equalizado + curva two-zone (gamma + knee) replica o processamento interno da FLIR.
4. **Limit colors:** `FlirColorUtils` resolve YCbCr para underflow/overflow/below/above da câmera.

---

## 5. Motor de Modos de Imagem

### Composição de modos

**Arquivo:** `ThermalModeEngine.cs`

Mescla o mapa de cores gerado pelo `ThermalPaletteEngine` com a foto visível. Modos:

- **Thermal:** Apenas a imagem térmica colorizada.
- **Blending:** Alpha blending da imagem IR sobre a visível, com fator de mescla configurável.
- **PiP (Picture in Picture):** Recorte quadrado central da imagem IR sobre a visível.
- **MSX (Multi-Spectral Dynamic Imaging):** Extrai bordas da foto visível (Sobel/Canny) e as aplica sobre a térmica, permitindo leitura de placas e textos.

### Detecção de modo

**Arquivo:** `ThermalModeDetectionService.cs`

Ponto único de detecção do modo de imagem original. Consulta o EXIF (`ThermalImageType`) via `IExifToolService` e mapeia para `ImageViewMode` usando `ExifModeMapper`.

### UI Overlay da Câmera

**Arquivo:** `FlirCameraUiOverlay.cs`

Isola elementos pretos/cinzas/brancos do JPEG original (Logo FLIR, termômetro lateral, Tmax/Tmin, retícula) e os reaplica sobre o canvas final. Garante que as molduras da câmera permaneçam nítidas independentemente da paleta escolhida. Usa `FlirBitmapFont` para renderização pixel-perfect da tipografia FLIR.

---

## 6. Infraestrutura Compartilhada

Módulos de utilidade usados por múltiplos motores:

| Módulo | Local | Função |
|--------|-------|--------|
| `FlirColorUtils` | `Core/Thermal/` | YCbCr limit colors (`ResolveYCrCbLimitColor`, `UsesFlirLimitColors`, `WriteLimitColor`, `LerpByte`) |
| `ExifModeMapper` | `Core/Thermal/` | Mapeamento `ThermalImageType` → `ImageViewMode` |
| `FlirFffParser` | `App/Services/Thermal/` | Parsing binário FLIR APP1/FFF (paleta, alinhamento, JPEG visível) |
| `FlirPaletteConverter` | `App/Services/Thermal/` | Conversão YCbCr embedded palette → BGRA LUT |
| `TemperatureRangeCalculator` | `Core/Thermal/` | Faixa min/max da matriz de temperatura |
| `FlirBitmapFont` | `App/Services/` | Renderização de fonte bitmap FLIR pixel-perfect |
| `IExifToolService` | `Core/Services/` | Interface unificada de acesso ao ExifTool |
| `IThermalModeDetectionService` | `Core/Services/` | Interface de detecção de modo |
| `IFlirCameraUiOverlay` | `Core/Services/` | Interface de UI overlay da câmera |

---

## 7. Exportação de Arquivos Idênticos

**Componente:** `MainViewModel.ExportIdenticalJpgAsync`

### Fluxo

1. Renderiza pixels térmicos via `ThermalPaletteEngine` (Planck Scaling → LUT Mapping).
2. Compõe o modo atual (Blending, PiP, MSX) via `ThermalModeEngine`.
3. Restaura elementos de UI da câmera via `FlirCameraUiOverlay`.
4. Salva a imagem nas dimensões originais da matriz (ex.: 320×240), ignorando o zoom do canvas.
5. Aciona ExifTool para copiar metadados do arquivo original:  
   `-overwrite_original -TagsFromFile "original.jpg" -all:all "exportado.jpg"`  
   Isso preserva a matriz 14-bits original e mantém compatibilidade com sistemas FLIR.

---
Este guia descreve a arquitetura do Thermix Studio para referência de manutenção e desenvolvimento.
