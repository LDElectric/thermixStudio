Renderizador de Termogramas FLIR com Escala de Temperatura Configurável
Contexto e Objetivo
O Thermix Studio atualmente preserva os elementos de UI da câmera FLIR (caixas de temperatura, barra de escala, retículo, logo) copiando pixels da imagem original via detecção de luminância/saturação (OverlayCameraUI em ThermalModeEngine.cs). Isso funciona, mas impede o controle total sobre a escala visível e a paleta de cores desses elementos.

Objetivo: Recriar programaticamente todos os elementos visuais da câmera FLIR (exceto o logo), para que eles se adaptem automaticamente à paleta ativa e à escala de temperatura configurável pelo usuário.

Análise da Imagem de Referência (1MSX.jpg)
Referência FLIR

Elementos visuais identificados na imagem 320×240:
#	Elemento	Posição (px aprox.)	Descrição visual
1	Temperatura do alvo (Spot)	Topo-esquerdo (2,2)–(96,28)	Texto ~41.8 °C com prefixo ~, fundo preto semi-transparente, fonte branca bold ~14px
2	Tmax (escala máx.)	Topo-direito (275,2)–(318,28)	Texto 43.2, fundo preto, fonte branca menor ~11px, sem °C
3	Tmin (escala mín.)	Base-direita (275,210)–(318,238)	Texto 19.0, fundo preto, fonte branca ~11px, sem °C
4	Logo FLIR	Base-esquerda (2,210)–(100,238)	Logo branco ®FLIR — PRESERVADO via overlay
5	Barra de escala (gradiente)	Coluna direita (304,30)–(313,207)	Gradiente vertical da paleta ativa, ~10px largura, ~177px altura
6	Moldura da barra de escala	(302,28)–(316,210)	Borda preta ~1px ao redor do gradiente
7	Retículo (crosshair/mira)	Centro (138,95)–(182,145)	Cruz com ticks nas 4 direções, cor branca/clara, ~2px espessura
Detalhes tipográficos observados:
Fonte: Monoespaçada, tipo Consolas / FLIR Sans / OCR-B — caracteres numéricos uniformes
Tamanho spot: ~14px (relativo a 240px de altura)
Tamanho Tmax/Tmin: ~11px
Cor texto: Branco puro (255,255,255)
Cor fundo caixas: Preto (0,0,0) com opacidade ~85–90%
Padding interno: ~3–4px em todas as direções
Questões de Design Resolvidas
NOTE

Limiares de visibilidade: O controle de "visibilidade" (que escurece os pixels) usará parâmetros separados (visibilityThresholdMinC, visibilityThresholdMaxC). Assim, a paleta ainda mapeia perfeitamente a distribuição de cores usando LevelMinC e LevelMaxC, mas apenas "apaga" os pixels indesejados.

NOTE

Estilo de Cores da UI: Para manter a fidelidade visual, as caixas de temperatura terão sempre fundo preto e texto branco, independentemente da paleta ativa. Apenas o gradiente da barra de escala sofrerá alterações de paleta.

NOTE

Fonte tipográfica: Usaremos a fonte Consolas (monospace) como substituta fiel para renderizar a tipografia numérica.

NOTE

Logo FLIR: O logo continuará sendo preservado via overlay de pixels (lógica simplificada de luma/chroma key).

Proposed Changes
Fase 1 — Novo serviço de desenho de UI (CameraUIRenderer)
Serviço responsável por desenhar programaticamente todos os elementos de UI da câmera sobre o buffer BGRA final.

[NEW] 
ICameraUIRenderer.cs
Interface do novo serviço de renderização de UI da câmera.

csharp

public interface ICameraUIRenderer
{
    /// <summary>
    /// Desenha programaticamente todos os elementos de UI da câmera (caixas de temperatura,
    /// barra de escala, retículo) sobre o buffer BGRA de destino.
    /// O logo FLIR NÃO é desenhado aqui (preservado via overlay separado).
    /// </summary>
    byte[] RenderCameraUI(
        byte[] destinationPixels,
        int width, int height,
        CameraUIRenderData uiData);
}
[NEW] 
CameraUIRenderData.cs
Modelo de dados para os elementos de UI a serem renderizados:

csharp

public sealed class CameraUIRenderData
{
    /// <summary>Temperatura do spot/alvo (retículo central), ex: 41.8</summary>
    public double? SpotTemperature { get; set; }
    
