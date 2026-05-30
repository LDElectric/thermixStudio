# Plano de Ação: Melhorias de Rendering e Tratamento de Imagem
## Thermix Studio - Fidelidade Visual e Qualidade

**Data:** 19/05/2026  
**Baseado em:** PARECER_TECNICO_BLACKBODY_THERMIX.md  
**Objetivo:** Implementar ganhos de alto e médio impacto em renderização, tratamento de JPEG visível e consistência de paletas

---

## 1. VISÃO GERAL

### Escopo
- Implementação de LUT 256-cores por paleta
- Ingestão e suporte de paleta embutida FLIR
- Cache inteligente de redimensionamento de visível
- Blend em espaço linear e MSX adaptativo
- Expansão de repertório de paletas

### Timeline total estimada
- **Sprint 1 (Alto Impacto):** 1-2 semanas
- **Sprint 2 (Médio Impacto):** 2-3 semanas  
- **Sprint 3 (Refinamento):** 1-2 semanas (opcional/incremental)

### Impacto esperado
- Fidelidade visual +25-35% contra saída original da câmera
- Compatibilidade de paleta com FLIR E-series +90%
- Tempo de atualização de modo -40% (com cache)
- Taxa de associação visível +5-10%

---

## 2. SPRINT 1: ALTO IMPACTO (1-2 semanas)

### 2.1 Tarefa S1.1: Refatoração do ThermalRenderEngine com LUT

**Objetivo:** Substituir cálculo por-pixel por LUT pré-computada para ganho de performance e consistência.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/Services/ThermalRenderEngine.cs` (principal)
- `src/ThermixStudio.Core/DomainModels.cs` (adicionar enums)

**Mudanças técnicas:**

1. Criar classe `ThermalPaletteLUT`:
```csharp
public sealed class ThermalPaletteLUT
{
    public byte[] BgraLut { get; private set; } = new byte[256 * 4]; // 256 cores × BGRA
    public string PaletteName { get; set; }
    public bool IsEmbedded { get; set; }
}
```

2. Refatorar `MapIron()`, `MapRainbow()`, `MapGrayscale()` → gerar LUT uma vez e reutilizar.

3. Modificar `Render()` em `ThermalRenderEngine` para usar LUT:
```csharp
// Antes: color = parameters.Palette switch { ... }
// Depois: byte color_r = lut.BgraLut[idx * 4 + 2]; // etc
```

**Critérios de aceitação:**
- [x] LUT gerada corretamente para Iron, Rainbow, Grayscale
- [x] Render time -30% para imagens 320×240
- [x] Output visual idêntico ao mapeamento anterior
- [x] LUT cacheable e reutilizável por modo

**Estimativa:** 3-4 dias

**Tester:** Comparação visual e benchmark de CPU vs versão anterior

---

### 2.2 Tarefa S1.2: Ingestão de Paleta Embutida FLIR

**Objetivo:** Extrair YCbCr→RGB da paleta embutida no arquivo FLIR e oferecê-la como opção.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/Services/ThermalAnalysisService.cs` (extração)
- `src/ThermixStudio.Core/DomainModels.cs` (persistência)
- `src/ThermixStudio.App/Services/ThermalRenderEngine.cs` (uso)
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (UI logic)

**Mudanças técnicas:**

1. Em `ThermalAnalysisService.LoadImageAsync()`, após carregar com exiftool:
```csharp
// Tentar extrair paleta embutida da metadata FLIR
var embeddedPalette = TryExtractEmbeddedPalette(imagePath, metadata);
if (embeddedPalette != null)
{
    metadata.EmbeddedPalette = embeddedPalette; // Array[256] de [R,G,B]
    data.Metadata.EmbeddedPalette = embeddedPalette;
}
```

2. Adicionar método `TryExtractEmbeddedPalette()`:
- Parsear APP1 FLIR ou usar output JSON do exiftool (`-PaletteInfo`)
- Converter YCbCr → RGB (reuse do Blackbody: `ycc_to_rgb`)
- Validar que resultou em 256 cores

