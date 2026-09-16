# AECS — Plano de implementação: Constraint Continuity e Modular Harness Evolution

> **Tipo:** especificação de implementação + roteiro de execução para agente de código.  
> **Data de elaboração:** 2026-09-16.  
> **Status:** proposta; **não** constitui auditoria do `HEAD`, implementação concluída ou relatório de testes.  
> **Objetivo:** preservar requisitos ao longo de sessões e tornar a evolução de componentes do harness mensurável, reversível e sujeita a verificação independente.

## 0. Como utilizar este documento

1. Abra o repositório AECS no agente de código de sua preferência (Codex, Claude Code ou equivalente).
2. Forneça ao agente a seção **1 — Prompt de execução para o agente** junto com este arquivo completo.
3. Exija que ele execute **primeiro a auditoria do repositório**, compare o plano com o código real, apresente um diagnóstico baseado em arquivos/testes e **só depois** implemente mudanças justificadas.
4. O agente deve atualizar a documentação existente (`AGENTS.md`, `architecture.md`/`arquitecture.md` ou `CLAUDE.md`); **apenas se nenhuma documentação elegível existir, criar `AECS.MD`**.
5. Ao finalizar, peça evidências: diff, comandos de teste e respectivos resultados, limitações, documentação modificada, números de benchmark quando houver e pendências.

**Regra de interpretação:** caminhos, nomes de classes e interfaces apresentados aqui são *candidatos de design*, não afirmações de que já existem no repositório. Substitua-os pelos nomes reais identificados na auditoria. Não gere um segundo subsistema para uma capacidade já implementada.

---

## 1. Prompt de execução para o agente de código — copiar e executar no repositório

