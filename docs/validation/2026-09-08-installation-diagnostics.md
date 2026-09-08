# Instalação e diagnóstico - 2026-09-08

## Resultado

A issue [#97](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/97) introduz o comando
`aecs doctor` e documenta a estratégia de instalação, atualização e desinstalação da CLI piloto.

## Comportamento validado

O diagnóstico:

- resolve a configuração efetiva `aecs.runtime-config/v1` sem exibir segredos;
- verifica SDK .NET 10 estável, Git, repositório Git e permissões dos diretórios de evidência;
- diferencia JSON e PostgreSQL, sem exigir PostgreSQL quando o backend efetivo é JSON;
- verifica Docker e a imagem staged por digest sem executar `docker pull`;
- verifica provider mock, Ollama local ou cloud fallback conforme a configuração efetiva;
- imprime saída humana ou JSON com schema `aecs.doctor/v1`;
- usa códigos estáveis: `ready`, `dependency_absent`, `configuration_invalid` e
  `service_unavailable`;
- limita chamadas externas por timeout e não executa inferência.

## Evidência local

Ambiente: Windows `win-x64`, SDK .NET `10.0.400`, Git `2.55.0.windows.3`.

| Verificação | Resultado |
| --- | --- |
| `dotnet build src/AECS.Cli/AECS.Cli.csproj --configuration Release --nologo` | passou |
| `aecs doctor --repo . --mock --format json` | retornou JSON camelCase e status `service_unavailable` |
| `aecs doctor --repo . --mock` | retornou texto humano e status `service_unavailable` |

O status `service_unavailable` foi esperado no host local porque o daemon Docker Desktop não estava
disponível. O diagnóstico preservou os demais checks como `ready` e não tentou instalar Docker,
baixar imagem, baixar modelo ou chamar provider pago.

## Limites

A validação de Docker disponível, imagem já presente por digest, PostgreSQL e primeira tarefa completa
de demonstração deve ser executada em ambiente com os serviços correspondentes. O comando `doctor`
é diagnóstico de pré-requisitos; ele não substitui os gates da execução `run`.