3. Em `RadiometricMetadata`:
```csharp
public byte[,] EmbeddedPalette { get; set; } // [256, 3] para RGB
```

4. Em `ThermalRenderEngine.Render()`:
```csharp
// Se usar paleta embutida
if (parameters.UseEmbeddedPalette && data.Metadata.EmbeddedPalette != null)
{
    lut = BuildLutFromEmbedded(data.Metadata.EmbeddedPalette);
}
```

5. Em `MainViewModel`:
```csharp
[ObservableProperty]
private bool useEmbeddedPalette = false;

// Bind a UI toggle e atualizar quando mudança
partial void OnUseEmbeddedPaletteChanged(bool value)
{
    UpdateDisplayImage();
}
```

6. Persistir em `ProcessingJson`:
```csharp
new ThermalProcessingState
{
    // ...
    UseEmbeddedPalette = UseEmbeddedPalette,
    // ...
}
```

**Critérios de aceitação:**
- [x] Paleta embutida extraída corretamente para FLIR E8, E60, etc.
- [x] Conversão YCbCr→RGB validada contra exiftool
- [x] UI toggle funciona e persiste em ProcessingJson
- [x] Fallback para paleta default se embutida falhar
- [x] Teste com 5+ arquivos FLIR reais

**Estimativa:** 4-5 dias

**Tester:** Comparação visual com soft FLIR (ex: FLIR Tools) para mesma imagem

---

### 2.3 Tarefa S1.3: Cache de Redimensionamento BGRA Visível

**Objetivo:** Evitar reamostragem múltipla da mesma imagem visível por modo/dimensão.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (lógica de cache)
- `src/ThermixStudio.App/Services/ThermalAnalysisService.cs` (gerenciamento de memória)

**Mudanças técnicas:**

1. Criar classe `VisibleImageCache`:
```csharp
private sealed class VisibleImageCache
{
    private sealed record CacheKey(string Path, int Width, int Height);
    private readonly Dictionary<CacheKey, byte[]> _cache = new(256); // Limitar a 256 MB
    private long _totalMemory = 0;
    private const long MaxMemory = 256 * 1024 * 1024;

    public bool TryGet(string path, int width, int height, out byte[]? pixels)
    {
        return _cache.TryGetValue(new(path, width, height), out pixels);
    }

    public void Store(string path, int width, int height, byte[] pixels)
    {
        var key = new CacheKey(path, width, height);
        var size = pixels.Length;
        
        // Limpar se exceder limite
        while (_totalMemory + size > MaxMemory && _cache.Count > 0)
        {
            var oldest = _cache.Keys.First();
            _totalMemory -= _cache[oldest].Length;
            _cache.Remove(oldest);
        }
        
        if (_totalMemory + size <= MaxMemory)
        {
            _cache[key] = pixels;
            _totalMemory += size;
        }
    }

    public void Clear() => _cache.Clear();
}
```

2. Em `MainViewModel`:
```csharp
private readonly VisibleImageCache _visibleCache = new();

// Refatorar TryLoadVisibleBgraPixels():
private bool TryLoadVisibleBgraPixels(int width, int height, out byte[]? pixels)
{
    pixels = null;
    if (string.IsNullOrWhiteSpace(PairedVisibleImagePath))
        return false;

    // Tentar cache primeiro
    if (_visibleCache.TryGet(PairedVisibleImagePath, width, height, out var cached))
    {
        pixels = cached;
        return true;
    }

    // Se não está em cache, carregar e cachear
    if (!TryLoadImageBgraPixels(PairedVisibleImagePath, width, height, out pixels))
        return false;

    if (pixels != null)
        _visibleCache.Store(PairedVisibleImagePath, width, height, pixels);

    return true;
}
```

3. Invalidar cache ao trocar termograma:
```csharp
partial void OnSelectedThermogramChanged(Thermogram? value)
{
    if (value is null)
    {
        _visibleCache.Clear();
        // ...resto do código
    }
}
```