    /// <summary>Temperatura máxima da escala visível (Tmax), ex: 43.2</summary>
    public double ScaleMaxTemperature { get; set; }
    
    /// <summary>Temperatura mínima da escala visível (Tmin), ex: 19.0</summary>
    public double ScaleMinTemperature { get; set; }
    
    /// <summary>LUT da paleta ativa para desenhar a barra de escala</summary>
    public ThermalPaletteLutData? ActivePaletteLut { get; set; }
    
    /// <summary>Se true, desenha o retículo/mira central</summary>
    public bool DrawCrosshair { get; set; } = true;
    
    /// <summary>Se true, desenha as caixas de Tmax/Tmin</summary>
    public bool DrawTemperatureBoxes { get; set; } = true;
    
    /// <summary>Se true, desenha a barra de escala lateral</summary>
    public bool DrawScaleBar { get; set; } = true;
    
    /// <summary>Se true, desenha o valor do spot (topo-esquerdo)</summary>
    public bool DrawSpotLabel { get; set; } = true;
}
[NEW] 
CameraUIRenderer.cs
Implementação do renderizador de UI com SkiaSharp.

Métodos a implementar:

Método	Responsabilidade
RenderCameraUI(...)	Orquestra o desenho de todos os elementos
DrawSpotTemperatureBox(...)	Caixa preta + texto ~XX.X °C no topo-esquerdo
DrawTmaxBox(...)	Caixa preta + texto XX.X no topo-direito
DrawTminBox(...)	Caixa preta + texto XX.X na base-direita
DrawScaleBar(...)	Gradiente vertical da paleta + moldura preta
DrawCrosshair(...)	Cruz com ticks no centro da imagem
Lógica de posicionamento: Proporcionais à resolução base 320×240, usando sx = width/320.0 e sy = height/240.0.

Tipografia: SkiaSharp SKFont com SKTypeface.FromFamilyName("Consolas"), caixas pretas estáticas e texto branco conforme requisitado.

Fase 2 — Escurecimento progressivo de pixels fora do limiar
[MODIFY] 
IThermalPaletteEngine.cs
Método alterado: RenderThermalWithPaletteAsync

Adicionar parâmetros separados de limiar de visibilidade:

diff

Task<byte[]> RenderThermalWithPaletteAsync(
     double[,] temperatures,
     int width, int height,
     string paletteName,
     double? levelMinC = null,
     double? levelMaxC = null,
     RadiometricMetadata? metadata = null,
+    double? visibilityThresholdMinC = null,
+    double? visibilityThresholdMaxC = null,
     CancellationToken cancellationToken = default);
[MODIFY] 
ThermalPaletteEngine.cs
Método alterado: RenderThermalWithPaletteAsync

Após o cálculo de normalized e a chamada a WriteInterpolatedLutColor, aplicar o escurecimento progressivo usando a nova lógica:

csharp

// Após WriteInterpolatedLutColor(lut, normalized, pixels, dest):
if (visibilityThresholdMinC.HasValue || visibilityThresholdMaxC.HasValue)
{
    double tempC = temperatures[y, x];
    double opacity = 1.0; 
    
    if (visibilityThresholdMinC.HasValue && tempC < visibilityThresholdMinC.Value)
    {
        double distance = visibilityThresholdMinC.Value - tempC;
        double fadeRange = Math.Max(1.0, (maxT - minT) * 0.15);
        double fadeRatio = Math.Clamp(distance / fadeRange, 0.0, 1.0);
        opacity = Math.Max(0.15, 1.0 - (fadeRatio * 0.85)); // 15% opacidade mínima
    }
    
    if (visibilityThresholdMaxC.HasValue && tempC > visibilityThresholdMaxC.Value)
    {
        double distance = tempC - visibilityThresholdMaxC.Value;
        double fadeRange = Math.Max(1.0, (maxT - minT) * 0.15);
        double fadeRatio = Math.Clamp(distance / fadeRange, 0.0, 1.0);
        opacity = Math.Max(0.15, 1.0 - (fadeRatio * 0.85));
    }
    
    if (opacity < 1.0)
    {
        pixels[dest]     = (byte)(pixels[dest]     * opacity);
        pixels[dest + 1] = (byte)(pixels[dest + 1] * opacity);
        pixels[dest + 2] = (byte)(pixels[dest + 2] * opacity);
    }
}
Fase 3 — Integração no pipeline existente
[MODIFY] 
ThermalModeEngine.cs
Método alterado: OverlayCameraUI (linhas 229–367)

