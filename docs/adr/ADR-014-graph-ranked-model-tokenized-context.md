# ADR-014: Compilar contexto pelo grafo e pelo tokenizer do modelo

- Status: Accepted
- Date: 2026-08-31

## Context

O compilador de contexto ranqueava principalmente nomes e termos e estimava tokens como quatro caracteres. Essa aproximação podia subestimar Unicode, não reservava de forma explícita a saída ou o overhead do adaptador e não explicava individualmente por que arquivos elegíveis eram omitidos. Embora o grafo Roslyn já fosse persistido, suas relações ainda não dirigiam a expansão do pacote.

## Decision

Adotar `aecs.context-manifest/v2` e uma estratégia versionada de seleção e orçamento que:

- cria sementes por objetivo, critérios, escopo, caminhos e símbolos;
- expande relações de arquivo derivadas do grafo Roslyn, inclusive referências reversas e partes parciais, com profundidade configurável e proteção contra ciclos;
- ordena candidatos por score, profundidade e caminho ordinal;
- solicita ao adaptador um perfil de contexto por modelo;
- resolve contadores exatos por `ITokenCounter` quando disponíveis e usa bytes UTF-8 como fallback conservador;
- reserva saída e overhead antes de compilar e mede o prompt completo pelo mesmo contador;
- registra decisões, motivos, tokens e hashes original/incluído para todo arquivo elegível;
- autentica o fingerprint do manifesto e seus vínculos com contrato, baseline, snapshot e grafo.

Um orçamento incapaz de acomodar o cabeçalho produz um pacote vazio e auditável. Ele não autoriza exceder a janela do modelo nem transforma uma limitação de contexto em erro não controlado.

## Consequences

O contexto passa a refletir dependências e testes semanticamente próximos, inclusive entre projetos, e pode ser reproduzido e explicado arquivo a arquivo. Contadores específicos de modelos podem melhorar a utilização da janela sem alterar o compilador. Na ausência deles, a contagem por bytes utiliza menos contexto que um tokenizer típico, mas preserva a garantia de não subestimar o pacote.

O manifesto cresce porque também registra omissões. Mudanças futuras na fórmula de score, relações, tokenizer ou serialização exigem nova versão de estratégia/schema para preservar a verificabilidade de evidências anteriores.