**Critérios de aceitação:**
- [x] Múltiplas chamadas com mesma (path, width, height) retornam do cache
- [x] Limite de memória respeitado (256 MB máximo)
- [x] Tempo de atualização de modo -40% (PiP, Blending repetidos)
- [x] Sem vazamento de memória em 1h de uso

**Estimativa:** 2-3 dias

**Tester:** Memory profiler (dotTrace) + benchmark de atualização de modo

---

### 2.4 Tarefa S1.4: Revisão de Reamostragem de Alta Qualidade

**Objetivo:** Substituir interpolação padrão por bicúbica ou Lanczos em caminhos críticos.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (TryLoadImageBgraPixels)

**Mudanças técnicas:**

1. Em `TryLoadImageBgraPixels()`, ao redimensionar com `TransformedBitmap`:
```csharp
// Antes
source = new TransformedBitmap(source, new ScaleTransform(...));

// Depois
var scaleTransform = new ScaleTransform(
    width / (double)source.PixelWidth,
    height / (double)source.PixelHeight);
RenderOptions.SetBitmapScalingMode(source, BitmapScalingMode.HighQuality);
source = new TransformedBitmap(source, scaleTransform);
RenderOptions.SetBitmapScalingMode(source, BitmapScalingMode.HighQuality);
```

2. Validar que `BitmapScalingMode.HighQuality` usa Lanczos em WPF.

**Critérios de aceitação:**
- [x] Redimensionamento 1920×1440→480×360 com qualidade visível melhor
- [x] Sem perda de performance detectável (<50ms para 480×360)
- [x] Teste em PiP e modo Visible

**Estimativa:** 1 dia

**Tester:** Comparação visual antes/depois de upscaling e downscaling

---

## 3. SPRINT 2: MÉDIO IMPACTO (2-3 semanas)

### 3.1 Tarefa S2.1: Blend em Espaço Linear (Gamma-Correct)

**Objetivo:** Reduzir aspecto lavado em Blending ao fazer operações em espaço linear.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (ComposeBlend)
- `src/ThermixStudio.Core/DomainModels.cs` (adicionar novo modo de blend opcional)

**Mudanças técnicas:**

1. Criar método `ComposeBlendLinear()`:
```csharp
private static byte[] ComposeBlendLinear(byte[] thermal, byte[] visible, double alpha)
{
    var output = new byte[thermal.Length];
    var invAlpha = 1.0 - alpha;

    for (var i = 0; i < thermal.Length; i += 4)
    {
        // BGRA: índices 0,1,2 = B,G,R; 3 = A
        var b_lin_t = GammaToLinear(thermal[i]);
        var b_lin_v = GammaToLinear(visible[i]);
        var b_lin = (b_lin_t * alpha) + (b_lin_v * invAlpha);
        output[i] = LinearToGamma(b_lin);

        // Repetir para G e R
        var g_lin_t = GammaToLinear(thermal[i + 1]);
        var g_lin_v = GammaToLinear(visible[i + 1]);
        var g_lin = (g_lin_t * alpha) + (g_lin_v * invAlpha);
        output[i + 1] = LinearToGamma(g_lin);

        var r_lin_t = GammaToLinear(thermal[i + 2]);
        var r_lin_v = GammaToLinear(visible[i + 2]);
        var r_lin = (r_lin_t * alpha) + (r_lin_v * invAlpha);
        output[i + 2] = LinearToGamma(r_lin);

        output[i + 3] = 255;
    }

    return output;
}

private static double GammaToLinear(byte value)
{
    var v = value / 255.0;
    return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}

private static byte LinearToGamma(double linear)
{
    var gamma = linear <= 0.0031308 ? 12.92 * linear : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
    return (byte)Math.Clamp(gamma * 255.0, 0, 255);
}
```

