# ADR-020: Matriz de suporte .NET transitória

- Status: Accepted
- Date: 2026-09-02

## Context

O código e a documentação descreviam versões diferentes como se fossem uma única escolha. Todos os
projetos AECS compilam para `net8.0`; `global.json`, CI e sandbox staged usam SDK .NET 9; o E2E
Docker cria uma fixture `net9.0`; já o documento de fundação e os prompts antigos de milestones
declaram “Solution .NET 10”.

Target framework, runtime necessário, SDK controlador e SDK do sandbox têm funções distintas. Sem
uma matriz explícita, uma compilação oportunista com outro SDK pode ser confundida com reprodução
suportada. A decisão também é urgente: conforme a política oficial consultada em 2026-09-02, .NET 8
e .NET 9 encerram suporte em 2026-11-10, enquanto .NET 10 é o LTS ativo.

## Decision

- A matriz operacional autoritativa fica em [Matriz de suporte .NET](../dotnet-support.md).
- Os binários e testes AECS permanecem `net8.0` nesta decisão estritamente documental.
- Build, testes e tooling devem resolver um SDK .NET 9 estável conforme `global.json`; preview ou
  outro major não constitui evidência oficial de reprodução.
- Execução framework-dependent dos binários exige runtime .NET 8 atualizado no host.
- A execução staged padrão mantém a imagem SDK .NET 9 fixada por digest. Contratos alternativos
  continuam responsáveis por declarar uma imagem compatível e igualmente imutável.
- O `net9.0` criado pelo E2E testa o sandbox; ele não altera o TFM do AECS.
- Menções a .NET 10 nos documentos fundacionais e prompts antigos são intenção histórica. Esses
  arquivos recebem avisos de vigência, sem reescrita retroativa do conteúdo original.
- A configuração vigente é transitória e deve migrar integralmente para .NET 10 LTS antes de
  2026-11-10. A migração técnica terá issue e evidência próprias.

## Consequences

- Pré-requisitos, CI e relatórios passam a usar a mesma terminologia e podem ser auditados.
- Validações feitas com SDK 10 preview continuam úteis para diagnóstico, mas não fecham o requisito
  de reprodução com SDK 9.
- O projeto não pode se declarar pronto para produção após o fim do suporte sem concluir a
  migração para .NET 10 e repetir as suites relevantes.
- Este ADR não muda TFM, pacotes, `global.json`, imagens Docker ou comportamento do produto.

## Alternatives rejected

- **Declarar .NET 10 como estado atual:** contradiz os arquivos de projeto, o SDK resolvido e a
  imagem staged.
- **Tratar qualquer SDK mais novo como equivalente:** impede reprodução exata de Roslyn, MSBuild,
  restore e replay.
- **Retargetar o código junto da correção documental:** mistura inventário e decisão com uma
  migração de maior risco que exige bateria própria.
