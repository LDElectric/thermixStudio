# Parecer Técnico: Blackbody x Thermix Studio

Data: 2026-05-19

## Objetivo
Avaliar o projeto Blackbody importado em experiments/blackbody e comparar com o Thermix Studio para identificar ganhos portáveis em:
- Pipeline de JPEG (decodificação, pós-processamento, exportação).
- Tratamento de paletas e mapeamento térmico.
- Nitidez e qualidade visual geral.

## Etapa 1: Leitura completa do Blackbody

### Arquitetura observada
- Aplicativo em Rust com biblioteca de leitura termográfica separada (libblackbody).
- Entrada de arquivos por detecção de formato via magic number (FLIR JPG e TIFF): [experiments/blackbody/src/lib/thermogram.rs](experiments/blackbody/src/lib/thermogram.rs#L55).
- Leitura FLIR delegada para flyr, com correção de orientação EXIF aplicada no buffer térmico: [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L31), [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L42), [experiments/blackbody/src/lib/thermogram.rs](experiments/blackbody/src/lib/thermogram.rs#L81).
- Foto óptica embutida disponível via thermogram.optical(): [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L58).

### Pipeline de render no Blackbody
- Render térmico baseado em binning linear para paleta de 256 níveis: [experiments/blackbody/src/lib/thermogram_trait.rs](experiments/blackbody/src/lib/thermogram_trait.rs#L57).
- Render default usa TURBO: [experiments/blackbody/src/lib/thermogram_trait.rs](experiments/blackbody/src/lib/thermogram_trait.rs#L93).
- UI alterna entre térmico e óptico com desenho em thread separada: [experiments/blackbody/src/gtkui/app_window.rs](experiments/blackbody/src/gtkui/app_window.rs#L331), [experiments/blackbody/src/gtkui/app_window.rs](experiments/blackbody/src/gtkui/app_window.rs#L342).
- Escala de visualização usa interpolação bilinear (Pixbuf): [experiments/blackbody/src/gtkui/app_window.rs](experiments/blackbody/src/gtkui/app_window.rs#L192).

### Paletas e exportação no Blackbody
- Suporte a paleta embutida do arquivo FLIR (YCbCr para RGB): [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L72), [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L86).
- UI pode usar paleta embutida ou paleta selecionada: [experiments/blackbody/src/gtkui/app_window.rs](experiments/blackbody/src/gtkui/app_window.rs#L486).
- Exporta térmico bruto em TIFF float32: [experiments/blackbody/src/lib/thermogram_trait.rs](experiments/blackbody/src/lib/thermogram_trait.rs#L104).
- Exporta render colorido via save_buffer (tipo inferido por extensão): [experiments/blackbody/src/lib/thermogram_trait.rs](experiments/blackbody/src/lib/thermogram_trait.rs#L138), [experiments/blackbody/src/lib/thermogram_trait.rs](experiments/blackbody/src/lib/thermogram_trait.rs#L151).

Conclusão da etapa 1:
- Blackbody é enxuto e sólido em leitura/render térmico, com bom tratamento de orientação e paletas.
- O foco é visualização técnica, não melhoria fotográfica avançada de JPEG visível.

## Etapa 2: Leitura do Thermix Studio e pontos comuns

### Núcleo do Thermix Studio observado
- Render térmico BGRA em C# com mapeamento Iron, Rainbow e Grayscale: [src/ThermixStudio.App/Services/ThermalRenderEngine.cs](src/ThermixStudio.App/Services/ThermalRenderEngine.cs#L33), [src/ThermixStudio.App/Services/ThermalRenderEngine.cs](src/ThermixStudio.App/Services/ThermalRenderEngine.cs#L106), [src/ThermixStudio.App/Services/ThermalRenderEngine.cs](src/ThermixStudio.App/Services/ThermalRenderEngine.cs#L127).
- Importação de termogramas para biblioteca gerenciada (LocalAppData), evitando alterar origem: [src/ThermixStudio.App/ViewModels/MainViewModel.cs](src/ThermixStudio.App/ViewModels/MainViewModel.cs#L530), [src/ThermixStudio.App/ViewModels/MainViewModel.cs](src/ThermixStudio.App/ViewModels/MainViewModel.cs#L1963).
- Persistência de estado térmico (escala, paleta, emissividade, visível pareada): [src/ThermixStudio.Core/DomainModels.cs](src/ThermixStudio.Core/DomainModels.cs#L75), [src/ThermixStudio.Core/DomainModels.cs](src/ThermixStudio.Core/DomainModels.cs#L163), [src/ThermixStudio.App/ViewModels/MainViewModel.cs](src/ThermixStudio.App/ViewModels/MainViewModel.cs#L1544).

### Pontos comuns com Blackbody
- Ambos suportam FLIR e trabalham com imagem visível associada ao térmico.
- Ambos centralizam render em buffer RGB/BGRA e atualizam UI a partir desse buffer.
- Ambos usam nível mínimo/máximo para normalização de faixa térmica.

### Diferenças relevantes
- Blackbody usa parsing nativo do ecossistema Rust (flyr/rexif), Thermix usa OpenCV + exiftool + fallbacks.
- Thermix tem pipeline mais amplo para extração e melhoria da foto visível em C# (APP1 FLIR + ExifTool + OpenCvSharp), inexistente em Blackbody.
- Blackbody possui conjunto maior de paletas de referência no código; Thermix restringe para 3 paletas na prática: [src/ThermixStudio.App/ViewModels/MainViewModel.cs](src/ThermixStudio.App/ViewModels/MainViewModel.cs#L1600).

Conclusão da etapa 2:
- Thermix já é mais avançado no fluxo operacional completo.
- Blackbody oferece boas ideias de consistência de paleta e simplicidade de pipeline térmico que podem ser portadas.

## Etapa 3: Leitura dirigida

### 1) Pipeline de JPEG

#### Blackbody
- Detecta JPG FLIR por assinatura e delega ao flyr: [experiments/blackbody/src/lib/thermogram.rs](experiments/blackbody/src/lib/thermogram.rs#L66), [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L31).
- Lê foto óptica embutida sem etapa explícita de enhancement: [experiments/blackbody/src/lib/flir.rs](experiments/blackbody/src/lib/flir.rs#L58).
- Exportação térmica forte (TIFF float32), exportação de render voltada a imagem final.

#### Thermix Studio
- Tenta raw térmico radiométrico com exiftool (-RawThermalImage) e decodifica com OpenCV: [src/ThermixStudio.App/Services/ThermalAnalysisService.cs](src/ThermixStudio.App/Services/ThermalAnalysisService.cs#L325).
- Converte RAW com coeficientes Planck quando disponíveis: [src/ThermixStudio.App/Services/ThermalAnalysisService.cs](src/ThermixStudio.App/Services/ThermalAnalysisService.cs#L1196).
- Extração de visível com múltiplos caminhos C#: parsing APP1/FFF FLIR, `IExifToolService.TryExtractVisibleImageAsync` e melhoria OpenCvSharp: [src/ThermixStudio.App/Services/ThermalAnalysisService.cs](src/ThermixStudio.App/Services/ThermalAnalysisService.cs).
- Pós-processamento de JPEG visível com regras por luminância, CLAHE e gamma/offset: [src/ThermixStudio.App/Services/ThermalAnalysisService.cs](src/ThermixStudio.App/Services/ThermalAnalysisService.cs#L965), [src/ThermixStudio.App/Services/ThermalAnalysisService.cs](src/ThermixStudio.App/Services/ThermalAnalysisService.cs#L1027).

Parecer:
- O Thermix já supera o Blackbody na robustez de extração e recuperação de JPEG visível.
- O ponto mais sensível hoje não é extração, e sim consistência de render final entre modos e redimensionamentos.

### 2) Tratamento de paletas e mapeamento térmico

#### Blackbody
- Estrutura de paletas extensa e normalizada em tabelas de 256 cores: [experiments/blackbody/src/lib/palettes/mod.rs](experiments/blackbody/src/lib/palettes/mod.rs).
- Possibilidade de usar paleta embutida da câmera quando presente: [experiments/blackbody/src/gtkui/app_window.rs](experiments/blackbody/src/gtkui/app_window.rs#L486).

#### Thermix Studio
- Mapeamento por control points (LerpStops) para Iron e Rainbow; grayscale simples: [src/ThermixStudio.App/Services/ThermalRenderEngine.cs](src/ThermixStudio.App/Services/ThermalRenderEngine.cs#L76), [src/ThermixStudio.App/Services/ThermalRenderEngine.cs](src/ThermixStudio.App/Services/ThermalRenderEngine.cs#L106), [src/ThermixStudio.App/Services/ThermalRenderEngine.cs](src/ThermixStudio.App/Services/ThermalRenderEngine.cs#L127).
- Enum contém Hotmetal, mas normalização atual limita as paletas suportadas na prática: [src/ThermixStudio.Core/DomainModels.cs](src/ThermixStudio.Core/DomainModels.cs#L36), [src/ThermixStudio.App/ViewModels/MainViewModel.cs](src/ThermixStudio.App/ViewModels/MainViewModel.cs#L1600).

Parecer:
- O Thermix tem boa base de calibração visual para Iron/Rainbow, porém com baixo repertório e sem ingestão da paleta embutida FLIR.
- Portar suporte a paleta embutida e ampliar LUTs de paleta trará ganho perceptivo e compatibilidade visual com captura original.

### 3) Pontos de ganho em nitidez e qualidade visual portáveis ao Thermix

#### Ganhos de alto impacto
1. Implementar LUT de 256 cores por paleta no render engine.
2. Adicionar opção de usar paleta embutida FLIR quando disponível.
3. Cachear buffers BGRA de visível redimensionado por modo/dimensão para evitar múltiplas conversões e reamostragens.
4. Revisar reamostragem para alta qualidade em caminhos de escala (especialmente PiP e visível com escala diferente).

#### Ganhos de médio impacto
1. Aplicar blend em espaço linear (aproximação gamma-correct) para reduzir aspecto lavado em Blending.
2. Tornar ComposeMsx adaptativo ao conteúdo térmico local (não só borda visível), para preservar detalhe sem escurecimento excessivo.
3. Expor presets de nitidez visível (Natural, Detalhe, Suave) aproveitando a base de CLAHE/gamma já existente.

#### Ganhos de baixo risco e rápida entrega
1. Expandir paletas para incluir Hotmetal real e mais 2 paletas técnicas.
2. Persistir indicador de origem da paleta (embutida ou custom) no ProcessingJson.
3. Incluir exportação opcional PNG/TIFF do frame renderizado final com metadados de escala aplicada.

## Recomendações objetivas para o Thermix Studio

### Roadmap sugerido
1. Sprint curta (1-2 semanas): LUT 256, expansão de paletas, cache de redimensionamento.
2. Sprint média (2-3 semanas): paleta embutida FLIR e blend gamma-correct.
3. Sprint avançada (3-4 semanas): refinamento MSX orientado a conteúdo e presets de qualidade.

### Métricas para validar melhoria
1. Similaridade visual contra saída original da câmera (erro médio por pixel em BGRA/DeltaE aproximado).
2. Tempo médio de atualização de modo (Thermal, Visible, Blending, PiP, MSX).
3. Taxa de falha na associação de imagem visível por lote importado.

## Conclusão final
- O Blackbody contribui principalmente com disciplina de paleta e consistência de render térmico.
- O Thermix Studio já possui pipeline de JPEG e extração visível mais completo e resiliente.
- O melhor caminho não é substituir o pipeline atual pelo do Blackbody, e sim incorporar dele:
  - estratégia de paletas robusta (incluindo embutida),
  - mapeamento térmico com LUT mais densa,
  - simplificação de caminhos de render para reduzir variação perceptiva.

Com isso, o Thermix tende a ganhar fidelidade visual, previsibilidade entre modos e melhor qualidade percebida em JPEG visível sem perder o diferencial de análise radiométrica já existente.