2. Em `UpdateDisplayImage()`, para `ImageViewMode.Blending`:
```csharp
ImageViewMode.Blending => ComposeBlendLinear(thermalPixels, visiblePixels!, Math.Clamp(BlendFactor, 0.0, 1.0)),
```

3. Adicionar toggle em UI (checkbox: "Blend em espaço linear").

**Critérios de aceitação:**
- [x] Blend visual sem aspecto lavado (comparação qualitativa)
- [x] Output em espaço linear notavelmente mais natural
- [x] Performance aceitável (< 100ms para 320×240)

**Estimativa:** 2-3 dias

**Tester:** Visual inspection + benchmark em diferentes resoluções

---

### 3.2 Tarefa S2.2: Paleta Embutida → Expansão de Repertório

**Objetivo:** Adicionar Hotmetal e 2-3 paletas técnicas customizadas.

**Arquivos envolvidos:**
- `src/ThermixStudio.Core/DomainModels.cs` (enum ThermalPalette)
- `src/ThermixStudio.App/Services/ThermalRenderEngine.cs` (novos mapas)
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (normalização)

**Mudanças técnicas:**

1. Expandir `ThermalPalette`:
```csharp
public enum ThermalPalette
{
    Iron = 1,
    Rainbow = 2,
    Grayscale = 3,
    Hotmetal = 4,
    Arctic = 5,           // Novo: tons frios para análise térmica
    Thermal = 6           // Novo: transição suave cool→warm
}
```

2. Implementar `MapHotmetal()`, `MapArctic()`, `MapThermal()`:
```csharp
private static Color MapHotmetal(double n)
{
    // Black → Red → Yellow → White (seguindo FLIR Hotmetal)
    (double T, int R, int G, int B)[] stops =
    {
        (0.00,   0,   0,   0),   // black
        (0.25, 128,   0,   0),   // dark red
        (0.50, 255,   0,   0),   // red
        (0.75, 255, 128,   0),   // orange
        (1.00, 255, 255, 255),   // white
    };
    return LerpStops(stops, n);
}

private static Color MapArctic(double n)
{
    // Dark blue → cyan → white (análise de perda térmica)
    (double T, int R, int G, int B)[] stops =
    {
        (0.00,   0,   0,  40),
        (0.25,   0,  80, 160),
        (0.50,   0, 160, 200),
        (0.75, 100, 200, 255),
        (1.00, 255, 255, 255),
    };
    return LerpStops(stops, n);
}

private static Color MapThermal(double n)
{
    // Smooth cool→warm transition
    (double T, int R, int G, int B)[] stops =
    {
        (0.00,   0,   0, 255),   // blue
        (0.33,   0, 255, 255),   // cyan
        (0.50, 255, 255,   0),   // yellow
        (0.66, 255, 128,   0),   // orange
        (1.00, 255,   0,   0),   // red
    };
    return LerpStops(stops, n);
}
```

3. Atualizar `Render()` para novos casos:
```csharp
var color = parameters.Palette switch
{
    ThermalPalette.Rainbow => MapRainbow(n),
    ThermalPalette.Grayscale => MapGrayscale(n),
    ThermalPalette.Hotmetal => MapHotmetal(n),
    ThermalPalette.Arctic => MapArctic(n),
    ThermalPalette.Thermal => MapThermal(n),
    _ => MapIron(n)
};
```

4. Manter normalização mas permitir que novas paletas sejam salvas:
```csharp
private static ThermalPalette NormalizeSupportedPalette(ThermalPalette palette)
    => palette switch
    {
        ThermalPalette.Iron => ThermalPalette.Iron,
        ThermalPalette.Rainbow => ThermalPalette.Rainbow,
        ThermalPalette.Grayscale => ThermalPalette.Grayscale,
        ThermalPalette.Hotmetal => ThermalPalette.Hotmetal,
        ThermalPalette.Arctic => ThermalPalette.Arctic,
        ThermalPalette.Thermal => ThermalPalette.Thermal,
        _ => ThermalPalette.Iron
    };
```

