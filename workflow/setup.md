# Setup da execução

Registro do "harness + estratégia" desta rodada (benchmark §3.3).

## Harness e modelo

- **Harness**: Claude Code (CLI). Versão: registrar no início da sessão (`claude --version`).
- **Modelo**: padrão da sessão (sonnet). Política: escalar para opus se travar — decisão e motivo entram no decisions.md.
- **Permissões**: modo padrão do usuário (auto). Ações externas (push/PR) sempre com confirmação — padrão do participante.
- **Skills/MCPs**: nenhum específico do projeto. Plugin yoda permanece instalado (setup habitual) mas **não é usado deliberadamente** — o SDD formal dele distorceria o workflow natural sendo medido.
- **Regras de projeto**: `solution/CLAUDE.md` (stack fixa, motor próprio, convenções, ordem do workflow).

## Estratégia (a "hipótese" sendo rodada)

**Verifier-first com gates** — adaptação consciente do padrão observado nas sessões anteriores
(análise em `decisions.md`, 07/out):

1. Kickoff rico (enunciado + constraints) → agente produz design em plan mode.
2. **Gate 1**: humano revisa/aprova design (máquina de estados, schema, claim, recovery).
3. Agente escreve o oráculo: `acceptance/`, script black-box dos 10 cenários via HTTP.
4. **Gate 2**: humano revisa fidelidade do oráculo ao enunciado.
5. Implementação delegada por milestones; intervenção ad-hoc quando necessário; gate informal em cada commit/push.
6. **Gate 3**: humano roda a aceitação pessoalmente + code review. Kill -9 real nos cenários 8–10.

A peça nova vs. o padrão habitual: oráculo executável **antes** da implementação.

## Stack

- Rust + tokio + axum; Postgres (`FOR UPDATE SKIP LOCKED` para claim, `LISTEN/NOTIFY` para wakeup).
- Justificativa: "peculiar pro time" + força a máquina de estados/claim/recovery serem decisões de design explícitas, não conveniência de ecossistema. Escolha deliberada de overengineering moderado — registrado como tal.
- Descartado: Valkey como source of truth (durabilidade frágil sob kill -9); KV embedded (fjall/redb) por superfície de erro extra sem ganho pro estudo.

## Registro

- Transcript: `~/.claude/projects/<slug>/*.jsonl` → copiado para `workflow/transcripts/` ao final.
- Tokens/custo: extraídos do transcript (`message.usage`/`cost-state`) no encerramento.
- Tempo humano: marcos de fase em `diary.md` + `time.csv` (fases do benchmark §3.3).
- Decisões: `decisions.md` append-only, no momento da decisão.
- Artefatos intermediários (design, oráculo, reviews): commitados em `solution/` — o git log é evidência.
