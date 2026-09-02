# Reprodução da stack suportada — 2026-09-02

## Resultado

A matriz transitória documentada em [Suporte .NET](../dotnet-support.md) foi reproduzida com êxito
em uma worktree limpa e destacada no commit
`159a80b3be9ba986fd592ad390d836ea7851fa19`. Restore, build, 450 testes .NET, testes da extensão
VS Code, integrações Docker/PostgreSQL, cenário real reproduzível, guard H1, harness experimental e
smokes da CLI passaram.

Esta execução valida a stack suportada e o provider mock. Ela **não** substitui o experimento H1
com provider real, que continua sendo o próximo marco do roadmap.

Rastreabilidade: [issue #70](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/70).

## Ambiente efetivo

| Componente | Versão/identidade observada |
| --- | --- |
| Host | Windows 11 Pro `10.0.26200`, `win-x64` |
| SDK selecionado | .NET SDK `9.0.317` |
| `global.json` | `9.0.100`, `latestFeature`, previews recusados |
| Runtime do produto | `Microsoft.NETCore.App 8.0.30` |
| Runtime/tooling adicional | ASP.NET Core, .NET e Windows Desktop `9.0.19` |
| Git | `2.55.0.windows.3` |
| Node/npm | Node `24.18.0`; npm `11.16.0` |
| Docker | Engine `29.6.1`, API `1.55`, Linux/amd64 |
| Docker Compose | `v5.3.0` |
| Kernel do daemon | `6.18.33.2-microsoft-standard-WSL2` |
| Sandbox staged | `mcr.microsoft.com/dotnet/sdk:9.0@sha256:f190d2dd9eef2899c91ac323caa0bd2b39334a5400ba93013e5199da39dad940` |
| PostgreSQL | `16.14`, container saudável, bind local `127.0.0.1:55432` |
| Imagem PostgreSQL | `postgres:16-alpine@sha256:57c72fd2a128e416c7fcc499958864df5301e940bca0a56f58fddf30ffc07777` |

O SDK e os runtimes foram instalados em raiz portátil isolada. Assim, a resolução do SDK 9 e a
presença explícita do runtime 8 não dependeram do SDK 10 preview existente no host.

## Matriz executada

| Bloco | Resultado | Observação |
| --- | ---: | --- |
| `dotnet restore AECS.slnx` | passou | SDK `9.0.317` |
| `dotnet build AECS.slnx -c Release --no-restore` | passou | 0 erros; 8 avisos preexistentes |
| Testes unitários | 335/335 | 2 s |
| Integrações sem categorias externas | 104/104 | 3 min 36 s |
| Categoria `PostgreSql` | 7/7 | 6 s, servidor `16.14` |
| Categoria `DockerSandbox` | 2/2 | 1 min 1 s, imagem staged pelo digest fixado |
| Categoria `RealWorldE2E` | 2/2 | 1 min 2 s; relatório externo emitido |
| Extensão VS Code | 2/2 | `npm ci`, compile e testes; 0 vulnerabilidades no audit |
| Guard do benchmark H1 | passou | baseline compila; fonte não rastreada é rejeitada |
| Smoke do experimento | 4/4 | 4 verificadas; 0 falhas/skips; schema v3 |
| `--resume` | passou | continuou com 4 checkpoints, sem alterar seus hashes |
| Smokes da CLI | passaram | usage, evidence vazio, segredo PostgreSQL ausente, VS Code e Jarvis |

Os oito avisos de build não foram introduzidos por esta reprodução: quatro `CS1998` em verificadores
e fixtures e quatro `xUnit1031` em `RejectionFlowTests`. O `npm ci` também informou que scripts de
instalação de `@vscode/vsce-sign` e `keytar` não estavam aprovados; os testes e o audit concluíram
normalmente.

## Correção necessária para a reprodução no Windows

Na primeira passagem, a categoria Docker obteve 1/2 porque o fixture tentava criar um link simbólico
sem o privilégio de symlink do usuário Windows; a falha ocorreu antes da lógica AECS. O fixture passou
a usar uma junction (`mklink /J`) como fallback somente quando a criação do symlink é negada no
Windows. A verificação de produção permaneceu a mesma: symlinks, junctions e demais reparse points
continuam proibidos.

Depois da correção, a categoria Docker passou 2/2 e toda a matriz acima foi repetida na worktree
limpa do commit `159a80b3be9ba986fd592ad390d836ea7851fa19`.

## Evidência RealWorldE2E

O relatório `aecs.real-world-e2e` foi mantido fora do checkout em
`%LOCALAPPDATA%\Temp\aecs-issue-70-realworld`:

| Artefato | SHA-256 |
| --- | --- |
| `report.json` | `e071fdfb73e6ded949b991fdc6e916479e7cce9dd9753bf4945feb38dd90a2f8` |
| evidência `agro-001-valid` | `a5bcd6e8b90d8271872e0012bb7319b87004cc6b6016bd87c9c6324787f09e7e` |
| evidência `agro-003-adversarial-scope` | `6914ae0da9266f261a338d3aa50586e5ccf74436e515393cd53e878375603454` |

O cenário válido terminou `Verified`, alterou os dois arquivos esperados, recarregou a evidência e
preservou o repositório original. O cenário adversarial terminou `Rejected` porque tentou alterar um
arquivo proibido; build e testes do candidato foram corretamente bloqueados após a falha da fronteira
de confiança. Os dois resultados coincidiram com as decisões esperadas.

## Evidência do harness experimental

O dataset `ci-smoke` v1.0.0 (`sha256:f16c78b554d9d08577ab6de352de810bbeee4ea5b36aa1da83593e5393c4a3d9`)
executou duas variantes em duas repetições. As quatro execuções foram verificadas na primeira tentativa,
com taxa de primeira passagem de 100%, dois pares comparáveis e repositório original preservado. Como
o provider era mock, custo efetivo e CPVC ficaram corretamente indisponíveis.

Os outputs foram mantidos fora do checkout em
`%LOCALAPPDATA%\Temp\aecs-issue-70-experiment-output-run2`:

| Artefato | SHA-256 |
| --- | --- |
| `report.json` | `fda6b4df601d50176724169e4940d9e7ddf4abaf8e72a200b3ebff70a12c81db` |
| `results.csv` | `8fc90fd9498fb008c5cdd02791418c79e7c9fe5673fe07bf92f1c067082da294` |
| `comparisons.csv` | `5a70554101ffdfba7960b61dd23acc9693ef1db6e1aec81a80746c4bfae33799` |
| `analysis.csv` | `ee1152d69953c7d1615959b9fca420f94db4ebc1d48b808921075a7a300a401c` |
| `cost-records.csv` | `cb467b1cc80fd84a1300cd96cb58cf6751eb2987c32ab5b3744aec4a5cae8198` |
| `cost-efficiency.csv` | `183a2c76d75b3b76f13aab969b8eb01a2dee1594169520e863658fe0a9cc52a1` |

As quatro evidências autenticadas ficam em
`%LOCALAPPDATA%\Temp\aecs-issue-70-experiment-evidence-run2`; a chave pública correspondente fica
em `%LOCALAPPDATA%\Temp\aecs-issue-70-experiment-keys-run2`. Nenhuma chave privada, credencial ou
artefato de execução foi adicionado ao Git.

## Comandos de referência

Os blocos principais reproduzem diretamente o workflow `ci.yml`:

```powershell
dotnet restore AECS.slnx
dotnet build AECS.slnx --configuration Release --no-restore --nologo
dotnet test AECS.slnx --configuration Release --no-build --nologo `
  --filter "Category!=RealWorldE2E&Category!=PostgreSql&Category!=DockerSandbox"
dotnet test tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj `
  --configuration Release --no-build --nologo --filter "Category=PostgreSql"
dotnet test tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj `
  --configuration Release --no-build --nologo --filter "Category=DockerSandbox"
dotnet test tests/AECS.IntegrationTests/AECS.IntegrationTests.csproj `
  --configuration Release --no-build --nologo --filter "Category=RealWorldE2E"
```

O smoke experimental também executou novamente `aecs experiment` com `--resume`; a contagem
permaneceu em quatro e os hashes dos quatro arquivos em `runs/` não mudaram.

## Conclusão e limite

A issue #70 pode ser encerrada: a stack suportada é reproduzível no ambiente registrado e os gates
locais relevantes passam. O resultado ainda caracteriza o AECS como protótipo experimental, não como
produto pronto para produção. O próximo passo é executar H1 com provider real, publicar custos,
checkpoints e evidências e só depois ampliar o experimento para as 50 tarefas planejadas.