> **PAPEL:** Você é engenheiro de software responsável por implementar incrementalmente *Constraint Continuity* e *Modular Harness Evolution* no AECS. Antes de modificar arquivos, valide o estado atual do projeto. Não assuma que descrições históricas representam o `HEAD`. Trabalhe com evidência verificável de código, testes, commits e documentação.
>
> ### Ordem obrigatória de trabalho
>
> **A. Descoberta e auditoria (somente leitura no início)**
>
> 1. Confirme a raiz do repositório, branch, commit (`git rev-parse HEAD`) e estado (`git status --short`). Não descarte modificações existentes. Caso a árvore esteja suja, documente os arquivos afetados e não os sobrescreva.
> 2. Localize README, solution/projects .NET, pipelines CI, testes, Docker, exemplos de contratos, persistência e documentação de agentes/arquitetura. Execute o inventário de documentação da seção 2.
> 3. Faça buscas no **código real**, sem deduzir existência pelos nomes deste documento: contratos de tarefa e parser, contexto e Roslyn, snapshot do repositório, grafo de símbolos, execução do agente, adapters/provedores, sandbox, verificadores, decisão, evidência assinada, Postgres, experimentos, custo/VCC/CPVC e Adaptive Controller.
> 4. Crie a tabela `capacidade | arquivo/símbolo real | evidência/teste | estado (presente/parcial/ausente) | lacuna`. Diferencie implementação ativa, código legado, experimento, mock e execução comprovada com provider real. Verifique se os apontamentos históricos da seção 3 ainda se aplicam.
> 5. Leia os contratos públicos e schemas existentes antes de escolher interfaces, migrações ou nomes. Preserve compatibilidade sempre que possível. Não duplique `TaskContract`, `Context Compiler`, grafos, `DecisionEngine`, `ExecutionEvidence` ou `Experiment Harness` que já estejam funcionais.
> 6. Antes de qualquer alteração, apresente um **plano de arquivos afetados**, dependências, testes e riscos. Se houver lacuna essencial de acesso ou ambiente, registre-a e implemente somente o que for verificável sem fingir resultados.
>
> **B. Baseline reprodutível**
>
> 7. Rode os comandos de restore/build/test e smoke/integração previstos no repositório, quando viáveis. Registre comandos, ambiente, SDK, quantidades/resultados e falhas preexistentes. Verifique explicitamente schema dos reports do experimento, execução real com provider e isolamento Docker, sem assumir que os problemas da auditoria anterior persistem.
> 8. Fixe baseline: commit, conjunto de tarefas, versões de modelo/ferramentas, seeds quando disponíveis, limites de tokens/custo, critérios de sucesso e políticas de acesso. Não use dados de avaliação para evoluir o harness.
>
> **C. Implementação incremental, somente quando faltar**
>
> 9. Implemente/complete um `Constraint Ledger` versionado e associado ao contrato atual. Suporte criação, substituição explícita, revogação autorizada, conflitos, escopo, proveniência, vínculo com commit/snapshot, classificação por verificabilidade e hash do conjunto vigente. Não trate comentários, logs ou arquivos recuperados como instruções privilegiadas.
> 10. Conecte a visão vigente das restrições ao Context Compiler e ao runtime sem depender exclusivamente do histórico conversacional. Verifique que alterações de requisitos invalidam ou atualizam planos/contextos derivados de versões antigas.
> 11. Reaproveite o mecanismo real de verificadores e decisão. Restrições obrigatórias verificáveis devem gerar resultados independentes e bloquear aprovação quando violadas. Restrições não verificáveis devem permanecer `pending/manual_review`, nunca `passed` por inferência. Preserve políticas de segurança de autoridade superior.
> 12. Registre na evidência a revisão/hash das restrições, o snapshot, os verificadores executados e o resultado por restrição. Use persistência já existente; só introduza migração PostgreSQL se demonstrada a necessidade.
> 13. Implemente/complete um **Harness Manifest** versionado e imutável que permita variar um módulo por vez (contexto, observação, loop, ferramentas dentro da política permitida, detecção auxiliar de conclusão). **Não** permita que candidatos modifiquem políticas de segurança, isolamento, critérios obrigatórios de aprovação ou seus próprios resultados de avaliação.
> 14. Integre variantes ao Experiment Harness existente. Rode comparações pareadas com mesmos modelos, tarefas, snapshots, permissões e budgets. Comece por **uma** intervenção isolada no Context Compiler; não ative self-modification automática nesta entrega.
> 15. Faça testes unitários, integração e E2E cabíveis; documente falhas preexistentes versus novas. Não marque tarefas como concluídas sem evidência. Preserve e documente caminhos de rollback/feature flag.
>
> **D. Documentação obrigatória e entrega**
>
> 16. Descubra os arquivos elegíveis de documentação **antes de criar um novo**. Priorize `AGENTS.md` da raiz para instruções gerais dos agentes; use `CLAUDE.md` para instruções específicas do Claude; use `architecture.md` ou `arquitecture.md` para decisões e arquitetura. Respeite variantes de capitalização e os arquivos já presentes, sem renomear só para padronizar.
> 17. **Se existir qualquer arquivo elegível**, atualize o(s) existente(s) pertinente(s), preservando conteúdo, escopo de diretório e convenções. Se houver mais de um, mantenha instruções gerais em `AGENTS.md` e arquitetura nos arquivos arquiteturais já existentes; evite cópia extensa entre eles e utilize links relativos. Não edite um `AGENTS.md` aninhado sem relação com os arquivos alterados. **Se nenhum arquivo elegível existir no repositório**, crie `AECS.MD` na raiz com visão geral, arquitetura efetivamente comprovada, comandos válidos, estados de implementação, políticas, processo de experimentação e links internos. Não crie `AECS.MD` se algum desses arquivos já existir.
> 18. Atualize a documentação **com o que foi implementado de fato**, citando classes/arquivos reais, contratos, opções de configuração, migrações, testes e limitações. Distinga `existente`, `implementado nesta tarefa`, `proposto` e `não verificado`. Não invente métricas, caminhos, resultados ou execução com provider real.
> 19. Apresente relatório final no formato: `commit/branch auditados`, `inventário existente`, `lacunas`, `alterações e arquivos`, `documentação escolhida e motivo`, `comandos/testes/resultados`, `métricas baseline vs variante (ou N/D)`, `riscos/rollback`, `pendências`. Não faça merge/deploy nem altere critérios de segurança sem autorização explícita.
>
> **Critério de conclusão:** mudanças mínimas e testadas; restrições persistem e são verificadas entre turnos; variante de harness reproduzível; documentação existente atualizada ou `AECS.MD` criado apenas na ausência dela. Quando algo não puder ser verificado, declare-o **não verificado**, não **concluído**.

