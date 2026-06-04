# Renderizador de Termogramas FLIR com Escala de Temperatura Configurável

## Contexto

Estamos desenvolvendo um renderizador de termogramas FLIR que deve recriar programaticamente a aparência visual da câmera, permitindo controlar a paleta e a faixa de temperatura visível sem depender do overlay original da imagem.

A imagem original da câmera continua sendo usada como referência visual e como fonte de metadados. A exceção deliberada é o logotipo FLIR, que deve ser preservado por overlay limpo, sem recriação manual.

## Diagnóstico do Plano Atual

O objetivo está correto, mas o plano precisa ser ajustado ao estado real do projeto:

- O projeto já possui pipeline separado em `ThermalViewPipeline`, `ThermalPaletteEngine` e `ThermalModeEngine`.
- As paletas por LUT JSON já existem e cobrem Iron, Rainbow, Grayscale e várias outras.
- A paleta `Original` embarcada já aparece no modelo e no renderizador.
- O overlay atual ainda copia/reconstrói parte da UI a partir da imagem original por detecção de luminância.
- A barra de escala já tem um primeiro redesenho por LUT em `ThermalModeEngine.DrawPaletteScaleBar`, mas o fluxo atual chama `OverlayCameraUI` sem passar `mode` e `paletteName`, então esse caminho pode não ser usado na tela principal.
- A escala térmica manual já existe via `LevelMinC`, `LevelMaxC` e `AutoScaleEnabled`, mas ainda falta separar "escala de coloração" de "limiar visível/análise", que têm comportamentos diferentes.
- A área da imagem ainda exibe no rodapé uma barra fixa "Frio -> Quente", independente da paleta e da escala real do termograma. Essa barra deve ser removida para não conflitar com a escala vertical do próprio termograma.
- O auto-scale atual pode usar min/max da matriz radiométrica em vez da escala visual gravada pela câmera. Para `1MSX.jpg`, por exemplo, o termograma mostra 19.0 C a 43.2 C, enquanto o programa pode exibir 26.6 C a 57.7 C. A escala inicial do programa deve preferir a escala visível do termograma.
- Atenção: a escala visual do EXIF não deve ser aplicada automaticamente como intervalo da LUT se a matriz radiométrica carregada estiver em outra faixa, pois isso altera a distribuição da paleta e pode deixar a paleta Ferro amarelada/saturada. Enquanto a calibração radiométrica não estiver comprovadamente alinhada ao display FLIR, separar escala exibida de faixa de coloração.

Portanto, o plano refinado deve focar menos em "criar paletas" e mais em:

1. Definir um contrato claro para renderização térmica com faixa visível.
2. Redesenhar a UI FLIR em vez de copiá-la do JPEG.
3. Medir e calibrar a fidelidade visual contra a referência.
4. Preservar apenas o logotipo FLIR por overlay.

## Objetivo Principal

Renderizar termogramas FLIR com controle total sobre:

- Paleta ativa.
- Escala térmica usada para mapear temperatura em cor.
- Faixa visível configurável pelo usuário.
- Escurecimento progressivo de pixels fora da faixa configurada.
- Elementos visuais da UI FLIR redesenhados programaticamente.

O resultado deve manter a aparência da câmera, mas sem depender dos textos, caixas, mira e escala originais do JPEG.

## Escopo Funcional

### Dados térmicos e metadados

Usar a matriz radiométrica já carregada em `ThermalImageData.Temperatures`.

Usar preferencialmente estes campos de `RadiometricMetadata`:

| Campo | Uso |
| --- | --- |
| `PaletteScaleMinC` | limite inferior original da barra de escala da câmera |
| `PaletteScaleMaxC` | limite superior original da barra de escala da câmera |
| `PlanckR1`, `PlanckR2`, `PlanckB`, `PlanckF`, `PlanckO` | mapeamento radiométrico/sinal quando disponível |
| `Emissivity`, `AmbientTemperatureC`, `ReflectedTemperatureC`, `RelativeHumidity`, `ObjectDistanceM` | base para cálculo radiométrico futuro |
| `Real2IR`, `OffsetX`, `OffsetY` | alinhamento MSX/visível |

