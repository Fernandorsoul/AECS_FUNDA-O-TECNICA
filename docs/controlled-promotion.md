# Promoção controlada de candidatos

A promoção é uma operação separada da descoberta e da verificação. O AECS nunca usa apenas a resposta do agente para escrever no checkout original: ele valida o envelope assinado da evidência persistida e só aceita um candidato `Verified` ou um `HumanReviewRequired` acompanhado de aprovação humana referenciada.

## Invariantes

Antes de aplicar o patch, `CandidatePromotionService` confirma:

1. schema, hash, assinatura e cadeia de eventos da evidência são válidos para uma chave confiável;
2. o identificador da tarefa, o `AgentRun` e o commit-base do candidato correspondem à evidência;
3. o SHA-256 recalculado do diff corresponde tanto ao hash persistido quanto ao hash informado pelo operador;
4. existe um ator e exatamente uma aprovação explícita na CLI;
5. o caminho solicitado resolve para o mesmo repositório registrado na baseline;
6. `HEAD`, branch e status do Git ainda são exatamente os da baseline limpa;
7. todos os caminhos declarados pelo candidato permanecem dentro do repositório;
8. `git apply --check --index` aceita o patch completo.

Uma decisão `Verified` aceita confirmação do usuário (`--confirm`) ou política referenciada (`--policy`). Uma decisão `HumanReviewRequired` só se torna elegível com `--human-approval <referência>`. Candidatos rejeitados nunca são promovidos.

## Atomicidade e concorrência

A aplicação usa `git apply --index`, que atualiza working tree e index como uma única operação. Depois da aplicação, o AECS deriva novamente o diff staged e compara seu hash ao candidato verificado. Uma divergência ou falha ao persistir a evidência causa `git reset --hard` para a baseline.

Promoções concorrentes são serializadas por repositório dentro do processo e por um lock exclusivo em seu Git common directory. A segunda operação revalida a baseline e é recusada após a primeira alterar o index. O lock coordena instâncias do AECS; processos externos continuam sendo tratados pelas revalidações fail-closed de `HEAD`, branch e status.

A promoção bem-sucedida não cria commit. As mudanças ficam staged para inspeção, novos testes e commit pelo fluxo normal do repositório.

## Auditoria

Cada tentativa acrescenta um evento assinado e ligado à assinatura anterior, preservando:

- ação e resultado (`Exported`, `Promoted`, `Rejected` ou `Failed`);
- ator, tipo de aprovação, referência e instante da confirmação;
- commit-base, hash do diff e identificador do candidato;
- repositório ou arquivo exportado, mensagem e duração da operação.

A cabeça assinada da cadeia cobre a quantidade de eventos e a assinatura final, detectando edição, reordenação e remoção simples de cauda. A atualização do JSON usa arquivo temporário e substituição atômica. Se o registro de uma promoção já aplicada não puder ser persistido, o repositório é restaurado.

## Exportação sem aplicação

`export-patch` valida a identidade e a integridade do candidato, mas não exige que ele seja elegível para promoção. Isso permite revisão externa inclusive de um candidato rejeitado. O destino deve estar fora do repositório original, não pode sobrescrever um arquivo existente e só se torna visível depois de uma gravação temporária completa. Se a evidência da exportação falhar, o arquivo produzido é removido.

Quando o store não está no caminho padrão, use `--evidence-root <path>` nos dois comandos.

## Recusas esperadas

A operação termina sem aplicar mudanças quando a evidência não existe, sua assinatura/cadeia não é válida, o hash não confere, a decisão não é elegível, falta confirmação, o repositório é outro, a baseline divergiu, o checkout está sujo, há um caminho inseguro ou o patch falha no preflight. Esses resultados também são registrados quando a evidência original autenticada está disponível; uma evidência inválida não pode receber um novo evento confiável.
