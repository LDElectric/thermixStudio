Objetivo

Investigar e implementar um modo de AutoScale Visual compatível com o comportamento observado em termogramas FLIR.

Importante:

Não alterar a matriz radiométrica.
Não alterar medições de temperatura.
Não alterar spots, áreas ou análises térmicas.
Não alterar cálculos físicos.
A mudança deve afetar apenas a renderização visual da paleta.
Etapa 1 – Auditoria dos metadados já extraídos

Verificar todos os metadados atualmente retornados pelo ExifTool e identificar se já existem campos relacionados a:

Palette
PaletteName
PaletteColors
AboveColor
BelowColor
OverRangeColor
UnderRangeColor
Isotherm
Level
Span
DisplayRange
ThermalRange
ColorBar
quaisquer outros campos FLIR relacionados à renderização visual

Gerar uma lista completa dos campos encontrados e informar:

nome do campo
valor
local onde o valor é armazenado no sistema
se já está sendo utilizado pelo renderizador

Objetivo: descobrir se as cores de under-range e over-range já estão disponíveis nos metadados e se já estão sendo carregadas.

Etapa 2 – Verificar suporte atual a cores Below/Above

Investigar se o renderizador atualmente utiliza cores específicas para:

temperaturas abaixo do range visual
temperaturas acima do range visual

Caso existam metadados equivalentes a BelowColor e AboveColor:

verificar se são lidos
verificar se são armazenados
verificar se são aplicados no render

Caso não estejam sendo utilizados:

criar suporte para armazenar essas cores
disponibilizar essas cores para o pipeline de renderização

Sem alterar o comportamento atual ainda.

Etapa 3 – Localizar pontos de AutoScale

Identificar exatamente onde o AutoScale atual é calculado.

Documentar:

classe
método
origem do Tmin/Tmax utilizados atualmente
se usa Min/Max da matriz radiométrica
se usa histograma
se usa percentis
se usa outro algoritmo

Objetivo: entender o ponto correto para inserir um modo FLIR-like.

Etapa 4 – Implementar AutoScale Visual FLIR

Criar um novo conceito separado:

VisualMinTemperature
VisualMaxTemperature

Esses valores NÃO devem alterar:

dados radiométricos
spots
medições
exportações térmicas

Devem afetar apenas o mapeamento da paleta.

O renderizador deve usar:

VisualMinTemperature
VisualMaxTemperature

como limites da distribuição da LUT.

Etapa 5 – Recuperar VisualMin/VisualMax a partir da imagem

Implementar um mecanismo experimental para detectar os valores exibidos na barra lateral do termograma FLIR.

Objetivo:

Extrair da imagem renderizada os valores que aparecem na escala lateral, por exemplo:

40.0°C
25.0°C

Esses valores devem alimentar:

VisualMinTemperature
VisualMaxTemperature
Restrições para a implementação

Não utilizar:

Tesseract
OCR externo
dependências pesadas
modelos de IA
LLM

Preferir:

processamento de imagem
template matching
reconhecimento simples de caracteres
algoritmos próprios
Etapa 6 – Integração com a renderização

Quando os valores forem detectados:

Utilizar:

v=
VisualMax−VisualMin
T−VisualMin
	​


para mapear a temperatura radiométrica para a posição na LUT.

Comportamento esperado:

temperaturas abaixo de VisualMin usam BelowColor (quando disponível)
temperaturas acima de VisualMax usam AboveColor (quando disponível)
temperaturas dentro da faixa usam a paleta normalmente
Resultado esperado

O render deve reproduzir o comportamento visual da FLIR o mais próximo possível:

mesma distribuição visual da paleta
mesmo clipping visual
mesmo comportamento de temperaturas abaixo da faixa
mesmo comportamento de temperaturas acima da faixa
mesma percepção de contraste

Mantendo toda a radiometria original intacta.