Adicionar, se ainda não existir no parsing EXIF:

- `SpotTemperatureC`: temperatura do retículo central/alvo.
- `ScaleMinTemperatureC` e `ScaleMaxTemperatureC` como aliases explícitos, mesmo que internamente apontem para `PaletteScaleMinC` e `PaletteScaleMaxC`.
- Parser tolerante para textos do spot/alvo com ou sem aproximação, por exemplo `~41.8º C`, `~41.8° C`, `41.5º C` ou `41.5 C`.

### Escala térmica vs faixa visível

Separar dois conceitos:

- **Escala de coloração**: intervalo usado para transformar temperatura em índice da LUT. Hoje corresponde a `LevelMinC`/`LevelMaxC`.
- **Faixa visível configurável**: intervalo de análise escolhido pelo usuário. Pixels fora dele não somem; ficam progressivamente escurecidos.
- **Escala visível da câmera**: números gravados/exibidos pela FLIR na barra vertical. Deve ser exibida/replicada na UI FLIR, mas não deve forçar a normalização da LUT sem validação, para evitar mudança de cor.

Exemplo:

> Quero ver apenas o que está acima de 35 °C; tudo abaixo fica escuro, mas com contornos MSX visíveis.

Nesse caso:

- `LevelMinC`/`LevelMaxC` continuam controlando a distribuição de cores.
- `VisibleMinC = 35` escurece pixels abaixo de 35 °C.
- O MSX continua aplicado por cima ou preservado durante a composição, para manter leitura estrutural.

## Modelo de Estado Proposto

Adicionar ao estado de processamento:

```csharp
public double? VisibleMinC { get; set; }
public double? VisibleMaxC { get; set; }
public bool EnableVisibleRangeMask { get; set; }
public double OutOfRangeMinOpacity { get; set; } = 0.15;
public double OutOfRangeFadeWidthC { get; set; } = 3.0;
public bool PreserveMsxEdgesOutsideRange { get; set; } = true;
```

Regras:

- Se `EnableVisibleRangeMask == false`, renderização permanece como hoje.
- Se apenas `VisibleMinC` for informado, escurecer progressivamente tudo abaixo dele.
- Se apenas `VisibleMaxC` for informado, escurecer progressivamente tudo acima dele.
- Se ambos forem informados, manter normal somente o intervalo fechado `[VisibleMinC, VisibleMaxC]`.
- `OutOfRangeFadeWidthC` define a suavidade da transição.
- `OutOfRangeMinOpacity` define o quanto o pixel ainda aparece longe do limite.

## Algoritmo de Escurecimento Progressivo

Após renderizar os pixels térmicos com a paleta, aplicar uma máscara por temperatura:

```text
distância = quanto o pixel está fora da faixa configurada
fade = clamp(distância / OutOfRangeFadeWidthC, 0, 1)
opacidade = lerp(1.0, OutOfRangeMinOpacity, fade)
pixel_final = pixel_paleta * opacidade
```

Observações:

- A transição deve ser suave.
- Não deve haver corte abrupto.
- A máscara deve ser aplicada antes da composição MSX quando o objetivo for escurecer a cor térmica e preservar contornos.
- Em `Blending`/`PiP`, validar visualmente se a máscara deve entrar antes ou depois da composição. A recomendação inicial é aplicar no térmico antes de `ComposeViewMode`.

## UI FLIR a Recriar Programaticamente

### Elementos que devem ser redesenhados

- Caixa de temperatura do alvo no topo esquerdo.
- Caixa de temperatura máxima no topo direito, quando aplicável.
- Caixa de temperatura mínima no canto inferior direito, quando aplicável.
- Barra lateral de escala com gradiente da paleta ativa.
- Valores numéricos da escala.
- Retículo/mira central.
- Molduras, padding, espessuras e cores das caixas.