---

## 2. Script de descoberta seguro (somente leitura)

Execute a partir da raiz do repositório. Este script **não escreve nem modifica documentação**; apenas localiza candidatos e mostra o estado do checkout. A atualização da documentação é responsabilidade do agente, após ler e validar o conteúdo. O inventário inclui a grafia solicitada `arquitecture.md` e a usual `architecture.md`.

```bash
#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT"

printf 'ROOT: %s\n' "$ROOT"
printf 'BRANCH: %s\n' "$(git branch --show-current 2>/dev/null || printf 'N/D')"
printf 'HEAD: %s\n' "$(git rev-parse HEAD 2>/dev/null || printf 'N/D')"
printf '\nGIT STATUS:\n'
git status --short 2>/dev/null || true

printf '\nDOCUMENTOS CANDIDATOS (em qualquer nível, excluindo diretórios gerados):\n'
find . \( -name .git -o -name node_modules -o -name bin -o -name obj -o -name .venv \) -prune -o \
  -type f \( -iname 'AGENTS.md' -o -iname 'CLAUDE.md' \
  -o -iname 'architecture.md' -o -iname 'arquitecture.md' \) -print | sort

printf '\nDOCUMENTOS NA RAIZ (preferência para governança geral):\n'
for name in AGENTS.md agents.md CLAUDE.md claude.md architecture.md ARCHITECTURE.md arquitecture.md ARQUITECTURE.md; do
  if [ -f "$name" ]; then printf '%s\n' "$name"; fi
done

printf '\nARQUIVOS ÚTEIS À AUDITORIA:\n'
find . \( -name .git -o -name node_modules -o -name bin -o -name obj -o -name .venv \) -prune -o \
  -type f \( -iname '*.sln' -o -iname '*.slnx' -o -iname '*.csproj' \
  -o -iname 'README.md' -o -iname '*task*contract*' -o -iname '*context*compiler*' \
  -o -iname '*experiment*' \) -print | sort | head -n 150
```

**Regra de decisão documental:**

| Resultado do inventário | Ação do agente |
|---|---|
| `AGENTS.md` na raiz | Atualizar suas instruções gerais; atualizar arquitetura existente separadamente se ela também existir. |
| Apenas `CLAUDE.md` | Atualizar o conteúdo aplicável nele, sem substituir instruções próprias do Claude. |
| Apenas `architecture.md` ou `arquitecture.md` | Atualizar o arquivo existente mantendo grafia/capitalização e incluir instruções operacionais em seção identificada, quando pertinente. |
| Apenas documentos elegíveis em subdiretórios | Atualizar o documento cujo escopo cobre o componente; **não** assumir que rege toda a raiz; se não houver documento geral, explicar a escolha antes de criar outro. |
| Nenhum documento elegível em qualquer nível | Criar `AECS.MD` na raiz. |

Não sobrescreva o conteúdo completo de documentos preexistentes para acrescentar esta iniciativa. Edite seções relevantes ou acrescente uma seção identificável com links para esta especificação, se ela estiver no repositório.

---

## 3. Estado histórico de referência — verificar, não presumir