Mudanças:

Remover o desenho da barra de escala via DrawPaletteScaleBar(...) (linhas 260–268) — será responsabilidade do CameraUIRenderer
Remover a cópia de pixels de UI das caixas Tmax/Tmin/Spot/Crosshair (loop dos uiBoxes exceto logo)
Manter APENAS a lógica do logo FLIR (índice 3 do uiBoxes) no overlay por pixels
Renomear o método para OverlayFlirLogo(...) para refletir a nova responsabilidade reduzida
Métodos removidos/obsoletos:

DrawPaletteScaleBar(...) → movido para CameraUIRenderer
InterpolateLut(...) → movido para CameraUIRenderer
IsInsideScaleBarFill(...) → não mais necessário
IsNearCrosshairLine(...) → crosshair agora é desenhado vetorialmente
[MODIFY] 
IThermalModeEngine.cs
Método alterado: Assinatura de OverlayCameraUI simplificada → renomeada para OverlayFlirLogo:

diff

- byte[] OverlayCameraUI(
-     byte[] finalPixels,
-     byte[] originalPixels,
-     int width, int height,
-     ImageViewMode mode = ImageViewMode.Thermal,
-     ThermalPaletteLutData? scaleLut = null,
-     bool copyOriginalScaleBar = true);
+ byte[] OverlayFlirLogo(
+     byte[] finalPixels,
+     byte[] originalPixels,
+     int width, int height);
[MODIFY] 
IThermalViewPipeline.cs
Método alterado: OverlayCameraUI → atualizar para usar o novo CameraUIRenderer + OverlayFlirLogo:

diff

- byte[] OverlayCameraUI(
-     byte[] finalPixels,
-     byte[] originalPixels,
-     int width, int height,
-     ImageViewMode mode = ImageViewMode.Thermal,
-     string? paletteName = null);
+ byte[] OverlayCameraUI(
+     byte[] finalPixels,
+     byte[] originalPixels,
+     int width, int height,
+     CameraUIRenderData uiData);
[MODIFY] 
ThermalViewPipeline.cs
Injetar ICameraUIRenderer no construtor.

Método alterado: OverlayCameraUI (linhas 96–126)

Nova lógica:

Chamar _cameraUIRenderer.RenderCameraUI(finalPixels, width, height, uiData)
Chamar _modeEngine.OverlayFlirLogo(result, originalPixels, width, height) para manter apenas o logo
[MODIFY] 
MainViewModel.cs
Método alterado: UpdateDisplayImage() e ExportIdenticalJpgAsync()

Mudanças na chamada ao OverlayCameraUI:

csharp

var uiData = new CameraUIRenderData
{
    SpotTemperature = GetCrosshairTemperature(_loadedImage),
    ScaleMaxTemperature = appliedMax,
    ScaleMinTemperature = appliedMin,
    ActivePaletteLut = currentLut,
};
finalPixels = _viewPipeline.OverlayCameraUI(finalPixels, originalPixels, width, height, uiData);
Passar VisibilityThresholdMinC e VisibilityThresholdMaxC do ViewModel para o pipeline de renderização da paleta. Estes parâmetros deverão ser definidos como propriedades no MainViewModel.cs.

[MODIFY] 
App.xaml.cs
Registrar ICameraUIRenderer → CameraUIRenderer no container de DI.

Fase 4 — Leitura de metadados EXIF para temperatura do spot
[MODIFY] 
RadiometricMetadata
Adicionar campo de temperatura do spot lida do EXIF:

diff

+ /// <summary>Temperatura do spot/retículo lida do EXIF (SpotTemperature − 273.15)</summary>
+ public double? SpotTemperatureC { get; set; }
[MODIFY] 
ThermalAnalysisService.cs
Mapear o campo EXIF SpotTemperature (que a câmera grava) para RadiometricMetadata.SpotTemperatureC.

Verification Plan
Automated Tests
dotnet build — Compilação sem erros.
Exportar termograma e checar fidelidade.
Manual Verification
Ver os labels de Temperatura do alvo, Tmax, Tmin com suas fontes em Consolas com fundo preto, na exata posição.
Conferir que a barra lateral preenche com a paleta ativa.
Testar visibilityThresholdMinC/MaxC para checar se a paleta de cores se mantém, apenas escurecendo abaixo e acima da área de interesse visual.