# Fluxo guiado no CLI e Jarvis

Documento de controle da issue
[#98](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/98), parte da entrega
[#94](https://github.com/Fernandorsoul/AECS_FUNDA-O-TECNICA/issues/94).

## Sequência recomendada

1. Verifique o ambiente antes da primeira tarefa:

   ```powershell
   aecs doctor --repo C:\repos\produto --mock
   ```

   O diagnóstico não chama modelo, não instala dependências e não baixa imagens. Corrija checks
   obrigatórios que retornem `dependency_absent`, `configuration_invalid` ou `service_unavailable`
   antes de executar um contrato real.

2. Comece por um contrato mínimo validável:

   ```yaml
   schema_version: aecs.task-contract/v1
   task:
     id: TASK-001
     objective: Corrigir tratamento de nome nulo no mapeador de clientes.
     scope:
       allowed:
         - src/Customers/**
     verification:
       build: true
       unit_tests: true
       scope: true
       budget: true
     approval:
       production: human
   ```

   Validação de contrato acontece antes da chamada ao agente. Erros apontam o campo ou problema
   detectado, por exemplo `task.objective is required`, e o Jarvis orienta corrigir o contrato antes
   de tentar novamente.

3. Abra o Jarvis e peça o guia embutido:

   ```powershell
   aecs jarvis --repo C:\repos\produto --mock
   aecs> guide
   aecs> run C:\repos\produto\tasks\TASK-001.yaml
   ```

   `run` mostra fases (`preflight/contract`, execução staged, verificação de baseline, geração de
   candidato, verificação do candidato e decisão), tentativas, budget consumido, gates, critérios de
   aceite, decisão, razão, hash do diff e evidence ID. Um resultado interrompido ou parcial não é
   apresentado como sucesso final.

4. Após uma reinicialização da interface, consulte fatos persistidos:

   ```text
   aecs> status
   aecs> history --task TASK-001
   aecs> explain --evidence <evidence-id>
   aecs> context --evidence <evidence-id>
   ```

   Esses comandos leem o store autenticado e o Evidence Graph. Eles não reconstroem diff, contexto,
   decisão ou budget por inferência da sessão atual.

5. Revise, exporte ou abandone explicitamente:

   ```text
   aecs> review <evidence-id> --policy policy/team-v1
   aecs> export-patch <evidence-id> C:\revisoes\TASK-001.patch
   ```

   `review` exibe baseline, estado do checkout, decisão, motivo, elegibilidade, diff, hash, gates e
   critérios de aceite antes de pedir `approve`, `reject` ou `abandon`. `export-patch` reapresenta a
   mesma revisão autenticada e grava o patch fora do repositório sem aplicar mudanças.

6. Promova somente depois da confirmação literal:

   ```text
   Decision [approve/reject/abandon]: approve
   Justification: Gates e diff revisados contra policy/team-v1
   Type 'PROMOTE <diff-hash>' to promote now: PROMOTE <diff-hash>
   ```

   A aprovação cria apenas um evento assinado de revisão. A promoção ainda recarrega a evidência,
   confere assinatura, baseline, branch, checkout limpo, repositório e hash exato do diff. A
   aprovação humana não concede permissões novas ao agente e não muda a política de sandbox.

## Linguagem de resultado

| Situação | Como comunicar | Próxima ação |
| --- | --- | --- |
| Código rejeitado | Gates determinísticos ou política recusaram o candidato. | Leia `explain`, corrija contrato/código e execute nova tarefa. |
| Infraestrutura falhou | Dependência, store, Docker, Git ou provider falhou fechado. | Rode `doctor`, preserve evidência e corrija o ambiente. |
| Cancelado/interrompido | Operador ou processo interrompeu a execução. | Use `status`/`history`; não trate como sucesso. |
| Revisão humana pendente | Evidência autenticada exige decisão explícita. | Use `review` ou `export-patch`; ausência de resposta não aprova. |

## Compatibilidade com VS Code

O cliente VS Code continua compatível com o protocolo `aecs.vscode/v1`: ele inicia execução,
consulta operação, cancela, inspeciona evidência e registra revisão. Não existe método de promoção
no protocolo, e a extensão nunca aplica patch. A promoção continua restrita ao backend CLI/Jarvis,
com confirmação literal pelo hash e revalidação do `CandidatePromotionService`.