Uma auditoria anterior reportou o snapshot `c8bfe7dce25ffa805cfb501124c44da829b02c1b` de **2026-09-03**. Esse registro é **histórico**; a auditoria obrigatória da seção 1 deve substituir cada hipótese pelo estado do checkout atual.

| Capacidade relatada em 03/09 | Pergunta a responder no código atual |
|---|---|
| Pipeline TaskContract → execução → verificação → decisão → evidência | Quais são os símbolos efetivos e os pontos de extensão? |
| Snapshot, grafo Roslyn e Context Compiler | Há contexto versionado, critérios de seleção e integração ativa? |
| Evidência assinada/armazenamento PostgreSQL | Onde vincular hash do conjunto de restrições e variante de harness sem duplicar armazenamento? |
| Experiment Harness, variantes e VCC/CPVC | O relatório e o workflow usam o mesmo schema? Há comparação reprodutível? |
| Adaptive Controller em modo shadow/offline | Continua shadow? Já existe mecanismo de promoção? |
| Provider LLM real e DockerSandbox | Existem evidências de execução real e de isolamento no ambiente correto? |

**Importante:** não use números históricos de testes como se fossem resultados do `HEAD`. Não implemente novamente componentes já presentes só para alinhar nomes com papers.

---

## 4. Entrega 1 — Constraint Continuity

### 4.1 Problema e invariantes

Requisitos, proibições, políticas e decisões de arquitetura devem continuar aplicáveis entre turnos até serem **explicitamente substituídos/revogados por autoridade suficiente**. O histórico de conversa não pode ser a única fonte de verdade. Dados não confiáveis do repositório não têm autoridade para mudar requisitos.

Invariantes:

- Cada restrição tem identificador estável, origem confiável, escopo, versão, precedência/autoridade, estado e evidência de alteração.
- O conjunto aplicável em cada execução possui `revision` e hash de conteúdo canônico; a evidência registra ambos e o commit/snapshot.
- `supersede` preserva a trilha histórica e exige vínculo com o requisito substituído; conflitos bloqueantes não são resolvidos silenciosamente pelo LLM.
- Restrições de segurança e critérios obrigatórios não podem ser revogados por instrução de menor autoridade.
- Restrições não automaticamente verificáveis ficam `pending/manual-review`, nunca `passed` por mera afirmação do agente.
- Se a revisão muda no meio de uma execução, planos/contextos dependentes devem ser revalidados, replanejados ou interrompidos, conforme a política existente.

### 4.2 Modelo conceitual (adaptar às estruturas reais)

```csharp
public sealed record ConstraintRecord(
    Guid Id,
    string RequirementKey,
    int Revision,
    string Description,
    string Scope,
    ConstraintKind Kind,
    ConstraintStatus Status,
    ConstraintAuthority Authority,
    ConstraintOrigin Origin,
    Guid? SupersedesId,
    string? VerifierId,
    string RepositorySnapshotId);

public sealed record ConstraintSetRef(
    string Id,
    int Revision,
    string CanonicalSha256);
```

Proposta de estados: `Active`, `Superseded`, `Revoked`, `Conflicted`, `PendingVerification`. Proposta de verificabilidade: `Deterministic`, `Assisted`, `Manual`, `ProcessInvariant`. Estes nomes são sugestões, não contrato obrigatório.

**Não confundir:** `PendingVerification` pode ser um estado do *resultado de avaliação* da restrição, distinto de `Active` (estado de vigência). Se o domínio atual já separar essas dimensões, **preserve a separação**, em vez de misturar estado normativo e resultado de verificação em um enum.

### 4.3 Contratos e integração

