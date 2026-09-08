# Recuperação de falhas e integridade

Documento de controle da issue
[#101](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/101), parte da entrega
[#94](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/94).

## Matriz de falhas

| Cenário | Resultado esperado | Evidência de teste |
| --- | --- | --- |
| Cancelamento durante agente | Execução termina `Cancelled`, persiste evidência terminal e não aplica resposta parcial no checkout original. | `StagedExecutionPipelineTests.CallerCancellation_DuringAgent_StopsRetriesAndPersistsCancellation` |
| Timeout de subprocesso | Árvore de processo é encerrada, resultado marca `TimedOut` e gate falha fechado. | `StagedExecutionPipelineTests.ProcessTimeout_TerminatesProcessAndReturnsTimedOut` |
| Cancelamento de subprocesso | Árvore de processo é encerrada sem classificar como timeout. | `StagedExecutionPipelineTests.ProcessCancellation_TerminatesProcessTreeAndReturnsCancelled` |
| Falha permanente do provider | Execução termina `Rejected`, sem aplicar resposta do agente. | `StagedExecutionPipelineTests.FailedAgent_IsRejected_AndItsResponseIsNotApplied` |
| Falha transitória do provider | Retries respeitam orçamento e cada tentativa fica persistida. | `StagedExecutionPipelineTests.TransientAgentFailure_RetriesWithinBudget_AndPersistsEveryAttempt` |
| Store de evidência dentro do repo alvo | Execução falha antes do agente, evitando evidência controlada pelo candidato. | `StagedExecutionPipelineTests.EvidencePathInsideTargetRepository_IsRejectedBeforeAgentRuns` |
| Falha ao persistir após promoção | Patch aplicado é revertido para a baseline e não fica sucesso silencioso. | `CandidatePromotionTests.EvidencePersistenceFailure_RollsBackAppliedPatch` |
| Baseline diverge antes da promoção | Promoção é recusada sem modificar repositório. | `CandidatePromotionTests.DivergedBaselineCommit_IsRejected` |
| Checkout original sujo | Execução/promoção falha fechado e preserva o checkout. | `StagedExecutionPipelineTests.DirtyRepository_FailsClosedBeforeAgentRuns`, `CandidatePromotionTests.DirtyOriginalRepository_IsRejectedAndPreserved` |
| Patch parcialmente inválido | Nenhum hunk é aplicado; repositório fica preservado. | `CandidatePromotionTests.MultiFilePatchWithInvalidSecondHunk_AppliesNothing` |
| Evidência adulterada | Leitura, promoção e replay rejeitam antes de confiar no payload. | `AuthenticatedEvidenceStoreTests.CoordinatedDiffHashAndDecisionTampering_IsRejected`, `ExecutionReplayTests.Replay_RejectsTamperedAuthenticatedEvidenceBeforeCreatingAnEvent` |
| Assinatura/chave inválida | Evidência sem chave confiável ou legado não assinado falha fechado. | `AuthenticatedEvidenceStoreTests.UnknownSigningKey_IsRejectedFailClosed`, `AuthenticatedEvidenceStoreTests.UnsignedLegacyEvidence_IsRejectedFailClosed` |
| Cadeia de eventos adulterada | Reordenação, remoção de cauda e aprovação adulterada são rejeitadas. | `AuthenticatedEvidenceStoreTests.PromotionEvents_AreSignedAndOrderTamperingIsRejected`, `AuthenticatedEvidenceStoreTests.PromotionTailDeletion_IsRejectedBySignedChainHead` |
| Docker cleanup normal | Containers e worktrees da execução são removidos após sucesso/falha. | `DockerSandboxE2ETests.RealSandbox_IsolatesFilesystemNetworkResourcesAndAlwaysCleansUp`, `ReproducibleRealWorldE2ETests.VersionedAgronomoPlusScenarios_ProduceExpectedDecisionsAndReport` |

## Limpeza de órfãos

Término normal remove worktrees e containers criados pelo AECS. Após interrupção abrupta do processo,
a limpeza deve ser limitada a recursos identificáveis da própria execução:

- worktrees listados por `git worktree list --porcelain` cujo caminho temporário comece com
  `aecs-staging-` ou `aecs-real-world-e2e-`;
- containers Docker com labels/padrões emitidos pelo sandbox AECS;
- diretórios de artefatos escolhidos explicitamente pelo operador para uma execução ou demonstração.

Não remova diretórios amplos como o repositório, `%TEMP%` inteiro, home do usuário ou volumes Docker
sem vínculo identificável com o AECS.

## Backup e restauração

JSON:

- backup deve copiar o diretório de evidências e o diretório de chaves públicas/privadas juntos;
- restauração sem keyring correspondente pode impedir leitura ou anexos de promoção/replay;
- evidências e chaves dentro do repositório alvo são recusadas para evitar controle pelo candidato.

PostgreSQL:

- backup deve incluir tabelas de evidência, eventos de promoção/replay e keyring configurado fora do
  banco;
- restauração deve preservar a ordem de eventos e constraints únicas;
- indisponibilidade do banco selecionado falha fechado e não cai silenciosamente para JSON.

Limite: assinatura detecta edição, remoção e reordenação simples. Rollback coordenado do banco e do
keyring para um estado antigo exige controles operacionais externos, como backup imutável,
versionamento de artefatos e revisão de logs da plataforma.

## Redaction e pacote de suporte

Configuração efetiva, `doctor`, runtime compartilhado e erros de store devem reportar segredos como
`configured` ou `unavailable`, nunca como valor. Pacotes de suporte para issue/release devem incluir:

- `dotnet --info`;
- saída de `aecs doctor --format json`;
- hashes e caminhos de evidências relevantes;
- logs de CI sem secrets;
- relatório de demonstração ou replay.

Não incluir `.env`, connection strings, chaves privadas, tokens de provider, dumps de processo com
segredos ou diretórios de evidência dentro do repositório alvo.

## Ameaças residuais

- Roslyn/MSBuild avalia projetos do repositório alvo durante análise semântica; usar somente
  repositórios confiáveis ou ambiente isolado.
- `--allow-host-execution` é override de desenvolvimento e reduz isolamento.
- Rede ampla no sandbox, quando concedida explicitamente pelo contrato, amplia superfície de risco.
- Proteção física/lógica do keyring continua responsabilidade operacional do ambiente.
- Docker isola processos staged, mas não é promessa de sandbox absoluta contra host hostil.
