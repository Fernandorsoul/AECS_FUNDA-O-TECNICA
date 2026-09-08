# Feedback e triagem do piloto AECS

Este guia completa o canal de feedback operacional da entrega piloto
[#102](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/102). Use issues do GitHub com
o template `AECS pilot bug report` para defeitos, dúvidas bloqueantes e achados de revisão.

## Responsável e SLA inicial

Durante o piloto, Fernando / owner do produto é o responsável por triagem inicial. A triagem deve
classificar cada entrada em até um dia útil como:

- `release-blocker`: impede publicar ou continuar o piloto com segurança;
- `fix-forward`: defeito real com correção necessária antes da próxima rodada;
- `document-limitation`: comportamento esperado ou limitação aceita que precisa ficar explícita;
- `follow-up`: melhoria fora do escopo do piloto.

Defeitos críticos ou altos em escopo, budget, integridade, promoção, instalação ou perda de dados
bloqueiam o go/no-go até existir PR verde, rollback documentado ou decisão explícita de aceite.

## Dados mínimos

Toda issue de piloto deve registrar:

- versão ou SHA do pacote usado;
- sistema operacional, arquitetura, .NET SDK/runtime, Docker, provider e evidence backend;
- comandos executados e saída observada;
- resultado esperado segundo README, quickstart, release notes ou checklist;
- links para logs de CI, report de demonstração ou IDs/hashes de evidência quando aplicável;
- severidade, área afetada e decisão de triagem.

## Redaction obrigatória

Inclua apenas artefatos redigidos. Não publique:

- `.env`;
- tokens de provider;
- connection strings;
- chaves privadas do evidence keyring;
- dumps de processo com memória;
- diretórios completos de evidência ou repositórios de terceiros não autorizados;
- patches ou logs contendo código proprietário sem autorização.

São geralmente seguros:

- `dotnet --info`;
- saída de `aecs doctor --format json`, desde que confirme que segredos aparecem só como
  `configured` ou `unavailable`;
- manifesto do pacote piloto e `SHA256SUMS.txt`;
- IDs e hashes de evidência;
- trechos mínimos de logs sem segredo;
- relatório da demonstração reproduzível.

## Roteiro de triagem

1. Confirmar se a versão/SHA reportado corresponde ao pacote candidato do piloto.
2. Reproduzir em checkout limpo ou pacote instalado, não em binários Debug locais.
3. Executar `aecs doctor --format json` e anexar a saída redigida.
4. Separar falha de código, infraestrutura, configuração inválida e limitação documentada.
5. Para bug confirmado, abrir ou vincular PR com teste/regressão sempre que possível.
6. Antes do release, revisar todas as issues `bug`/`pilot-feedback` abertas e registrar a decisão no
   checklist de go/no-go.

## Labels recomendadas

- `pilot-feedback`: toda entrada coletada durante o piloto;
- `bug`: comportamento incorreto;
- `release-blocker`: impede go/no-go;
- `security`: possível vazamento, integridade, escopo ou promoção indevida;
- `docs`: lacuna de documentação;
- `external-dependency`: Docker, SDK, provider, CI ou repositório externo.
