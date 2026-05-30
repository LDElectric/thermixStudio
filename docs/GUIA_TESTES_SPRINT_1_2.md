# Guia de Testes - Implementações Sprint 1/2

**Data:** 19/05/2026  
**Objetivo:** Validar as 6 tarefas implementadas no Thermix Studio

---

## Pré-requisitos

- Build do projeto bem-sucedido (sem erros de compilação)
- Mínimo 3 arquivos FLIR para teste (.jpg ou .tiff)
- 1 imagem visível pareada (ex: arquivo _visivel ou _visible)
- Monitor com boa calibração de cores (recomendado)

---

## Teste 1: LUT + Novas Paletas ✅

**Objetivo:** Validar que rendering com LUT funciona e novas paletas renderizam corretamente

### Passos:
1. **Abrir Thermix Studio** e carregar um arquivo FLIR
2. **Verificar dropdown de paletas:** Deve mostrar 6 opções
   - [ ] Iron
   - [ ] Rainbow
   - [ ] Grayscale
   - [ ] Hotmetal
   - [ ] Arctic
   - [ ] Thermal
3. **Testar cada paleta:**
   - Selecionar "Hotmetal" → Verifica se transição é Black→Red→Orange→Yellow→White
   - Selecionar "Arctic" → Verifica se é Blue→Cyan→White
   - Selecionar "Thermal" → Verifica se é Blue→Cyan→Yellow→Orange→Red
4. **Comparar visualmente com versão anterior** (se possível)
   - [ ] Cores ligeiramente mais nítidas (LUT vs interpolação por-pixel)

**Resultado esperado:** 6 paletas disponíveis, renderização suave sem artefatos

---

## Teste 2: Performance de Render (LUT Cache) ✅

**Objetivo:** Validar que LUT cache melhora performance

### Setup:
- Carregar imagem 320×240 ou maior

### Passos:
1. **Abrir DevTools ou usar external profiler** (dotTrace, etc.)
2. **Medir tempo de render inicial** ao carregar imagen
3. **Trocar paleta 5 vezes** entre Iron/Rainbow/Grayscale
4. **Observar que troca é instantânea** (< 100ms)
5. **Comparar com versão anterior** (esperado: ~30% faster)

**Resultado esperado:** Troca de paleta é imediata, sem lag visível

---

## Teste 3: Cache de Imagem Visível ✅

**Objetivo:** Validar que cache reduz reamostragem de visível

### Setup:
- Termograma com imagem visível pareada
- Conjunto de 5+ imagens para teste

### Passos:
1. **Modo Thermal** → Display imagem térmica
2. **Alternar para Blending** (deve carregar e redimensionar visível)
3. **Alternar para PiP** (deve usar cache, rápido)
4. **Alternar para Blending novamente** (deve usar cache)
5. **Medir tempos de transição:**
   - Blending (primeira vez): ~150-200ms
   - PiP: ~50-100ms (cache hit)
   - Blending (novamente): ~50-100ms (cache hit)

**Resultado esperado:** Segundas e terceiras execuções notavelmente mais rápidas

---

## Teste 4: Reamostragem de Alta Qualidade ✅

**Objetivo:** Validar que reamostragem em alta qualidade melhora visual

### Setup:
- Imagem visível em resolução diferente (ex: 1920×1440 redimensionada para 480×360)

### Passos:
1. **Modo Visible com imagem redimensionada**
2. **Observar qualidade de upsampling/downsampling:**
   - Sem borrão excessivo
   - Texto (se houver) mais nítido que versão anterior
   - Artefatos de aliasing reduzidos
3. **Modo PiP com diferentes scales** (0.3, 0.5, 0.8)
   - [ ] Qualidade consistente em todos os scales

**Resultado esperado:** Reamostragem mais nítida e suave

---

## Teste 5: Blend em Espaço Linear ✅

**Objetivo:** Validar que blend linear reduz aspecto "lavado"