### Elemento preservado por overlay

- Logotipo FLIR no canto inferior esquerdo.

Esse overlay deve ser restrito à área do logo e usar critério conservador de pixels claros, evitando copiar partes da cena.

## Estratégia de Fidelidade Visual

Antes de redesenhar, medir a imagem de referência em coordenadas normalizadas para base 320x240:

| Elemento | Medidas a levantar |
| --- | --- |
| Caixa alvo | x, y, largura, altura, padding, raio se houver, cor de fundo, borda |
| Caixa Tmax | x, y, largura, altura, padding, alinhamento do texto |
| Caixa Tmin | x, y, largura, altura, padding, alinhamento do texto |
| Barra de escala | x, y, largura, altura, moldura, faixa interna, ticks |
| Textos da escala | posição, fonte aproximada, tamanho, cor |
| Retículo | centro, comprimento das hastes, espessura, ticks, cor |
| Logo | bounding box exata para overlay |

Guardar essas medidas em uma estrutura única, por exemplo:

```csharp
public sealed class FlirOverlayLayout
{
    public Size ReferenceSize { get; init; } = new(320, 240);
    public Rect SpotBox { get; init; }
    public Rect MaxBox { get; init; }
    public Rect MinBox { get; init; }
    public Rect ScaleBar { get; init; }
    public Rect LogoBox { get; init; }
    public Point CrosshairCenter { get; init; }
}
```

Todas as posições devem ser escaladas proporcionalmente para outras resoluções.

## Arquitetura Recomendada

### 1. Novo serviço de desenho de UI FLIR

Criar um serviço dedicado, em vez de aumentar `ThermalModeEngine`:

```text
src/ThermixStudio.App/Services/FlirCameraOverlayRenderer.cs
```

Responsabilidades:

- Desenhar caixas pretas e bordas.
- Desenhar texto de temperatura.
- Desenhar barra de escala pela LUT ativa.
- Desenhar retículo.
- Aplicar overlay do logo FLIR a partir do original.

Contrato sugerido:

```csharp
public sealed class FlirCameraOverlayRenderRequest
{
    public byte[] Pixels { get; init; } = [];
    public byte[]? OriginalPixels { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ThermalPaletteLutData? PaletteLut { get; init; }
    public double ScaleMinC { get; init; }
    public double ScaleMaxC { get; init; }
    public double? SpotTemperatureC { get; init; }
    public double? MaxTemperatureC { get; init; }
    public double? MinTemperatureC { get; init; }
    public ImageViewMode ViewMode { get; init; }
}
```

### 2. Ajustar `ThermalViewPipeline`

Substituir gradualmente:

```csharp
OverlayCameraUI(...)
```

por:

```csharp
RenderCameraOverlay(...)
```

Manter `OverlayCameraUI` temporariamente como fallback/debug.

### 3. Corrigir passagem de modo e paleta no fluxo atual

No fluxo atual, `MainViewModel.Rendering.cs` chama:

```csharp
_viewPipeline.OverlayCameraUI(finalPixels, originalPixels, width, height);
```

Isso perde `ImageViewMode` e `SelectedPalette`, impedindo que o overlay adapte corretamente barra e comportamento por modo.

Mesmo antes do novo renderer, ajustar para:

```csharp
_viewPipeline.OverlayCameraUI(
    finalPixels,
    originalPixels,
    width,
    height,
    ImageViewMode,
    SelectedPalette.ToString());
```

Essa correção é um passo curto e reduz inconsistências enquanto o redesenho completo não entra.

## Fases de Implementação

### Fase 0 - Calibração e baseline

Objetivo: criar uma base objetiva para comparar antes/depois.

Tarefas:

- Escolher 2 ou 3 termogramas FLIR reais como referência.
- Exportar render atual e original lado a lado.
- Medir bounding boxes da UI em 320x240.
- Registrar fonte aproximada, tamanho e cores.
- Criar imagens de debug com boxes desenhados por cima da referência.

Critério de aceite:

