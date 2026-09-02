# Registro histórico do EB005

O EB005 usa decisões persistidas no mesmo store operacional selecionado para as evidências. Não há regras embutidas, extração textual autoritativa nem fallback silencioso: ausência, ambiguidade e indisponibilidade do histórico aparecem no `VerificationResult`.

## Contratos versionados

| Registro | Schema atual | Identidade imutável |
| --- | --- | --- |
| Decisão histórica | `aecs.historical-decision/v1` | `id` + `version` |
| Supressão | `aecs.historical-decision-suppression/v1` | `id` + `version` |
| Evidência EB005 | `aecs.historical-decision-verification/v1` | parte do envelope autenticado da execução |

Uma decisão representa `Adr`, `Incident`, `Decision` ou `Policy` e registra:

- fonte, versão e hash SHA-256 da fonte;
- autoridade responsável e justificativa;
- início e término opcional da validade;
- seletores semânticos proibidos e requeridos;
- escopo por projeto, namespace e tipo de símbolo;
- origem heurística ou manual;
- enforcement `Advisory` ou `Blocking`;
- revisão, ator, razão e instante;
- hash do conteúdo persistido.

As revisões são append-only. A ingestão sempre cria um `Draft`; `history review` cria a versão seguinte como `Approved` ou `Rejected`. Uma regra aprovada com enforcement bloqueante só é válida quando a revisão tem autoridade humana. Confiança da extração, por si só, nunca transforma uma heurística em gate.

## Seletores semânticos

Os padrões não são regex. Eles consultam nós e arestas do `CSharpSymbolGraph` do candidato:

| Seletor | Fato exigido |
| --- | --- |
| `SymbolName` | nome exato de símbolo impactado |
| `TypeName` | nome exato de tipo impactado |
| `NamespacePrefix` | namespace resolvido do símbolo |
| `ConstructsType` | aresta `constructs` para alvo resolvido |
| `ReferencesSymbol` | aresta `references` para alvo resolvido |
| `ImplementsType` | aresta `implements` para alvo resolvido |
| `InheritsType` | aresta `inherits` para alvo resolvido |
| `ProjectReference` | referência resolvida entre projetos |

Texto em comentários, strings ou nomes parecidos não produz conflito. Decisões expiradas, rejeitadas, ainda em draft ou sem símbolos impactados no escopo não são selecionadas.

Se duas decisões ativas exigem e proíbem o mesmo seletor, o resultado é `Ambiguous/Error`; nenhuma delas é escolhida arbitrariamente. Sem decisão relevante, EB005 retorna `NoHistory/Pass`. Falha do store ou evidência legada sem seleção retorna `Unavailable/Error`, que bloqueia quando EB005 é requerido pelo contrato.

## Ingestão e revisão

Um arquivo de ingestão pode usar enums por nome. `review`, `contentHash` e `createdAt` fornecidos são ignorados na ingestão; o registro é persistido como draft no instante da operação.

```json
{
  "id": "ADR-021",
  "version": 1,
  "type": "Adr",
  "source": "docs/adr/ADR-021.md",
  "sourceVersion": "git:4e9b4f0",
  "sourceHash": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "authority": "architecture-board",
  "validFrom": "2026-08-31T00:00:00Z",
  "prohibitedPatterns": [
    { "kind": "ConstructsType", "value": "LegacyHttpClient" }
  ],
  "requiredPatterns": [],
  "justification": "Clientes HTTP devem entrar pelo adapter aprovado.",
  "enforcement": "Advisory",
  "extractedHeuristically": true,
  "scope": {
    "projectPaths": ["src/AECS.Application/AECS.Application.csproj"],
    "namespacePrefixes": [],
    "symbolKinds": ["member"]
  }
}
```

```powershell
aecs history ingest --file ADR-021.json --evidence-store json
aecs history review --id ADR-021 --version 1 `
  --actor architect@example.com --reason "Fonte e seletor revisados" `
  --approve --blocking --evidence-store json
aecs history list --format json --evidence-store json
```

As mesmas operações aceitam `--evidence-store postgres`; a connection string continua vindo exclusivamente de `AECS_POSTGRES_CONNECTION_STRING`.

## Supressões

Supressões também são append-only e sempre registram decisão/versão, ator, razão, criação e expiração. `symbol` e `path` são opcionais; sem ambos, a supressão vale para todos os conflitos daquela versão da decisão.

```powershell
aecs history suppress --id SUP-ADR-021 --version 1 `
  --decision ADR-021 --decision-version 2 `
  --actor platform-owner --reason "Janela de migração" `
  --path src/Legacy/Client.cs --expires 2026-09-30T23:59:59Z
```

Apenas a versão mais recente de cada supressão é considerada e ela precisa estar vigente no instante da seleção. Conflitos suprimidos continuam na evidência com o ID e a versão da supressão, mas não bloqueiam.

## Persistência, evidência e replay

O backend JSON grava decisões e supressões atomicamente em `historical-decisions/`, abaixo do root operacional, com arquivo por identidade/versionamento e hash recalculado na leitura. O PostgreSQL usa as tabelas `historical_decisions` e `historical_decision_suppressions`, chaves compostas, FK, constraints e índices de revisão, fonte e validade.

Antes de assinar uma execução, o store valida a seleção EB005, os hashes das decisões, as supressões e o vínculo de cada conflito com fonte, símbolo e decisão. O replay não consulta o estado histórico atual: ele reaplica a seleção autenticada na execução original e compara a evidência EB005 completa.
