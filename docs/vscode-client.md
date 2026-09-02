# Cliente VS Code e protocolo local

## Objetivo e limite de confiança

O cliente em `clients/vscode` é uma interface fina para o AECS. Ele seleciona um TaskContract,
acompanha a operação, apresenta fatos autenticados e encaminha decisões humanas. Planejamento,
execução, cancelamento efetivo, leitura e validação de evidência, elegibilidade, revisão e
promoção permanecem no backend .NET.

A extensão não tem método de promoção e nunca aplica o diff. `review.submit` somente chama o
serviço de revisão do AECS. Mesmo uma aprovação persistida ainda precisa da confirmação separada
e exata exigida pelo fluxo de promoção controlada.

## Compatibilidade

- VS Code 1.95 ou mais recente, em workspace local e confiável;
- Node.js 20 ou mais recente apenas para compilar e testar a extensão;
- backend AECS publicado para .NET 8 conforme a [matriz de suporte vigente](dotnet-support.md);
- extensão e backend com o protocolo exato `aecs.vscode/v1`;
- mesmos requisitos de Docker, Ollama, PostgreSQL e TaskContract da CLI escolhidos pela
  configuração de runtime.

Workspaces virtuais e não confiáveis são declarados como incompatíveis no manifesto. A extensão
opera sobre a primeira pasta do workspace; suporte multi-root deliberado não faz parte do cliente
mínimo.

## Instalação local

Publique o backend e gere o VSIX:

```powershell
dotnet publish src/AECS.Cli/AECS.Cli.csproj -c Release -o artifacts/aecs-cli

Push-Location clients/vscode
npm ci
npm test
npm run package
Pop-Location
```

Instale `clients/vscode/aecs-vscode-0.1.0.vsix` pelo comando **Extensions: Install from VSIX** e
configure `aecs.backendPath` com o caminho absoluto de `artifacts/aecs-cli/AECS.Cli.exe` (Windows)
ou do executável `AECS.Cli` (Linux/macOS). Também é aceito `AECS.Cli.dll`; nesse caso a extensão o
inicia com `dotnet`.

Configurações sem segredo disponíveis:

| Configuração | Efeito |
| --- | --- |
| `aecs.backendPath` | Executável ou DLL do backend local |
| `aecs.runtimeConfig` | Contrato opcional `aecs.runtime-config/v1` |
| `aecs.agentMode` | Runtime compartilhado ou override `mock` |
| `aecs.allowHostExecution` | Override explícito para desenvolvimento |
| `aecs.evidenceStore` | Runtime compartilhado ou override `json`/`postgres` |
| `aecs.evidenceRoot` | Diretório opcional do store JSON |
| `aecs.keyDirectory` | Diretório do keyring usado somente pelo backend |
| `aecs.refreshIntervalMs` | Polling enquanto a operação não é terminal |

Credenciais do Ollama/cloud/PostgreSQL continuam vindo do runtime e do ambiente do backend. Não
as coloque em `settings.json`.

## Uso

1. Abra uma pasta Git confiável.
2. Abra a visão **AECS** na Activity Bar e execute **AECS: Iniciar execução**.
3. Selecione um `.yaml` ou `.yml` válido como TaskContract.
4. Acompanhe estado e cancelamento na árvore. Quando houver evidência, inspecione diff, gates,
   critérios, budget e links.
5. Para revisar um candidato elegível, informe a referência de política e uma justificativa.
   Aprovar ou rejeitar cria um evento autenticado no backend; não modifica o checkout.

O diff mostrado vem de `CandidatePromotionService.InspectAsync`, incluindo a validação da
evidência e do repositório. O hash enviado na revisão deve continuar idêntico ao exibido; uma visão
obsoleta falha fechada no backend.

## Protocolo `aecs.vscode/v1`