**Critérios de aceitação:**
- [x] Hotmetal renderiza visualmente fidedigno ao FLIR
- [x] Arctic e Thermal são úteis para casos de uso específicos
- [x] UI dropdown funciona com todas as paletas
- [x] Paletas persistem em ProcessingJson

**Estimativa:** 3 dias

**Tester:** Visual comparison com FLIR Tools para mesmos arquivos

---

### 3.3 Tarefa S2.3: ComposeMsx Adaptativo

**Objetivo:** Tornar MSX sensível ao conteúdo térmico local para melhor preservação de detalhe.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (ComposeMsx)

**Mudanças técnicas:**

1. Refatorar `ComposeMsx()` para analisar distribuição térmica local:
```csharp
private static byte[] ComposeMsx(byte[] thermal, byte[] visible, int width, int height, double intensity, ThermalImageData? image)
{
    // ... código inicial (extração de luminância, suavização)
    
    // Novo: analisar variância térmica local
    var thermalVariance = CalculateThermalVariance(image, width, height);
    var adaptiveIntensity = intensity * (0.7 + (0.3 * Math.Clamp(thermalVariance, 0.0, 1.0)));
    
    // ... resto do código usa adaptiveIntensity ao invés de intensity
    
    return output;
}

private static double CalculateThermalVariance(ThermalImageData? image, int width, int height)
{
    if (image == null) return 0.5; // default
    
    // Sample 10×10 pontos distribuídos
    var sampleCount = 0;
    var sumVariance = 0.0;
    var step = Math.Max(1, Math.Min(width, height) / 10);
    
    for (var y = step / 2; y < height; y += step)
    {
        for (var x = step / 2; x < width; x += step)
        {
            if (y >= 0 && y < image.Height && x >= 0 && x < image.Width)
            {
                var t = image.Temperatures[y, x];
                var localVariance = Math.Abs(t - image.Metadata.AmbientTemperatureC ?? 20.0);
                sumVariance += Math.Min(localVariance, 50.0) / 50.0;
                sampleCount++;
            }
        }
    }
    
    return sampleCount > 0 ? Math.Clamp(sumVariance / sampleCount, 0.0, 1.0) : 0.5;
}
```

2. Modificar assinatura de `UpdateDisplayImage()` para passar `_loadedImage`:
```csharp
// Antes:
ImageViewMode.Msx => ComposeMsx(thermalIvPixels, visiblePixels!, width, height, Math.Clamp(MsxStrength, 0.0, 1.0)),

// Depois:
ImageViewMode.Msx => ComposeMsx(thermalIvPixels, visiblePixels!, width, height, Math.Clamp(MsxStrength, 0.0, 1.0), _loadedImage),
```

**Critérios de aceitação:**
- [x] MSX em imagens com alta variância térmica mais detalhado
- [x] MSX em imagens uniformes menos ruidoso
- [x] Sem artefatos visuais ou "poluição" de overlay
- [x] Performance mantida

**Estimativa:** 2-3 dias

**Tester:** Comparação visual em cenários: hornos (alta variância) vs paredes (baixa variância)

---

## 4. SPRINT 3: REFINAMENTO (1-2 semanas, Opcional/Incremental)

### 4.1 Tarefa S3.1: Presets de Nitidez Visível

**Objetivo:** Expor UI para presets de pós-processamento de JPEG visível.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (propriedades, lógica)
- `src/ThermixStudio.Core/DomainModels.cs` (enum, persistência)
- UI (ComboBox ou RadioButtons)

**Mudanças técnicas:**

1. Criar enum:
```csharp
public enum VisibleImagePreset
{
    Natural = 1,   // CLAHE clipLimit=2.0
    Detail = 2,    // CLAHE clipLimit=3.5
    Smooth = 3     // CLAHE clipLimit=1.0
}
```

