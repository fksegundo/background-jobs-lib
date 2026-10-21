# Protocolo da rodada

## Fases e gates

| Fase | Atividade | Encerra quando |
|------|-----------|----------------|
| 0 Setup | workspace, CLAUDE.md, workflow/ (feito) | — |
| 1 Entender+Projetar | kickoff → agente em plan mode → design doc | **Gate 1**: aprovo o design |
| 2 Oráculo | agente escreve `acceptance/` (10 cenários + erros, black-box HTTP) | **Gate 2**: aprovo a fidelidade ao enunciado |
| 3 Executar | implementação por milestones, commit por milestone | harness verde local |
| 4 Revisar | eu rodo `acceptance/` + code review + kill -9 real | **Gate 3**: aprovo resultado |
| 5 Fechar | docs de execução, exemplo sem HTTP, completar workflow/ | entregáveis §6 completos |

## O que conta como intervenção (para o registro)

- Correção de rumo ("não é isso", "muda pra X") — contar.
- Gate: aprovação/pedido de ajuste em design, oráculo ou review — contar.
- Resposta a pergunta do agente — contar (é o modelo pedindo ajuda).
- Confirmação de ação externa (push/PR) — não conta como intervenção de direção; registrar como gate operacional.

## Regras do registro

- Anotar decisões **na hora** em `decisions.md` (1 linha basta: decisão + motivo).
- Trocar de fase = 1 linha em `diary.md` com timestamp (`date -Is`).
- Não mudar o jeito de trabalhar por causa do registro — registro serve o processo, não o contrário.

## Definição de pronto

- `acceptance/` verde do zero (storage vazio), incluindo cenários 8–10 com `kill -9` real.
- Entregáveis do enunciado §6: código, instruções reproduzíveis, exemplo sem HTTP, evidências.
- `workflow/` completo: transcript, tokens, tempo por fase, decisions, diary.