- Existe uma tabela de layout com coordenadas normalizadas.
- Existe pelo menos uma comparação visual baseline.

### Fase 1 - Corrigir fluxo de overlay atual

Objetivo: reduzir divergências imediatas usando a arquitetura existente.

Tarefas:

- Remover a barra fixa "Frio/Quente" do rodapé da área da imagem; manter apenas o rótulo textual da escala atual, se necessário.
- Fazer o rótulo/overlay de escala preferir `PaletteScaleMinC` e `PaletteScaleMaxC` quando esses metadados existirem e forem válidos.
- Manter `LevelMinC`/`LevelMaxC` como faixa de coloração derivada da matriz térmica até a calibração radiométrica ficar alinhada à escala FLIR, evitando alteração de cores.
- Passar `ImageViewMode` e `SelectedPalette` para `OverlayCameraUI`.
- Garantir que a barra de escala seja redesenhada para paletas não originais.
- Confirmar que modo `Visible` não recebe escala térmica indevida.
- Validar `Original` e `Iron` com fallback coerente.
- Ajustar parsing de números de temperatura para aceitar `~` opcional no Tspot/Tspor.

Critério de aceite:

- Ao trocar paleta, a barra lateral acompanha a paleta ativa.
- A imagem exportada não copia a barra antiga quando uma nova paleta está ativa.
- Para `1MSX.jpg`, a escala exibida/replicada deve ser aproximadamente 19.0 C - 43.2 C, igual à escala visível do termograma, sem mudar a distribuição da paleta Ferro.
- Textos de spot com `~` e sem `~` devem ser aceitos pelo parser.

### Fase 2 - Implementar faixa visível configurável

Objetivo: permitir análise por limiar mínimo/máximo sem destruir contexto visual.

Tarefas:

- Adicionar propriedades ao `ThermalProcessingState`.
- Adicionar propriedades no `MainViewModel`.
- Implementar função de máscara por temperatura no pipeline de renderização.
- Persistir a configuração em `ProcessingJson`.
- Expor controles na UI: habilitar máscara, mínimo visível, máximo visível, suavidade e opacidade mínima.

Critério de aceite:

- Pixels abaixo/acima do limiar escurecem progressivamente.
- Pixels dentro da faixa permanecem com a paleta normal.
- MSX continua legível em áreas escurecidas.
- Desativar a máscara retorna exatamente ao comportamento anterior.

### Fase 3 - Criar renderer programático da UI FLIR

Objetivo: parar de copiar caixas, textos, escala e mira do JPEG original.

Tarefas:

- Criar `FlirCameraOverlayRenderer`.
- Desenhar caixas com layout calibrado.
- Desenhar textos de temperatura com `System.Drawing` ou API WPF equivalente.
- Desenhar escala lateral com LUT ativa.
- Desenhar valores min/max conforme a escala aplicada.
- Desenhar retículo central.
- Preservar apenas o logo FLIR por overlay restrito.
- Garantir que o desenho ocorra depois da composição térmica/visível/MSX/PiP, sobre pixels sem UI de câmera copiada.
- Em modo luz visível, desenhar apenas Tspot, retículo e logo FLIR; não desenhar barra, Tmax ou Tmin.

Critério de aceite:

- Com a mesma paleta e escala original, o render fica visualmente próximo ao JPEG da câmera.
- Alterar escala ou paleta atualiza textos e barra corretamente.
- Nenhum texto antigo da câmera é copiado, exceto logo.
- Não há sobreposição dupla entre UI antiga preservada e UI nova programática.

Status inicial implementado:

- O overlay programático foi integrado no `ThermalModeEngine`.
- A barra vertical passa a usar a LUT ativa para paletas nomeadas.
- Caixas de Tspot, Tmax, Tmin e retículo são desenhadas programaticamente.
- O logo FLIR continua preservado por overlay restrito.
- O modo `Visible` usa overlay reduzido: Tspot, retículo e logo.
- Ainda é necessário calibrar pixel a pixel fonte, dimensões e offsets contra a referência FLIR.