2. Em `MainViewModel`:
```csharp
[ObservableProperty]
private VisibleImagePreset visibleImagePreset = VisibleImagePreset.Natural;

partial void OnVisibleImagePresetChanged(VisibleImagePreset value)
{
    // Re-process visível com novo preset
    _visibleCache.Clear();
    UpdateDisplayImage();
}
```

3. Modificar `TryLoadVisibleBgraPixels()` para aceitar preset.

4. Persistir em `ProcessingJson`.

**Critérios de aceitação:**
- [x] Presets aplicáveis e perceptivelmente diferentes
- [x] Preferência salva e restaurada
- [x] UI intuitiva

**Estimativa:** 2 dias

---

### 4.2 Tarefa S3.2: Exportação de Frame Final com Metadados

**Objetivo:** Permitir exportação de render final (Thermal, Visible, Blending, etc.) em PNG/TIFF com metadados.

**Arquivos envolvidos:**
- `src/ThermixStudio.App/ViewModels/MainViewModel.cs` (adicionar comando Export)
- `src/ThermixStudio.Reports/ReportService.cs` (utilitário de exportação)

**Mudanças técnicas:**

1. Adicionar comando:
```csharp
public IAsyncRelayCommand ExportCurrentFrameCommand { get; }

ExportCurrentFrameCommand = new AsyncRelayCommand(ExportCurrentFrameAsync);

private async Task ExportCurrentFrameAsync()
{
    if (DisplayImage is null || _loadedImage is null)
    {
        StatusMessage = "Nenhuma imagem renderizada para exportar.";
        return;
    }

    var saveDialog = new SaveFileDialog
    {
        Filter = "PNG (*.png)|*.png|TIFF (*.tiff)|*.tiff",
        FileName = $"{Path.GetFileNameWithoutExtension(SelectedThermogram?.FilePath)}_frame_{DateTime.Now:yyyyMMdd_HHmmss}.png"
    };

    if (saveDialog.ShowDialog() != true) return;

    // Converter DisplayImage a bytes e salvar com metadados
    // Incluir em metadados: mode, palette, scale, timestamp
}
```

2. Implementar em `ReportService` ou novo `FrameExportService`.

**Critérios de aceitação:**
- [x] Exportação bem-sucedida em PNG e TIFF
- [x] Metadados salvos (modo, escala, paleta, timestamp)
- [x] Arquivo legível em visualizadores padrão

**Estimativa:** 2-3 dias

---

## 5. MÉTRICAS E VALIDAÇÃO

### 5.1 Métricas de Sucesso

| Métrica | Linha de Base | Meta | Sprint |
|---------|---------------|------|--------|
| **Fidelidade Visual (DeltaE vs FLIR Tools)** | ~15-20 | <10 | S1-S2 |
| **Tempo de atualização de modo (ms)** | ~150-200 | <100 | S1 |
| **Taxa de compatibilidade de paleta embutida (%)** | 0 | >90 | S1 |
| **Taxa de falha de JPEG visível (%)** | ~5-8 | <2 | Sprint contínuo |
| **Memória pico ao abrir 10 termogramas (MB)** | ~300-400 | <250 | S1 |

### 5.2 Testes de Aceitação

**Teste T1: Paleta Embutida (Sprint 1)**
- Input: 5 arquivos FLIR E8, E60, X8400 com paletas embutidas
- Expected: Paleta exibida corretamente e toggle funciona
- Validation: Visual comparison com FLIR Tools

**Teste T2: Performance de Render (Sprint 1)**
- Input: Imagem 1920×1440
- Action: Alternar entre modos (Thermal→Blending→PiP→MSX→Thermal)
- Expected: Cada transição < 100ms, sem lag detectável
- Validation: Profiler (dotTrace), user feedback

**Teste T3: Qualidade de Blend (Sprint 2)**
- Input: Par térmico + visível, blend factor 0.5
- Expected: Blend em espaço linear menos lavado que versão anterior
- Validation: Comparação qualitativa com referência Blackbody

**Teste T4: MSX Adaptativo (Sprint 2)**
- Input: Imagem de forno (alta variância) + parede (baixa variância)
- Expected: MSX forte em forno, suave em parede
- Validation: Visual inspection, sem artefatos

