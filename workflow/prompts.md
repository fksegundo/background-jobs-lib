# Prompts por fase

Esqueletos dos prompts para cada fase — usar como base, não literal. O transcript registra o que de fato foi dito.

## Fase 1 — Kickoff (abrir sessão no diretório `solution/` ou na raiz do workspace)

```
Quero construir uma biblioteca de background jobs como projeto de um estudo de workflows.
O enunciado completo está em ../project-detail.md — leia com atenção, ele é a fonte da verdade
(estados, contratos HTTP, 10 cenários de aceite). As regras fixas estão no CLAUDE.md.

Quero o DESIGN primeiro, não código. Me apresente:
1. A máquina de estados e as transições (incluindo recovery pós kill -9 e o papel de
   'interrupted' no maxAttempts)
2. O schema Postgres (jobs, attempts, history) e o protocolo de claim com SKIP LOCKED
3. Como scheduled/retry viram elegíveis e como os workers acordam (LISTEN/NOTIFY vs fallback de poll)
4. Estrutura do workspace (lib vs demo), e como a demo consome a lib
5. Milestones de implementação na ordem

Decisões ambíguas: me pergunte propondo a opção recomendada. Pontos que já vi no enunciado
e quero ver endereçados: DATA_DIR faz sentido com Postgres? a tentativa interrupted conta
no maxAttempts? "nunca mais funções executando que WORKER_COUNT" como invariante.
```

## Gate 1 → Fase 2 (após aprovar o design)

```
Design aprovado. Agora o oráculo: escreva acceptance/ — um script (bash+curl+jq ou python)
que sobe a demo do zero e cobre os 10 cenários de aceite do enunciado + o contrato de erros
(400/404/409, formatos de resposta). Deve rodar com UM comando e imprimir PASS/FAIL por cenário
com evidência (estados observados, tempos, histórico). Ainda não implemente a biblioteca.
```

## Gate 2 → Fase 3 (após revisar o oráculo)

```
Oráculo aprovado. Implemente pelos milestones do design: commit por milestone,
rode a aceitação ao final de cada um e me mostre o resultado. Travou ou achou
ambiguidade no enunciado, pare e me pergunte.
```

## Gate 3 — Revisão (eu executo)

```
# eu mesmo:
./acceptance/run.sh     # do zero, storage limpo
# cenários 8-10: kill -9 real no processo, restart, verificar
# code review do diff
```