### Fase 4 - EXIF e temperaturas exibidas

Objetivo: garantir que os números exibidos sejam dados corretos, não pixels copiados.

Tarefas:

- Confirmar quais tags EXIF fornecem temperatura do spot/alvo.
- Se `SpotTemperature` não estiver disponível, calcular a temperatura no centro do retículo pela matriz.
- Calcular `MinTemperatureC` e `MaxTemperatureC` a partir da matriz ou região visível, conforme decisão de produto.
- Usar `PaletteScaleMinC`/`PaletteScaleMaxC` como escala inicial preferencial.

Decisão recomendada:

- Números da barra devem refletir a escala de coloração aplicada.
- Caixa do alvo deve refletir o pixel/spot do retículo.
- Tmax/Tmin devem refletir a matriz térmica total, salvo se futuramente houver modo "min/max da faixa visível".

Critério de aceite:

- Valores exibidos batem com a matriz radiométrica dentro de tolerância definida.
- Ao mudar escala manual, os números da barra mudam.
- Ao mudar apenas faixa visível, a barra não muda, a menos que o usuário também mude a escala.

### Fase 5 - Validação visual e regressão

Objetivo: garantir fidelidade e evitar quebrar modos existentes.

Tarefas:

- Comparar Original, Thermal, MSX, Blending e PiP.
- Testar Iron, Rainbow, Grayscale e Original.
- Testar escala automática, escala manual e faixa visível.
- Exportar imagens antes/depois.
- Validar relatórios que usam `LevelMinC`/`LevelMaxC`.

Critérios de aceite:

- Sem regressão no carregamento de imagens FLIR.
- Sem perda de alinhamento MSX/FOV.
- Sem textos sobrepostos em 320x240 e em resoluções maiores.
- Exportação mantém metadados originais quando aplicável.

## Riscos e Mitigações

| Risco | Impacto | Mitigação |
| --- | --- | --- |
| Fonte FLIR exata não estar disponível | médio | usar fonte visualmente equivalente e calibrar tamanho/peso |
| Coordenadas fixas falharem em outros modelos FLIR | alto | usar layout por perfil de câmera/modelo, com fallback 320x240 |
| Máscara escurecer demais e perder contexto | médio | tornar opacidade mínima e suavidade configuráveis |
| MSX ficar fraco fora da faixa visível | médio | aplicar máscara antes do MSX ou preservar bordas após máscara |
| Overlay do logo copiar cena junto | médio | limitar bounding box e copiar apenas pixels claros/baixa saturação |
| Confusão entre escala e faixa visível | alto | separar nomes na UI e no estado interno |

## Ordem Recomendada

1. Corrigir passagem de `mode` e `paletteName` no overlay atual.
2. Adicionar máscara de faixa visível configurável.
3. Calibrar layout da UI FLIR em 320x240.
4. Criar `FlirCameraOverlayRenderer`.
5. Migrar caixas, textos, escala e retículo para desenho programático.
6. Manter apenas logo FLIR por overlay.
7. Validar contra imagens reais e exportação.

## Fora do Escopo Inicial

- Recriar o logotipo FLIR vetorialmente.
- Implementar novos modelos radiométricos além do que já existe.
- Criar novas paletas.
- Alterar o alinhamento FOV/MSX, exceto se a validação revelar regressão.
- Mudar a geração de relatórios, exceto para refletir novos campos persistidos.

## Definição de Pronto

O recurso estará pronto quando:

- O usuário conseguir definir mínimo e/ou máximo visível.
- Pixels fora da faixa forem escurecidos de forma suave.
- A barra de escala e seus números refletirem a escala ativa.
- Caixas de temperatura, textos e retículo forem desenhados pelo app.
- Apenas o logotipo FLIR vier do JPEG original.
- A troca de paleta atualizar o termograma e a escala lateral de forma coerente.
- A imagem resultante for visualmente próxima da referência da câmera FLIR.
