# Descrição dos Arquivos Refatorados do MainViewModel

Este documento descreve a estrutura e as responsabilidades de cada arquivo C# resultante da refatoração do `ORIGINAL_MainViewModel.cs`.

## 1. `MainViewModel.cs`

Este arquivo contém a definição principal da classe `MainViewModel`, que herda de `ObservableObject` (do `CommunityToolkit.Mvvm.ComponentModel`) para implementar o padrão MVVM. Ele declara as propriedades observáveis, os comandos assíncronos e os eventos que são centrais para a interação da interface do usuário com a lógica de negócios. Também define enums auxiliares como `AnalysisTool`, `ImageViewMode`, `IsothermMode` e `EquipmentCriticality`.

### Propriedades Principais:

*   `SelectedThermogram`: O termograma atualmente selecionado.
*   `SelectedInspection`: A inspeção atualmente selecionada.
*   `CurrentImagePath`: Caminho da imagem atual.
*   `StatusMessage`: Mensagens de status para exibição na UI.
*   `IsothermThresholdC`, `IsothermUpperThresholdC`, `SelectedIsothermMode`: Propriedades relacionadas à funcionalidade de isotermas.
*   `HumidityRelativeLimit`, `InsulationIndoorC`, `InsulationOutdoorC`, `InsulationThermalIndex`: Propriedades para presets de umidade e isolamento.
*   `ThermogramEquipmentTag`, `ThermogramEquipmentDescription`, `ThermogramEquipmentLocation`, `ThermogramNotes`, `ThermogramCriticality`: Propriedades editáveis do termograma.
*   `ActiveTool`: Ferramenta de análise atualmente ativa.
*   `ImageViewMode`: Modo de visualização da imagem (Térmica, Visível, Fusão, etc.).
*   `DisplayImage`: A imagem a ser exibida na UI.
*   `SelectedMeasurement`: A medição atualmente selecionada.
*   `PairedVisibleImagePath`: Caminho da imagem visível pareada.
*   `AutoScaleEnabled`, `LevelMinC`, `LevelMaxC`: Propriedades para controle de escala de temperatura.
*   `Emissivity`: Emissividade para cálculos térmicos.
*   `SelectedPalette`: Paleta de cores selecionada.
*   `CurrentScaleLabel`: Rótulo da escala atual.
*   `BlendFactor`, `PipScale`, `MsxStrength`: Fatores para modos de visualização de fusão.

### Comandos Principais:

*   `LoadDataCommand`: Carrega dados.
*   `OpenFileCommand`: Abre um arquivo.
*   `AddSpotCommand`, `AddAreaCommand`, `AddLineCommand`, `AddCircleCommand`, `AddDifferenceCommand`, `AddIsothermCommand`: Comandos para adicionar diferentes tipos de medições.
*   `DefineAutoAdjustRegionCommand`, `ClearAutoAdjustRegionCommand`: Comandos para definir e limpar a região de ajuste automático.
*   `ApplyHumidityPresetCommand`, `ApplyInsulationPresetCommand`: Comandos para aplicar presets de umidade e isolamento.
*   `ExportImageCsvCommand`, `ExportMeasurementsCsvCommand`, `ExportIdenticalJpgCommand`, `GenerateReportCommand`: Comandos para exportação e geração de relatórios.
*   `SaveThermogramPropertiesCommand`: Salva as propriedades do termograma.
*   `ToggleViewModeCommand`: Alterna o modo de visualização.
*   `UndoLastActionCommand`: Desfaz a última ação.
*   `RemoveSelectedMeasurementCommand`, `RemoveSelectedThermogramCommand`, `DeleteSelectionCommand`: Comandos para remover medições ou termogramas.
*   `AutoScaleCommand`: Aplica a escala automática.

### Eventos:

*   `MeasurementRemoved`: Disparado quando uma medição é removida.
*   `ReportSnapshotRequested`: Disparado para solicitar um snapshot para o relatório.

## 2. `MainViewModel.FileOps.cs`

Este arquivo agrupa as operações relacionadas a arquivos, como abertura, importação e carregamento de termogramas.

### Métodos:

*   `OpenFileAsync()`: Abre um arquivo de termograma.
*   `LoadDataAsync()`: Carrega dados de termogramas e inspeções.
*   `LoadThermogramAsync(Thermogram thermogram)`: Carrega um termograma específico.
*   `ImportThermogramAsync(CameraImportSource source)`: Importa um termograma de uma fonte de câmera.
*   `EnsureManagedLibraryRoot()`: Garante que o diretório raiz da biblioteca gerenciada exista.
*   `LogToFile(string message)`: Grava mensagens de depuração em um arquivo de log.

## 3. `MainViewModel.Illustrations.cs`

Este arquivo lida com a gestão de ilustrações (setas, retângulos, círculos, texto) no termograma, incluindo adição, atualização, remoção e funcionalidade de desfazer.

### Propriedades:

*   `Illustrations`: Coleção observável de ilustrações.

### Métodos:

*   `RemoveIllustrationByIdAsync(Guid id)`: Remove uma ilustração pelo ID.
*   `AddIllustrationAsync(IIllustration illustration)`: Adiciona uma nova ilustração.
*   `UpdateIllustrationAsync(Guid id, IIllustration illustration)`: Atualiza uma ilustração existente.
*   `PersistIllustrationsStateAsync()`: Persiste o estado das ilustrações no termograma selecionado.
*   `PushIllustrationUndoSnapshot()`: Adiciona um snapshot do estado atual das ilustrações à pilha de desfazer.
*   `GetIllustrationUndoStack(Guid thermogramId)`: Obtém a pilha de desfazer para um termograma específico.
*   `TryUndoIllustrationActionAsync()`: Tenta desfazer a última ação de ilustração.
*   `CloneIllustration(ThermalIllustration source)`: Clona uma ilustração térmica.