1. Associar o `ConstraintSetRef` ao `TaskContract` por composição/versão compatível; não quebrar contratos públicos existentes sem plano de migração.
2. Criar resolvedor determinístico de restrições vigentes com precedência, conflitos e substituições explícitas.
3. Fornecer ao Context Compiler uma visão curta + detalhes sob demanda; orçamento de tokens não elimina obrigações críticas.
4. Revalidar dependências de plano/contexto quando o conjunto de restrições ou o snapshot muda.
5. Reaproveitar os verificadores e a decisão já existentes. Para cada restrição obrigatória: `verifier`, `evidence`, `status`, `reason` e `scope`.
6. Incluir na evidência assinada o hash/revisão, snapshot e resultados por requisito; persistência adicional apenas se justificada.
7. Para proibições de processo (ex.: não acessar rede), capturar sinais **durante** a execução no sandbox/control plane; testes de resultado final são insuficientes para comprovar toda a trajetória.

### 4.4 Casos de teste mínimos

- R1 ativo → novo turno preserva R1 sem depender de texto antigo na janela.
- R2 substitui R1 legitimamente → somente R2 é cobrado, e trilha histórica de R1 permanece.
- Requisito contraditório de mesma autoridade → conflito explícito; não aprovar sem resolução.
- Texto em arquivo/log tenta revogar política → rejeitado como origem não confiável.
- Patch satisfaz testes funcionais, mas viola restrição obrigatória → decisão não aprovada.
- Regra subjetiva sem verificador confiável → `pending/manual-review`, não aprovação automática.
- Hash/revisão do Ledger ou snapshot muda durante execução → revalidação/invalidação correta.
- Duas execuções no mesmo TaskContract com revisões distintas → evidências distinguíveis e reproduzíveis.

---

## 5. Entrega 2 — Modular Harness Evolution, inicialmente **sem autoedição**

### 5.1 Objetivo

Separar os parâmetros e estratégias do harness em módulos experimentais de escopo restrito. O agente que propõe melhorias **não** altera verificador obrigatório, política de segurança, fonte da avaliação ou critérios de promoção. A primeira entrega consiste em **comparar variantes declarativas**, não em um loop autônomo de self-modification.

### 5.2 Módulos candidatos e fronteiras

| Módulo | Configuração inicialmente elegível | Fora de escopo para evolução automática |
|---|---|---|
| Agent Loop | orçamento de tentativas, recuperação, detecção de estagnação | bypass de TaskContract e de aprovação |
| Context Management | ranking do grafo, expansão de símbolos, compactação, limites | acesso a contexto proibido ou testes ocultos |
| Observation Management | seleção/estrutura de logs e tool outputs | adulteração da evidência original |
| Tool Use | escolha entre ferramentas **previamente autorizadas** | concessão de capacidades/permissões |
| Task Completion Detection | sinal auxiliar de término | alteração dos verificadores/gates obrigatórios |

A arquitetura existente pode ter divisões diferentes: mapear essas funções a símbolos reais; **não criar cinco serviços por obrigação de corresponder ao paper**.

### 5.3 Manifesto ilustrativo

```yaml
harness:
  schema_version: aecs.harness/v1
  variant_id: context-graph-ranked-v1
  agent_loop:
    strategy: baseline
  context:
    strategy: graph-ranked
    max_tokens: 12000
  observation:
    strategy: baseline
  tool_use:
    policy_ref: immutable-approved-policy
  completion:
    strategy: required-verifiers
```

Esse YAML é **apenas uma proposta**: use o formato/contrato de variantes já adotado no AECS, se houver. Imutabilize a identidade por conteúdo canônico e registre versão real de modelo, prompts, ferramentas, baseline de política, snapshot e budget.

### 5.4 Protocolo experimental e critérios de promoção

1. Baseline congelada; tarefas de evolução **separadas** das de avaliação final.
2. Escolher **uma variável por comparação inicial** (por exemplo, `context.strategy`), preservando modelo, snapshot, política, verificador e orçamento.
3. Comparar execuções pareadas por tarefa; repetir quando houver aleatoriedade e registrar seeds quando suportadas.
4. Registrar taxa de mudança verificada, continuidade de restrições, custo completo, tokens, duração, regressões, falsos bloqueios e falhas por classe de tarefa.
5. Analisar efeitos da interação Context × Ledger em desenho fatorial `2 × 2`; não atribuir melhoria conjunta a um único componente.
6. Promover somente candidato com evidência independente, sem violação crítica ou acesso proibido, com rollback e aprovação humana explícita.
7. Posteriormente, **se houver evidência suficiente**, permitir propostas de patch do harness em branch/workspace isolado, sujeitas ao mesmo protocolo. Autoaprovação e alteração de verificadores permanecem proibidas.