O comando interno `aecs vscode-server` usa JSON Lines em `stdin/stdout`. Logs e diagnósticos usam
`stderr`, para não corromper o framing. Cada mensagem tem no máximo 1 MiB e inclui versão, ID de
correlação e token de sessão:

```json
{"protocol":"aecs.vscode/v1","id":"request-id","method":"initialize","token":"session-secret","parameters":{}}
```

Respostas preservam `protocol` e `id`, e contêm `ok/result` ou `ok/error`. Uma versão diferente é
recusada, sem negociação silenciosa.

| Método | Finalidade | Mutação autorizada |
| --- | --- | --- |
| `initialize` | Confirma versão, repositório, ator e capacidades | Nenhuma |
| `execution.start` | Valida o TaskContract e inicia o pipeline do AECS | Estado operacional e evidência do pipeline |
| `execution.get` | Recupera o vínculo durável por `operationId` | Nenhuma |
| `execution.cancel` | Solicita cancelamento ao pipeline | Estado operacional |
| `execution.inspect` | Consolida diff, gates, critérios, budget e links autenticados | Nenhuma |
| `review.submit` | Registra `Approve` ou `Reject` via serviço do AECS | Evento assinado de revisão |

`execution.start` recebe um `clientRequestId` gerado antes da chamada. Repetir a solicitação com o
mesmo ID e repositório devolve a operação existente, evitando execução duplicada após perda da
resposta.

## Desconexão e reinício

A extensão guarda apenas o `operationId`, o pedido idempotente ainda não confirmado e metadados de
UI no `workspaceState`. O backend grava registros `aecs.vscode-operation/v1` no diretório privado
de armazenamento global da extensão, fora do repositório-alvo.

Ao reabrir o workspace, o cliente inicia outra sessão e consulta o mesmo `operationId`. Se o
processo anterior morreu durante uma operação e não existe mais, o backend marca o registro como
`Interrupted`, preservando TaskContract, IDs e eventual `evidenceId`; ele não converte interrupção
em sucesso. Fechar normalmente a extensão encerra `stdin`, solicita o cancelamento dos trabalhos
ativos e persiste o estado terminal.

O registro operacional facilita retomada de UI, mas não é evidência de confiança. Apenas o store
de evidências autenticadas é usado para inspeção, decisão e revisão.

## Modelo de ameaças

### Ativos protegidos

- token efêmero da sessão local;
- chaves e credenciais do runtime;
- integridade da evidência e do diff;
- checkout original e autorização humana;
- vínculo entre operação, execução, candidato e evidência.

### Controles

- um token aleatório de 256 bits é guardado no `SecretStorage` do VS Code;
- o token é passado somente no ambiente do processo filho e em mensagens pelo pipe; não aparece em
  argumentos, configurações, logs ou registros operacionais;
- o backend compara a autenticação em tempo constante e restringe cada sessão a um repositório;
- o transporte é `stdin/stdout`, sem porta local exposta;
- IDs, estado e hash do diff são revalidados pelo backend; a UI não decide elegibilidade;
- a inspeção carrega evidências pelo store autenticado e respeita seu isolamento por repositório;
- não existe endpoint de aplicação de patch ou promoção;
- `aecs.backendPath` tem escopo de máquina, e workspaces não confiáveis não ativam o cliente.

### Limites residuais

Um processo executado pelo mesmo usuário do sistema operacional pode inspecionar memória,
ambiente ou pipes da extensão e do backend; o protocolo não protege contra comprometimento da
conta local. `SecretStorage` herda as garantias do cofre fornecido pelo VS Code e pelo sistema.
Alterar o binário configurado em `aecs.backendPath` equivale a substituir o backend confiável. O
keyring local continua sujeito aos limites documentados em [Integridade das
evidências](evidence-integrity.md).

O estado operacional não é assinado e pode ser apagado ou adulterado pelo usuário local. Isso pode
prejudicar a retomada visual, mas não autoriza aprovação, não torna um candidato elegível e não
substitui uma evidência autenticada.