## 4. `MainViewModel.Measurements.cs`

Este arquivo contém a lógica para adicionar e gerenciar diferentes tipos de medições (spot, área, linha, círculo, diferença, isoterma), bem como a atualização do gráfico de tendência.

### Métodos:

*   `AddSpotAsync(double x, double y)`: Adiciona uma medição de ponto.
*   `AddAreaAsync(double x1, double y1, double x2, double y2)`: Adiciona uma medição de área.
*   `AddLineAsync(double x1, double y1, double x2, double y2)`: Adiciona uma medição de linha.
*   `AddCircleAsync(double x1, double y1, double x2, double y2)`: Adiciona uma medição de círculo.
*   `AddDifferenceAsync(Guid m1Id, Guid m2Id)`: Adiciona uma medição de diferença entre duas medições.
*   `AddIsothermAsync()`: Adiciona uma medição de isoterma.
*   `ActivateSpotToolAsync()`, `ActivateAreaToolAsync()`, `ActivateLineToolAsync()`, `ActivateCircleToolAsync()`, `ActivateAutoAdjustRegionToolAsync()`: Ativa as ferramentas de análise correspondentes.
*   `ClearAutoAdjustRegionAsync()`: Limpa a região de ajuste automático.
*   `ApplyHumidityPresetAsync()`, `ApplyInsulationPresetAsync()`: Aplica presets de umidade e isolamento.
*   `RefreshTrendPlot(IEnumerable<ThermogramTrendPoint> trendData)`: Atualiza o gráfico de tendência.
*   `GetPreferredThermalRange(ThermalImageData imageData)`: Obtém a faixa térmica preferida.
*   `UpdateMeasurement(ThermalMeasurement measurement)`: Atualiza uma medição existente.

## 5. `MainViewModel.Rendering.cs`

Este arquivo é responsável pela renderização da imagem exibida na UI, incluindo a aplicação de paletas, modos de visualização e ilustrações.

### Métodos:

*   `UpdateDisplayImage()`: Atualiza a imagem exibida na UI com base nas configurações atuais.
*   `TryLoadImageBgraPixels(string imagePath, int width, int height, out byte[]? pixels)`: Tenta carregar pixels BGRA de uma imagem.
*   `TryLoadVisibleBgraPixels(int width, int height, out byte[]? pixels)`: Tenta carregar pixels BGRA da imagem visível.
*   `TryLoadOriginalCameraBgraPixels(int width, int height, out byte[]? pixels)`: Tenta carregar pixels BGRA da imagem original da câmera.
*   `GetViewModeDisplay(ImageViewMode mode)`: Retorna uma string descritiva para o modo de visualização.
*   `NormalizeSupportedPalette(ThermalPalette palette)`: Normaliza a paleta para uma paleta suportada.
*   `ResolvePaletteFromMetadata(ThermalImageMetadata metadata)`: Resolve a paleta a partir dos metadados da imagem.

## 6. `MainViewModel.State.cs`

Este arquivo gerencia a persistência do estado do `MainViewModel` e do termograma selecionado, incluindo a sincronização de campos editáveis e o mapeamento de modos de visualização.

### Métodos:

*   `SyncEditableFieldsToSelectedThermogram()`: Sincroniza os campos editáveis da UI com o termograma selecionado.
*   `PersistCurrentStateToSelectedThermogram()`: Persiste o estado atual do ViewModel no `ProcessingJson` do termograma selecionado.
*   `PersistSelectedThermogramViewStateAsync()`: Persiste o estado de visualização do termograma selecionado de forma assíncrona.
*   `SaveVisibleImagePath(string metadataJson, string? visiblePath)`: Salva o caminho da imagem visível no JSON de metadados.
*   `BuildDefaultProcessingState(ThermalImageData? imageData)`: Constrói um estado de processamento padrão.
*   `ExtractProcessingState(string? json)`: Extrai o estado de processamento de um JSON.
*   `MapToCoreImageViewMode(ImageViewMode mode)`: Mapeia o `ImageViewMode` do ViewModel para o `ImageViewMode` do Core.
*   `MapFromCoreImageViewMode(global::ThermixStudio.Core.ImageViewMode mode)`: Mapeia o `ImageViewMode` do Core para o `ImageViewMode` do ViewModel.

## 7. `MainViewModel.ThermogramManagement.cs`

Este arquivo contém a lógica para gerenciar termogramas, incluindo ações de desfazer, remover medições e remover termogramas.

### Métodos:

*   `UndoLastActionAsync()`: Desfaz a última ação realizada (medição ou ilustração).
*   `RemoveSelectedMeasurementAsync()`: Remove a medição selecionada.
*   `RemoveSelectedThermogramAsync()`: Remove o termograma selecionado.
*   `RemoveThermogramByReferenceAsync(Thermogram? thermogram)`: Remove um termograma por referência.
*   `DeleteSelectionAsync()`: Remove a seleção atual (medição ou termograma).
*   `ApplyAutoScaleAsync()`: Aplica a escala automática à imagem.
*   `ToggleViewModeAsync()`: Alterna o modo de visualização da imagem.
*   `SetMeasurementMaxAdmissibleAsync(Guid measurementId, double? maxAdmissible)`: Define o limite máximo admissível para uma medição.
*   `RemoveMeasurementByIdAsync(Guid id)`: Remove uma medição pelo ID.