---

## 6. Métricas e desenho da avaliação

| Indicador | Definição operacional |
|---|---|
| Constraint Retention Rate (CRR) | Restrições vigentes satisfeitas ÷ restrições vigentes **avaliadas**; reportar cobertura de avaliação em separado. |
| Cobertura de verificação | Restrições vigentes avaliadas ÷ restrições vigentes aplicáveis. |
| Violações críticas | Quantidade/taxa de violações de segurança, escopo, autoridade e processo; reportar separadamente. |
| Verified Correct Change Rate | Mudanças aceitas por verificação independente ÷ tentativas elegíveis, com contrato e critérios fixos. |
| CPVC | Custo total efetivo ÷ mudanças verificadas; discriminar inferência, ferramentas, sandbox/CI, verificação, retrabalho e revisão humana quando observáveis. |
| Falsos bloqueios | Mudanças corretas rejeitadas indevidamente pelo Ledger/gates, adjudicadas com revisão independente. |
| Regressões por horizonte | Violações e falhas por turno e por comprimento da sessão, não apenas no último turno. |

**Cuidado:** CRR pode parecer alto se o sistema simplesmente deixar de avaliar restrições difíceis; por isso sua cobertura e os casos `pending` são obrigatórios. Para denominador zero, mostrar `N/D`, nunca 100% ou custo zero fictício. Não confundir aprovação no próprio harness com validade independente; selar a avaliação e a proveniência.

Experimento inicial recomendado (hipótese, não previsão): corpus piloto de aproximadamente 20–30 tarefas de integração e sessões com 5–10 turnos, incluindo requisitos adicionados, revogados, substituídos e contraditórios. O piloto serve para validar o protocolo, **não** para afirmar ganho generalizável. Depois, avaliar em tarefas disjuntas e reportar incerteza.

| Variante | Ledger | Estratégia experimental de harness |
|---|---:|---:|
| A — baseline | Não | Baseline |
| B — Ledger | Sim | Baseline |
| C — harness | Não | Variante previamente definida |
| D — combinado | Sim | Mesma variante de C |

Manter constantes os outros fatores e verificar que a própria introdução de restrições **não muda** as regras da avaliação externa entre A/B/C/D.

---

## 7. Sequência de entregas e critérios de aceite

| Fase | Entrega | Gate de aceite |
|---|---|---|
| P0 | Auditoria atual e baseline real | Commit, status, inventário, testes e limitações documentados; nenhum resultado imaginado. |
| P1 | Constraint Ledger versionado | Testes de vigência, autoridade, substituição, conflito e compatibilidade de contrato. |
| P2 | Integração contexto/verificação/evidência | Restrições obrigatórias bloqueiam violações; revisão/hash em evidência; zero bypass de políticas. |
| P3 | Corpus multi-turn | Casos progressivos e medição por turno com cobertura explícita. |
| P4 | Harness Manifest/variantes | Pelo menos duas variantes reprodutíveis sem mudança dos gates de segurança. |
| P5 | Experimento controlado | Comparação pareada A/B/C/D e custo completo, com limites declarados. |
| P6 opcional | Propostas automáticas de evolução | Somente após P0–P5; isolamento, avaliação independente, promoção humana e rollback. |

**Ordem:** corrigir eventuais falhas atuais de baseline primeiro; Ledger e verificação antes de permitir evolução; experimentos controlados antes de automação. Não implemente P6 apenas porque aparece no plano.