### Setup:
- Termograma com visível pareada
- Blend factor: 0.5 (50/50)

### Passos:
1. **Modo Blending** com `UseLinearBlend = true` (default)
2. **Observar qualidade visual:**
   - [ ] Cores mais vibrantes/naturais
   - [ ] Menos "desbotado" comparado a blend padrão
   - [ ] Contraste melhor preservado
3. **Toggle `UseLinearBlend = false`** (se UI permitir)
   - [ ] Comparação lado-a-lado mostra diferença perceptível
   - [ ] Linear blend (true) é preferível visualmente
4. **Testar com diferentes blend factors:** 0.2, 0.5, 0.8
   - [ ] Transição suave em todos os valores

**Resultado esperado:** Blend visual mais natural, menos desbotado

---

## Teste 6: MSX Adaptativo ✅

**Objetivo:** Validar que MSX adapta-se ao conteúdo térmico

### Setup A - Imagem com alta variância (forno, máquina):
1. **Carregar imagem com pontos quentes** (> ΔT 50°C)
2. **Modo MSX**
3. **Observar:**
   - [ ] MSX overlay forte e detalhado
   - [ ] Bordas térmicas bem marcadas
   - [ ] Sem ruído excessivo

### Setup B - Imagem com baixa variância (parede):
1. **Carregar imagem uniforme** (< ΔT 10°C)
2. **Modo MSX com mesmo MsxStrength**
3. **Observar:**
   - [ ] MSX overlay suave e discreto
   - [ ] Menos "poluição" visual que setup A
   - [ ] Detalhe preservado sem ruído

**Resultado esperado:** MSX mais inteligente, se adapta ao conteúdo

---

## Teste 7: Persistência de Estado ✅

**Objetivo:** Validar que novas paletas e configurações são salvas

### Passos:
1. **Selecionar Hotmetal** como paleta
2. **Ativar UseLinearBlend = true**
3. **Definir MsxStrength = 0.25**
4. **Fechar aplicação** (ProcessingJson é salvo automaticamente)
5. **Reabrir aplicação e selecionar mesmo termograma**
6. **Verificar:**
   - [ ] Paleta é Hotmetal
   - [ ] UseLinearBlend está true
   - [ ] MsxStrength é 0.25

**Resultado esperado:** Estado é persistido corretamente

---

## Teste 8: Compatibilidade Backward ✅

**Objetivo:** Validar que arquivos antigos ainda funcionam

### Setup:
- Termograma processado em versão anterior (com paleta Iron/Rainbow/Grayscale)

### Passos:
1. **Abrir termograma antigo**
2. **Verificar:**
   - [ ] ProcessingJson lê corretamente
   - [ ] Paleta original é respeitada
   - [ ] Nenhum erro de desserialização
   - [ ] Novo dropdown mostra paleta corretamente

**Resultado esperado:** Sem regressões, compatibilidade 100%

---

## Checklist Final

- [ ] Compilação sem erros/warnings
- [ ] 6/6 paletas funcionam corretamente
- [ ] Performance LUT validada (-30%)
- [ ] Cache de visível funciona (modo switching < 100ms)
- [ ] Reamostragem melhora qualidade visual
- [ ] Blend linear menos desbotado
- [ ] MSX adaptativo em dois cenários
- [ ] Estado persiste entre sessões
- [ ] Compatibilidade backward OK
- [ ] Sem artefatos ou bugs visuais

---

## Relatório

**Teste Data:** ________  
**Testador:** ________________  
**Build:** ________________

**Resultado Final:**
- [ ] ✅ PASSOU (pronto para UAT/deployment)
- [ ] ⚠️ PASSOU COM OBSERVAÇÕES (detalhes abaixo)
- [ ] ❌ FALHOU (não pronto, revisar)

**Observações:**

_________________________________________________________________________________

_________________________________________________________________________________

---

**Assinado:** ________________________  
**Data:** ________________
