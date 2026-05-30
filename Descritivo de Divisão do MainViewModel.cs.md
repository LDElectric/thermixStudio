# Descritivo de Divisão do MainViewModel.cs

A refatoração do arquivo `MainViewModel.cs` foi realizada utilizando o recurso de **Classes Parciais (`partial classes`)** do C#. Esta abordagem permite que uma única classe seja definida em múltiplos arquivos físicos, facilitando a manutenção e organização do código sem alterar o comportamento funcional ou exigir modificações em outras partes do sistema.

## Estrutura da Divisão

O arquivo original de ~3.700 linhas foi dividido em **6 arquivos menores**, cada um focado em uma responsabilidade específica:

| Arquivo | Responsabilidade Principal |
| :--- | :--- |
| **MainViewModel.cs** | **Estrutura Base**: Contém a declaração da classe, propriedades observáveis (`ObservableProperty`), injeção de dependência e o construtor principal. |
| **MainViewModel.FileOps.cs** | **Operações de Arquivo**: Gerencia o carregamento de dados, abertura de arquivos, importação de termogramas e detecção de câmeras conectadas. |
| **MainViewModel.Rendering.cs** | **Renderização e Visualização**: Contém a lógica de processamento de imagem, incluindo os modos MSX, PiP, Blending, Fusion e a gestão de paletas térmicas. |
| **MainViewModel.Measurements.cs** | **Ferramentas de Análise**: Implementa as funcionalidades de medição (Spot, Área, Linha, Círculo), cálculos de diferença e gestão de isotermas. |
| **MainViewModel.Export.cs** | **Exportação e Relatórios**: Gerencia a exportação de dados para CSV, geração de imagens JPG com metadados e a integração com o editor de relatórios. |
| **MainViewModel.Illustrations.cs** | **Interface e Edição**: Controla as ilustrações sobrepostas à imagem, o sistema de histórico (Undo/Redo) e comandos de exclusão de itens. |

## Benefícios da Refatoração

1.  **Manutenibilidade**: Desenvolvedores podem localizar rapidamente o código relevante para uma funcionalidade específica sem navegar por milhares de linhas.
2.  **Segurança**: Como a classe permanece a mesma (`partial`), não há risco de quebrar referências de XAML ou de outros ViewModels que dependem do `MainViewModel`.
3.  **Organização**: Separa a lógica de baixo nível (como manipulação de pixels em `Rendering`) da lógica de alto nível (como comandos de UI em `MainViewModel`).
4.  **Conformidade**: Mantém todos os imports e chamadas equivalentes, garantindo que a injeção de dependência continue funcionando perfeitamente.

## Instruções de Uso

Para aplicar esta mudança, basta substituir o arquivo `MainViewModel.cs` antigo pelos 6 novos arquivos na pasta `ViewModels` do seu projeto. O compilador do Visual Studio ou .NET identificará automaticamente que eles compõem a mesma classe.
