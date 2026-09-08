# Instalação e diagnóstico do AECS

Documento de controle da issue
[#97](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/97), parte da entrega
[#94](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/94).

## Estratégia de distribuição

O piloto distribui a CLI como artefato versionado produzido por `dotnet publish`. O pacote deve
conter o executável `aecs`, documentação de configuração e o SHA de origem usado na publicação.

Publicação local de referência:

```powershell
dotnet publish src/AECS.Cli/AECS.Cli.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output artifacts/aecs-cli
```

Instalação:

1. instalar o SDK/runtime .NET 10 LTS estável;
2. extrair o pacote versionado da CLI;
3. adicionar o diretório extraído ao `PATH`, ou chamar o executável pelo caminho completo;
4. executar `aecs doctor --repo <repositorio> --mock` antes da primeira tarefa.

Atualização:

1. executar `aecs doctor --format json` e guardar o diagnóstico redigido;
2. substituir o diretório do pacote por uma nova versão;
3. executar novamente `aecs doctor`;
4. se houver regressão, restaurar o diretório anterior do pacote.

Desinstalação:

1. remover o diretório do pacote do `PATH`;
2. remover o diretório extraído da CLI;
3. opcionalmente arquivar ou apagar os diretórios locais de evidência e chaves, depois de confirmar
   que não serão mais necessários para replay, promoção ou auditoria.

## Diagnóstico

O comando `doctor` verifica pré-requisitos sem executar inferência, instalar software, baixar modelos
ou puxar imagens Docker:

```powershell
aecs doctor --repo C:\repos\produto --mock
aecs doctor --repo C:\repos\produto --format json
```

Códigos estáveis:

| Código | Significado |
| --- | --- |
| `ready` | requisito disponível e coerente |
| `dependency_absent` | executável, modelo, imagem ou caminho obrigatório ausente |
| `configuration_invalid` | configuração conflita com a política efetiva |
| `service_unavailable` | serviço instalado, mas indisponível ou sem resposta no timeout |

Checks obrigatórios no perfil padrão:

- SDK .NET 10 estável resolvido por `global.json`;
- Git e repositório Git, quando `--repo` é informado;
- diretórios de evidência e chaves graváveis;
- Docker daemon acessível;
- imagem staged padrão presente pelo digest fixado;
- provider selecionado: mock, Ollama local ou cloud fallback com consentimento explícito.

PostgreSQL só é obrigatório quando o store efetivo é `postgres`. O fluxo local padrão usa JSON e não
exige banco. Cloud fallback permanece desabilitado por padrão; quando habilitado, o diagnóstico exige
credencial configurada e consentimento explícito para enviar contexto do repositório.

## Primeiro caminho até uma evidência

1. Executar `aecs doctor --repo <repositorio> --mock` e corrigir checks obrigatórios que não estejam
   `ready`.
2. Preparar um TaskContract pequeno com escopo restrito e provider mock.
3. Executar `aecs run --repo <repositorio> --task-file <contrato.yaml> --mock`.
4. Registrar `Evidence ID`, `Diff hash` e caminho da evidência.
5. Revisar com `aecs jarvis --repo <repositorio> --mock` ou exportar o patch para revisão externa.

O diagnóstico é pré-voo. Ele reduz surpresas antes de chamar um modelo, mas não substitui build,
testes, gates de aceite, replay ou promoção controlada.
