# Refatoração Modular — Motores Thermix Studio

**Data:** 2026-05-31  
**Backup funcional:** `docs/refactoring/backup/2026-05-31/` (cópias dos arquivos originais antes da refatoração)

## Objetivo

Eliminar duplicação, acoplamento circular e responsabilidades excedentes nos motores/serviços térmicos, conforme análise crítica acordada.

## Plano de execução aplicado

### Fase 0 — Consolidação (duplicatas críticas)

| Ação | Status | Arquivos |
|------|--------|----------|
| `FlirColorUtils` — YCbCr, limit colors, IsFlir | ✅ | `Core/Thermal/FlirColorUtils.cs` |
| `TemperatureRangeCalculator` — faixa de matriz | ✅ | `Core/Thermal/TemperatureRangeCalculator.cs` |
| `ExifModeMapper` — mapeamento ThermalImageType | ✅ | `Core/Thermal/ExifModeMapper.cs` |
| `FlirFffParser` — APP1/FFF unificado | ✅ | `App/Services/Thermal/FlirFffParser.cs` |
| `FlirPaletteConverter` — YCbCr → BGRA LUT | ✅ | `App/Services/Thermal/FlirPaletteConverter.cs` |
| `ThermalAnalysisService` usa `IExifToolService` | ✅ | Removidos `ResolveExifToolPath`, `RunProcessCaptureBinary` |
| `ExifToolService` consolidado | ✅ | +embedded temp, +`-j -n`, +`-b -Palette` |

### Fase 1 — Separação de responsabilidades

| Ação | Status | Arquivos |
|------|--------|----------|
| `ThermalModeDetectionService` | ✅ | `App/Services/ThermalModeDetectionService.cs` |
| `IThermalModeDetectionService` | ✅ | `Core/Services/IThermalModeDetectionService.cs` |
| `FlirCameraUiOverlay` extraído | ✅ | `App/Services/FlirCameraUiOverlay.cs` (~770 linhas) |
| `ThermalModeEngine` só composição | ✅ | 828 → **134 linhas** |
| `IThermalModeEngine` simplificada | ✅ | Sem detecção nem UI overlay |
| `IFlirCameraUiOverlay` | ✅ | `Core/Services/IFlirCameraUiOverlay.cs` |
| `DomainModels.cs` separado | ✅ | `Core/Domain/`, `Core/Thermal/`, `Core/Reports/` |

### Fase 2 — Parsers de formato + Conversão + Visível ✅ (concluída 2026-05-31)

| Ação | Status | Arquivos |
|------|--------|----------|
| `CsvThermalParser` | ✅ | `App/Services/Thermal/CsvThermalParser.cs` |
| `FlukeIs2Parser` | ✅ | `App/Services/Thermal/FlukeIs2Parser.cs` |
| `InfiRayThermalParser` | ✅ | `App/Services/Thermal/InfiRayThermalParser.cs` |
| `RadiometricMetadataExtractor` | ✅ | `App/Services/Thermal/RadiometricMetadataExtractor.cs` |
| `RadiometricConverter` | ✅ | `App/Services/Thermal/RadiometricConverter.cs` |
| `FlirVisibleImageExtractor` | ✅ | `App/Services/Thermal/FlirVisibleImageExtractor.cs` |
| `ThermalAnalysisService` reduzido | ✅ | ~2000 → **281 linhas** (orquestrador puro) |

### Fase 3 — Descartada (avaliada em 2026-05-31)

- `ThermalPaletteLutRepository` — **não implementado**. Após análise detalhada, concluiu-se que:
  - O `ThermalPaletteEngine` (~620 linhas) é coeso e tem tamanho saudável.
  - Carregar LUTs é responsabilidade natural do motor de paletas.
  - A extração traria ganho insignificante (~115 linhas) e adicionaria indireção desnecessária para um single-consumer.
  - *Parecer:* over-engineering. YAGNI.

## Verificação de duplicações

| Símbolo | Antes | Depois |
|---------|-------|--------|
| `ResolveYCrCbLimitColor` | 4 implementações | 1 (`FlirColorUtils`) |
| `MapExifModeToEnum` | 3 locais | 1 (`ExifModeMapper`) |
| `ResolveExifToolPath` | 2 locais | 1 (`ExifToolService.FindExifTool`) |
| `RunProcessCaptureBinary` (ExifTool) | 2 locais | 1 (`IExifToolService`) |
| Parser FLIR APP1/FFF | 4 cópias (~800 linhas) | 1 (`FlirFffParser`) |
| Detecção de modo EXIF | 3 locais | 1 (`ThermalModeDetectionService`) |
| UI overlay FLIR | dentro de `ThermalModeEngine` | `FlirCameraUiOverlay` |
| CSV/IS2/IRG parsers | dentro de `ThermalAnalysisService` | Módulos independentes |
| Extração metadados EXIF/JSON | dentro de `ThermalAnalysisService` | `RadiometricMetadataExtractor` |
| Conversão Planck/fallback | dentro de `ThermalAnalysisService` | `RadiometricConverter` |
| Extração imagem visível | dentro de `ThermalAnalysisService` | `FlirVisibleImageExtractor` |

## Redução de linhas (serviços principais)

| Arquivo | Antes | Depois |
|---------|-------|--------|
| `ThermalModeEngine.cs` | ~828 | ~134 |
| `ThermalAnalysisService.cs` | ~2243 | **~281** |
| `ThermalRenderEngine.cs` | ~139 | ~95 |
| `VisualScaleDetector.cs` | ~262 | ~228 |

## DI registrado (`App.xaml.cs`)

```csharp
services.AddSingleton<IExifToolService, ExifToolService>();
services.AddSingleton<IThermalModeDetectionService, ThermalModeDetectionService>();
services.AddSingleton<IFlirCameraUiOverlay, FlirCameraUiOverlay>();
services.AddSingleton<IThermalModeEngine, ThermalModeEngine>(); // sem IExifTool
services.AddSingleton<IThermalAnalysisService, ThermalAnalysisService>(); // requer IExifTool
```

## Estrutura DomainModels (pós-separação)

```
ThermixStudio.Core/
├── Domain/
│   ├── InspectionEnums.cs    (EquipmentCriticality, InspectionStatus, …)
│   └── Entities.cs           (User, Inspection, Thermogram, …)
├── Thermal/
│   ├── ThermalEnums.cs       (ThermalPalette, ImageViewMode, …)
│   ├── ThermalDataModels.cs  (ThermalImageData, RadiometricMetadata, …)
│   ├── FlirColorUtils.cs
│   ├── TemperatureRangeCalculator.cs
│   └── ExifModeMapper.cs
└── Reports/
    └── ReportModels.cs
```

**Namespace mantido:** `ThermixStudio.Core` (sem breaking change de imports).

## Como restaurar backup

Copiar arquivos de `docs/refactoring/backup/2026-05-31/src/` de volta para `src/` e recompilar.

## Build

```
dotnet build ThermixStudio.slnx
```

Compilação verificada com **0 erros, 0 avisos** em 2026-05-31.

## Notas

- `ThermalAnalysisService` ainda é grande (~1729 linhas) — Fase 2 extrairá parsers e conversores radiométricos.
- `RadiometricMetadata` permanece monolítica; fragmentação em sub-objetos é opcional na Fase 3.
- Inferência visual de modo (`MainViewModel.Inference.cs`) permanece no ViewModel; integração futura com `ThermalModeDetectionService` é possível.