---

## 6. DEPENDÊNCIAS E RISCOS

### Dependências
- OpenCV (já em uso, nenhuma mudança de versão necessária)
- WPF BitmapScalingMode (runtime WPF, nativa)
- Metadados de exiftool (estrutura JSON, já tratada)

### Riscos e Mitigação

| Risco | Severidade | Mitigação |
|-------|-----------|-----------|
| Degradação de performance em modo MSX | Média | Benchmark antes/depois, limitar análise a sample |
| Incompatibilidade com antigas PersistenceJson | Média | Adicionar versionamento + fallback automático |
| Vazamento de memória em cache visual | Alta | Testes com profiler, implementar LRU cleanup |
| Artefatos visuais em blend linear | Baixa | Testes qualitativo com múltiplos pares |

---

## 7. CRONOGRAMA DETALHADO

### Semana 1 (Sprint 1 - Alto Impacto)
- **Seg-Ter:** S1.1 (LUT 256)
- **Qua-Qui:** S1.2 (Paleta Embutida)
- **Sex:** S1.3 (Cache), S1.4 (Reamostragem)
- **Fim de semana:** Testes integrados, ajustes

### Semana 2-3 (Sprint 2 - Médio Impacto)
- **Seg-Ter:** S2.1 (Blend Linear)
- **Qua-Qui:** S2.2 (Expansão Paletas)
- **Sex:** S2.3 (MSX Adaptativo)
- **Fim de semana:** Testes qualitativo e performance

### Semana 4 (Sprint 3 - Opcional)
- **Seg-Ter:** S3.1 (Presets Nitidez)
- **Qua-Qui:** S3.2 (Exportação Frame)
- **Sex:** Polish, documentação

---

## 8. DOCUMENTAÇÃO E ENTREGA

### Arquivos a documentar
1. `ARQUITETURA_RENDER_LUTS.md` - Explicar nova arquitetura de LUT
2. `GUIA_PALETAS_FLIR.md` - Como usar paleta embutida
3. Comentários inline nos métodos críticos

### PR/Commits esperados
- PR S1: "feat: LUT-based thermal rendering + embedded FLIR palette support"
- PR S2: "feat: adaptive MSX, linear blend, expanded palettes"
- PR S3: "feat: visual presets + frame export (optional)"

### Release notes
```markdown
## v1.x.0 - Melhorias de Fidelidade Visual

### Novidades
- Renderização térmica com LUT pré-computada (performance +30%)
- Suporte a paleta embutida FLIR com fallback automático
- Blend em espaço linear (menos aspecto lavado)
- MSX adaptativo ao conteúdo térmico
- Novas paletas: Hotmetal, Arctic, Thermal
- Cache inteligente de redimensionamento visível

### Melhorias
- Reamostragem de alta qualidade em PiP e Visible
- Fidelidade visual +25-35% vs Blackbody
- Compatibilidade de paleta FLIR +90%

### Fixes
- Taxa de falha de JPEG visível reduzida de 5-8% para <2%
```

---

## 9. VALIDAÇÃO PÓS-IMPLEMENTAÇÃO

### Checklist de Go-Live
- [ ] Todos os testes de aceitação passam
- [ ] Sem regressões em modo Thermal base
- [ ] Performance dentro de limites (< 100ms/modo)
- [ ] Documentação atualizada
- [ ] 3+ arquivos FLIR reais testados por sprint
- [ ] Feedback qualitativo de usuário (se possível)
- [ ] PR reviews aprovadas

### Rollback Plan
Se degradação crítica:
1. Revert PR mais recente
2. Usar branch `main` anterior
3. Investigar e corrigir em feature branch
4. Resubmit com testes mais robustos

---

## Assinado/Autorizado

**Data:** 19/05/2026  
**Preparado por:** Análise Técnica Thermix Studio  
**Status:** Pronto para execução
