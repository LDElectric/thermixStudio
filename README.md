# Thermix Studio

Software profissional de análise termográfica desenvolvido em C#/WPF para Windows.

## Visão Geral

O Thermix Studio é uma aplicação desktop para análise de imagens termográficas radiométricas (FLIR e outros formatos). Permite importar termogramas, aplicar medições, gerar relatórios técnicos em PDF e HTML, e gerenciar inspeções.

## Funcionalidades

- Importação de imagens térmicas FLIR radiométricas
- Calibração Planck automática via dados EXIF
- Ferramentas de medição: Spot, Área, Linha, Círculo, Isoterma, Diferença
- Paletas térmicas: Iron, Rainbow, Grayscale
- Modos de visualização: Térmico, Luz visível, MSX, PiP, Fusion, Blending
- Auto-scale e ajuste manual de escala
- Limiar Tmax admissível por termograma
- Geração de relatório técnico em HTML e PDF
- Banco de dados local SQLite portátil
- Exportação de dados para CSV

## Tecnologias

- .NET 10 / WPF / C# 13
- Entity Framework Core + SQLite
- OpenCvSharp4 (processamento de imagem)
- SkiaSharp (renderização)
- QuestPDF (geração de PDF)
- WebView2 (preview HTML interativo)
- CommunityToolkit.Mvvm (MVVM)

## Motores de renderização (C#)

| Motor | Origem legada | Responsabilidade |
|-------|---------------|------------------|
| `ThermalRenderEngine` | Thermix / FLIR EXIF | LUT embarcada da câmera, render radiométrico Original |
| `ThermalPaletteEngine` | ThermalCS | LUTs JSON, `ProcessSmartHD`, detecção e troca de paleta |
| `ThermalModeEngine` | modos_CS | MSX, PiP, Combinação térmica, térmica/visível pura |
| `ThermalViewPipeline` | — | Orquestra os três motores (detecção + modulação na UI) |
| `ExifToolService` | — | Metadados FLIR via `exiftool.exe` (binário, não Python) |

Extração da foto visível FLIR: C# nativo (`ThermalAnalysisService`) — o script `extrair_imagens_flir.py` na raiz é legado e **não é chamado** pelo app.

## Estrutura do Projeto

```
src/
├── ThermixStudio.App/          # Aplicação WPF (UI + ViewModels + Services)
├── ThermixStudio.Core/         # Modelos de domínio e contratos
├── ThermixStudio.Infrastructure/ # Persistência (EF Core + SQLite)
└── ThermixStudio.Reports/      # Geração de relatórios HTML e PDF

publish/
└── ThermixStudio.App.exe       # Executável portátil compilado (self-contained)
```

## Como usar o executável

O executável é **portátil e autossuficiente** — basta copiar `ThermixStudio.App.exe` para qualquer pasta e executar. Na primeira execução, a pasta `thermixStudioDB/` será criada automaticamente no mesmo diretório com o banco de dados local.

**Requisitos:** Windows 10 x64 ou superior (WebView2 Runtime já incluído no Windows 10/11).

## Como compilar

```powershell
# Build
cd src/ThermixStudio.App
dotnet build --configuration Release

# Publicar executável portátil único
dotnet publish --configuration Release
# Resultado em: publish/ThermixStudio.App.exe
```

## Licença

Proprietário — © Leonam Dias / LDElectric
