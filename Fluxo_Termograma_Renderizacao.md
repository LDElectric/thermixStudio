# Fluxo de Importação e Renderização de Termogramas - Thermix Studio

Este documento descreve detalhadamente como o Thermix Studio importa, processa e renderiza as imagens térmicas, servindo de guia técnico de manutenção e arquitetura.

## 1. Importação de Termogramas
- **Componentes**: `MainViewModel.cs` (`OpenFileAsync`, `ImportFilesAsync`), `ThermalAnalysisService.cs`
- **Como funciona**:
  1. O usuário seleciona o arquivo via interface ou importação da câmera.
  2. `ThermalAnalysisService.LoadImageAsync` lê o arquivo e utiliza o ExifTool (`IExifToolService`) para extrair os metadados radiométricos e a matriz de temperaturas (com dados brutos/Celsius). O resultado preenche o objeto `ThermalImageData`.
  3. O `ThermalViewPipeline.PrepareThermogramAsync` é acionado para a detecção de modos originais e extração de imagens visíveis emparelhadas (foto real) quando estão anexadas ao JPG ou presentes no diretório.

## 2. Metadados e Ajuste Automático de Escala
- **Modelos**: `RadiometricMetadata`, `ThermalProcessingState`, `ThermalImageData` (em `DomainModels.cs`)
- **Como funciona**:
  - O aplicativo preserva o estado de exibição do usuário (modo PiP/MSX, paleta escolhida, fator de mescla) dentro do banco de dados local via propriedade `ProcessingJson`.
  - No Auto-Scale (`MainViewModel.GetPreferredThermalRange`), o motor busca os limites originais do momento da foto (`PaletteScaleMinC` e `PaletteScaleMaxC`) guardados no Exif. Se o usuário alterar a imagem ou os dados não existirem, ele faz a varredura e pega as temperaturas mínimas/máximas reais da matriz em cena.

## 3. O "Maestro": Pipeline de Visualização
- **Componente**: `ThermalViewPipeline.cs`
- **Como funciona**:
  Atua como a ponte/fachada entre as visões do WPF (`MainViewModel`) e os motores de processamento de baixo nível. Em vez de o VM misturar pixels com matrizes e paletas, ele chama o `ThermalViewPipeline`.
  - Delega o tratamento de cor ao `ThermalPaletteEngine`.
  - Delega a mistura de imagens térmicas/visuais ao `ThermalModeEngine`.

## 4. O Motor de Paletas (ThermalPaletteEngine)
- **Componente**: `ThermalPaletteEngine.cs`
- **Responsabilidades e Fluxo Analítico**:
  - **Carregamento de Cores (LUTs)**: Lê do diretório de aplicação `/paletas` (arquivos `.json` com arrays RGB) e salva em memória cache.
  - **Renderização Radiométrica (`RenderThermalWithPaletteAsync`)**:
    - **O SEGREDO DA CALIBRAÇÃO (Correção Logarítmica)**: O mapeamento de temperatura para cor NÃO é estritamente linear nas câmeras FLIR. As câmeras mapeiam o "Sinal" (radiação eletromagnética bruta 14-bits) na paleta. Para conseguir as cores originais exatas e evitar um termograma "muito amarelo" (Iron) ou "muito vermelho" (Rainbow), a engine reverte as Temperaturas para Valores de Sinal utilizando a Equação de Planck, caso os parâmetros estejam presentes no metadado (`PlanckR1`, `PlanckR2`, `PlanckB`, `PlanckF`, `PlanckO`). Após essa conversão para Sinal, o valor é interpolado de forma linear sobre a paleta (LUT), reproduzindo a resposta fiel da câmera. Caso não tenha os metadados, o mapeamento é feito linearmente através das temperaturas em °C.
  - **ProcessSmartHD**: Uma função auxiliar utilizada em fluxos onde se faz a "troca" inteligente de paletas operando diretamente sobre os pixels do JPG original (substituindo cores aproximadas) para casos isolados.

## 5. O Motor de Modos de Imagem (ThermalModeEngine)
- **Componente**: `ThermalModeEngine.cs`
- **Como funciona**:
  - Mescla o mapa de pixels de cores gerado pelo PaletteEngine com os pixels da foto visível real da câmera.
  - **Modos principais**:
    - `Blending`: Mescla por transparência/opacidade (alpha blending) da imagem IR na visível.
    - `PiP (Picture in Picture)`: Sobrepõe um recorte quadrado centrado.
    - `MSX (Multi-Spectral Dynamic Imaging)`: Processa a foto visível buscando os contornos através do detector de bordas (Algoritmo de Sobel/Canny Edge) e aplica as bordas escurecidas/contrastadas por cima da imagem térmica, permitindo a leitura de placas e textos sem misturar cores irrelevantes.
  - **`OverlayCameraUI` (Isolador da Interface Flir)**: Ele isola (corta) os elementos pretos, cinzas e brancos (Logo FLIR, Termômetro Lateral, Tmax/Tmin) do JPEG original e os prega sobre o canvas final. Isso garante que, independentemente da paleta modificada, as molduras originais da câmera continuem nítidas e inalteradas.

## 6. Exportação de Arquivos Identicos
- **Componente**: `MainViewModel.cs` (`ExportIdenticalJpgAsync`)
- **Fluxo de Execução**:
  1. Renderiza os pixels térmicos usando a mesma lógica acima (Plack Scaling -> LUT Mapping).
  2. Compõe o modo atual (Blending, PiP, etc).
  3. Restaura os elementos OverlayCameraUI da tela.
  4. Salva a imagem estritamente nas dimensões originais matriciais (por exemplo, 320x240) ignorando a ampliação interna da tela (zoom 2x do canvas do programa).
  5. Aciona o ExifTool por baixo dos panos executando o comando `-overwrite_original -TagsFromFile "original.jpg" -all:all "exportado.jpg"`, garantindo que o arquivo exportado permaneça compatível com sistemas Flir/outros, contendo a matriz de 14-bits original incólume.

---
Este guia consolida todas as engrenagens de processamento térmico em um formato fácil de acompanhar para futuras atualizações ou intervenções no sistema de renderização.
