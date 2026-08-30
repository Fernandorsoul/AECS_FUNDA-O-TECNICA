# Arquitetura atual do AECS

Este documento descreve o código executável no repositório. A visão de longo prazo, mais ampla, permanece no [Documento de Fundação Técnica](../AECS_Fundacao_Tecnica_v0.1.md).

## Princípio central

O AECS separa duas categorias de responsabilidade:

- **descoberta probabilística:** geração de código, inferência de risco, seleção de contexto e sugestão de padrões;
- **enforcement determinístico:** limites de escopo, orçamento, circuit breaker, build, testes e políticas confirmadas.

Essa separação está registrada no [ADR-001](adr/ADR-001-probabilistic-discovery-deterministic-enforcement.md).

## Fluxo executável

Uma tarefa percorre o seguinte pipeline:

1. `TaskContractParser` converte o YAML em um `TaskContract`.
2. `RiskClassifier` recalcula o risco a partir do objetivo, dos critérios, das restrições e do tamanho do escopo.
3. `ExecutionController` escolhe o modelo local e cria um `ExecutionPlan`.
4. `GitWorkspaceManager` captura uma baseline limpa e cria um worktree Git descartável.
5. Um `IAgentAdapter` executa o pedido por mock, Ollama ou fallback cloud.
6. `FileApplicator` interpreta blocos `FILE:` e grava o conteúdo somente no worktree.
7. `ControlKernel` aplica circuit breaker, escopo e orçamento.
8. Os verificadores executam build, testes, escopo, orçamento e regras EB001–EB005 no candidato isolado.
9. `DecisionEngine` produz `Verified`, `Rejected` ou `HumanReviewRequired`, e a evidência JSON é persistida fora do repositório.
10. Opcionalmente, `CandidatePromotionService` revalida evidência, baseline, hash e aprovação antes de aplicar atomicamente o diff no checkout original.

No modo `experiment`, o pipeline também tenta compilar um pacote de contexto e agrega métricas de todas as tarefas.

## Mapa de componentes

| Projeto | Responsabilidade atual | Principais elementos |
| --- | --- | --- |
| `AECS.Domain` | Modelo independente de infraestrutura | `TaskContract`, `AgentRunResult`, `ExecutionBudget`, estados, decisões e interfaces |
| `AECS.Application` | Casos de uso e regras de controle | parser, classificador de risco, Control Kernel, verificadores, Context Compiler e experimentos |
| `AECS.Infrastructure` | Integrações externas | adaptadores Ollama/cloud/mock, EF Core/PostgreSQL e sandbox Docker |
| `AECS.Cli` | Composição e experiência de terminal | `run`, `experiment`, `jarvis`, `promote` e `export-patch` |
| `AECS.UnitTests` | Cobertura das regras e dos adaptadores | testes xUnit das camadas Domain, Application e Infrastructure |
| `AECS.IntegrationTests` | Testes integrados com Git e projetos reais | staging, baseline, promoção, concorrência, rollback e fixture AgronomoPlus |

A solução segue um monólito modular: as fronteiras são projetos .NET separados, mas a implantação ainda é uma única CLI. Veja o [ADR-002](adr/ADR-002-modular-monolith.md).

## Risco e roteamento

O classificador usa regras determinísticas e palavras-chave:

| Nível | Exemplos atuais | Modelo local selecionado |
| --- | --- | --- |
| R0 | documentação, comentários, formatação e rename | `qwen2.5-coder:7b` |
| R1 | mudanças simples e escopo pequeno | `qwen2.5-coder:7b` |
| R2 | services, repositories, APIs, validação ou escopo amplo | `qwen2.5-coder:14b` |
| R3 | autenticação, pagamento, segredos ou dependência externa | `qwen2.5-coder:14b` |
| R4 | infraestrutura, deploy, CI/CD, produção ou schema de banco | `qwen2.5-coder:14b` |

O valor informado no YAML é uma entrada para a classificação, não a decisão final. Palavras-chave de maior risco prevalecem.

## Runtime de agentes

Todos os runtimes implementam `IAgentAdapter`:

- `MockAgentAdapter`: produz um resultado sintético para testes do pipeline;
- `OllamaAdapter`: chama `http://localhost:11434/api/generate`;
- `CloudAdapter`: chama `<base-url>/chat/completions` com autenticação Bearer;
- `FallbackAdapter`: usa o adaptador local primeiro e chama cloud se o resultado falhar ou não contiver arquivos úteis.

O fallback cloud é montado apenas nos entry points de tarefa única e experimento. O Jarvis cria diretamente um adaptador Ollama ou mock.

O agente deve responder neste protocolo:

````text
FILE: src/Path/To/File.cs
```csharp
// conteúdo completo do arquivo
```
````