---

## 8. Política de documentação: atualizar antes de criar

O agente deve **abrir e ler** os arquivos encontrados na seção 2. Atualizar documentação conforme o código efetivo, com:

- Arquitetura e responsabilidades *existentes*; extensão do Ledger e suas garantias; fronteiras de autoridade.
- Fluxo de execução, dados persistidos, fonte de verdade e como revalidar o contrato após alteração.
- Harness Manifest/variantes, escopo de evolução permitido e componentes imutáveis.
- Comandos **realmente executados** de build/teste/experimento e pré-requisitos específicos do repositório.
- Critérios de aceite, métricas, riscos, rollback/feature flags e limitações conhecidas.
- Referência relativa a este plano **somente se ele tiver sido adicionado ao repositório**.

Exemplo de seção a inserir/adaptar no documento escolhido:

```markdown
## Constraint Continuity e evolução modular do harness

**Estado atual:** [preencher a partir do código e testes; sem suposições]

- Fonte de verdade das restrições: `[arquivo/símbolo real]`.
- Resolução de vigência, conflitos e autoridade: `[arquivo/símbolo real]`.
- Integração com Context Compiler e plano: `[arquivo/símbolo real]`.
- Verificação independente e evidência: `[arquivo/símbolo real]`.
- Manifesto de variantes e isolamento experimental: `[arquivo/símbolo real]`.
- Comandos reproduzíveis: `[somente comandos executados]`.
- Gates que não podem evoluir automaticamente: `[lista real]`.
- Estado de entrega: implementado / parcial / proposto / não verificado.
```

A seção acima **não é texto para preencher com nomes inventados**; cada campo exige inspeção real. Se nada foi implementado, registrar proposta e lacunas, sem anunciar funcionalidade pronta.

---

## 9. Relatório de encerramento que o agente deve devolver

```text
BASELINE
- Repo/branch/commit:
- Git status inicial e final:
- SDK/ambiente/CI:

INVENTÁRIO REAL
- Presente e reutilizado (arquivo + símbolo + teste):
- Parcial e complementado:
- Ausente e implementado:
- Código legado/mock não utilizado:

ALTERAÇÕES
- Arquivos e motivo:
- Contratos/schemas/migrations/compatibilidade:
- Segurança e rollback:

DOCUMENTAÇÃO
- Arquivos elegíveis encontrados:
- Arquivo(s) atualizados ou AECS.MD criado:
- Justificativa da escolha:

VERIFICAÇÃO
- Comando | resultado | evidência/log:
- Testes preexistentes que falharam:
- Testes novos que passaram/falharam:
- Provider real e DockerSandbox: verificado / não verificado (por quê):

EXPERIMENTO
- Baseline e variante; modelos, tarefas, snapshots e budgets:
- CRR, cobertura, VCCR, CPVC, falsos bloqueios, violações críticas:
- Incerteza, custo e limitações:

PENDÊNCIAS
- Não implementado:
- Bloqueios:
- Próximo incremento mínimo:
```

---

## 10. Referências científicas e limites de extrapolação

- **ModularRSI: Modular and Generalizable Recursive Harness Self-Improvement** (preprint, 14/09/2026): https://arxiv.org/abs/2609.14857 — evolução modular/contrastiva; seus ganhos **não** são previsão de resultado no AECS.
- **MTAC-IFBench: Benchmarking Instruction-Following in Multi-Turn Agentic Coding** (preprint, 14/09/2026): https://arxiv.org/abs/2609.14992 — avaliação de restrições em tarefas multi-turn; sua densidade de requisitos é um *stress test*, não amostra representativa de todos os projetos.

**Natureza do documento:** proposta arquitetural derivada da conversa e de uma auditoria histórica; nenhum arquivo do `HEAD` foi inspecionado ou alterado durante sua elaboração. A verificação do projeto atual é a **primeira atividade obrigatória** do agente executor.
