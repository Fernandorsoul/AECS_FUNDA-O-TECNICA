# Protocolo pré-registrado do piloto de utilidade

## Objetivo

Medir se o controlador fixo do AECS permite concluir trabalho real em C#/.NET e se uma pessoa
consegue revisar o resultado com confiança operacional. Este piloto não mede benefício adaptativo e
não substitui o corpus causal de 50 tarefas.

## Congelamento antes da coleta

Antes da primeira execução, copie `pilot-utility-manifest.template.json`, preencha todos os campos
marcados como obrigatórios e execute:

```powershell
pwsh pilot\utility-validation\Validate-PilotUtilityProtocol.ps1 `
  -Manifest C:\pilotos\aecs\pilot-utility-manifest.json `
  -RequireFrozen
```

O manifesto congelado deve conter exatamente a amostra planejada. Tarefas malsucedidas permanecem
no denominador; não substitua entradas depois de ver resultados.

## Amostra

- mínimo de 10 tarefas reais distintas;
- repositórios autorizados para uso no piloto;
- origem, licença/autorização, baseline e risco registrados por tarefa;
- escopo C#/.NET representativo, com correção de bug, validação e mudança simples de regra de
  negócio;
- critérios executáveis sem entregar a solução ao agente.

## Runtime e interrupção

Registre previamente:

- provider real (`local` ou `cloud`), modelo e versão quando disponível;
- hardware, sistema operacional, SDK, Docker/image e janela de contexto;
- limite de gasto por tarefa e total do piloto;
- retries, timeout, limite de tokens, limite de arquivos alterados e piso de memória;
- condição de interrupção operacional, incluindo falha repetida de infraestrutura e cancelamento.

Runs com `mock` só podem validar o pacote operacional; eles não contam para utilidade do piloto.

## Métricas

Cada tarefa deve registrar:

- decisão AECS;
- VCC;
- primeira passagem;
- retries;
- tokens de entrada/saída;
- custo estimado e, quando houver, custo reconciliado;
- latência;
- minutos de revisão humana;
- links/IDs/hashes de evidência;
- dificuldades e defeitos encontrados pelo revisor.

## Limiar de aceite

Valores padrão sugeridos, a revisar com o responsável antes de congelar:

- sucesso mínimo: 40% das 10 tarefas com VCC verdadeiro;
- custo máximo estimado por tarefa: US$ 0,20;
- custo máximo reconciliado do piloto: US$ 5,00;
- tempo mediano de revisão humana: até 10 minutos;
- tolerância a falha de infraestrutura: até 20%, desde que preservando evidência parcial.

Ao final, a decisão deve ser `advance`, `fix` ou `expand_sample`, acompanhada de justificativa,
limitações e links para evidências publicáveis.

## Revisão independente

Pelo menos uma pessoa que não implementou o fluxo deve revisar diffs, gates e evidências. A revisão
deve registrar identidade organizacional ou pseudônimo rastreável, vínculo com o projeto, tempo gasto,
dificuldades, defeitos encontrados e decisão por tarefa.