`FileApplicator` reconhece esses blocos, cria diretórios quando necessário e escreve o arquivo completo.

## Control Kernel e verificações

O Control Kernel rejeita o resultado quando os metadados retornados excedem limites de custo, tokens, duração, tentativas ou arquivos, ou quando há mudanças relatadas fora do escopo. Essa validação ocorre depois da chamada do agente; os limites ainda não cancelam preventivamente uma inferência em andamento.

O kernel aceita um contador de tentativas, mas os entry points atuais sempre fazem uma única chamada e usam o contador zero. Ainda não existe um loop automático de retry.

Depois do kernel, os verificadores produzem evidências adicionais:

| Verificador | Implementação atual |
| --- | --- |
| Build | executa `dotnet build --no-restore` no repositório-alvo |
| Tests | executa `dotnet test --no-build` no repositório-alvo |
| Scope | compara os caminhos relatados com `allowed` e `forbidden` |
| Budget | confere os limites do contrato |
| EB001 | procura violações arquiteturais configuradas |
| EB002 | procura violações de padrões existentes |
| EB003 | sinaliza possíveis breaking changes |
| EB004 | sinaliza mudanças relacionadas ausentes |
| EB005 | sinaliza conflitos com decisões históricas |

No estado atual, `DecisionEngine` considera obrigatórios apenas Build, Tests, Scope e Budget conforme o perfil do contrato. Quando o kernel permite continuar, EB001–EB005 são coletados como evidência, mas ainda não alteram a decisão final.

Os verificadores de build e testes redirecionam a saída dos subprocessos, mas não consomem o `stdout` atual. Uma saída suficientemente volumosa pode preencher o buffer e interromper o progresso até que essa implementação seja corrigida.

## Decisões

- `Verified`: todos os verificadores obrigatórios passaram e não há aprovação humana configurada;
- `Rejected`: o kernel bloqueou a execução ou algum verificador obrigatório falhou;
- `HumanReviewRequired`: os verificadores obrigatórios passaram, mas `approval.production` é `human`.

A decisão é uma avaliação do pipeline disponível, não uma garantia formal de correção do software.

O sucesso do `AgentRunResult` ainda não participa diretamente da decisão. Se o adaptador falhar sem modificar arquivos e o repositório-alvo já passar em build e testes, o pipeline pode chegar a `Verified`. Esse comportamento é uma lacuna conhecida; consumidores não devem interpretar `Verified` como prova de que o objetivo foi atendido até existir uma verificação explícita de mudança útil e critérios de aceite.

## Context Compiler

`CodebaseIndexer` e `ContextSelector` constroem pacotes compactos com arquivos e símbolos relevantes.

As integrações ainda não são uniformes:

- experimentos procuram código inicialmente em `<repo>/Backend` e limitam o conteúdo a aproximadamente 20 mil caracteres;
- o comando `context` do Jarvis indexa `<repo>/src`;
- o fluxo individual da CLI ainda não injeta um pacote de contexto na solicitação do agente.

## Persistência e evidências

`AecsDbContext` e `EvidenceStore` fornecem a base de persistência em PostgreSQL, coerente com o [ADR-003](adr/ADR-003-postgresql-evidence-store.md). O `docker-compose.yml` da raiz sobe uma instância local do banco.

A execução staged usa `JsonExecutionEvidenceStore` e grava cada resultado fora do repositório-alvo. O identificador exibido por `run` referencia esse documento, que contém contrato, baseline, candidato, comandos, verificações, decisão e tentativas posteriores de exportação ou promoção. O store PostgreSQL permanece como infraestrutura futura e ainda não compõe a CLI.

## Fronteiras de confiança

O agente escreve somente em um worktree descartável e a execução atesta que o checkout original permaneceu inalterado. A fronteira de escrita no repositório-alvo fica no caso de uso de promoção, que exige uma baseline limpa e idêntica, hash confirmado, candidato elegível e aprovação explícita. O patch passa por preflight, aplicação atômica e verificação posterior do hash; falha de auditoria após a aplicação aciona rollback.

O sandbox Docker ainda não envolve o caminho padrão da CLI e chaves de API devem permanecer exclusivamente em `.env` ou no ambiente. A promoção continua sendo uma operação de escrita deliberada: prefira branch dedicada e revise o diff staged antes do commit. Veja [Promoção controlada](controlled-promotion.md).

## Pontos de extensão

- implemente `IAgentAdapter` para adicionar outro runtime;
- implemente `IVerifier` para produzir uma nova evidência;
- evolua `RiskClassifier` e `ExecutionController` para novas políticas de roteamento;
- implemente outro `IExecutionEvidenceStore` para trocar a persistência JSON;
- transforme uma regra semântica confirmada em enforcement determinístico;
- estenda as políticas de promoção sem mover a decisão de elegibilidade para o runtime probabilístico.
