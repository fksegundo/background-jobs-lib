# Modelo de dados

> [English version](data-model.md)

Todo o estado mora no Postgres sob o schema `background_jobs` — três tabelas,
uma sequência e uma chave de advisory lock. As migrations são recursos SQL
embutidos (`src/BackgroundJobs/Migrations/*.sql`) aplicadas em ordem de nome
no startup do motor; `_migrations` registra as versões aplicadas.

## `background_jobs.jobs`

Uma linha por job. `id` é uuid gerado pela biblioteca e exposto como string
opaca.

| Coluna | Tipo | Notas |
|---|---|---|
| `id` | uuid PK | id opaco devolvido pelo `EnqueueAsync` |
| `name` | text | identificador estável do handler registrado pela aplicação |
| `args` | jsonb | argumentos do handler serializados |
| `state` | text | `scheduled`,`enqueued`,`processing`,`succeeded`,`failed`,`deleted` (CHECK) |
| `attempts` | int | tentativas **iniciadas** desde a criação/último requeue; incrementado dentro da tx de claim |
| `max_attempts` | int | 1..10 (CHECK) |
| `retry_delay_ms` | int | 0..60000 (CHECK) |
| `generation` | int | incrementado pelo requeue; escopa a numeração das tentativas |
| `scheduled_at` | timestamptz | instante de elegibilidade; `NULL` fora de `scheduled` |
| `enqueued_at` | timestamptz | timestamp da entrada na fila no período atual |
| `enqueue_seq` | bigint | desempate monotônico da sequência `enqueue_seq` |
| `created_at`/`updated_at` | timestamptz | `clock_timestamp()` do Postgres |

Restrições:

```sql
CHECK (attempts <= max_attempts)
CHECK ((state = 'scheduled') = (scheduled_at IS NOT NULL))
```

Índices:

```sql
jobs_enqueued_order  ON (enqueued_at, enqueue_seq, id) WHERE state='enqueued'
jobs_scheduled_due   ON (scheduled_at, id)             WHERE state='scheduled'
jobs_processing      ON (id)                           WHERE state='processing'
```

Toda leitura quente bate só em índice parcial — jobs finalizados nunca sujam
o índice da fila.

## `background_jobs.attempts`

Uma linha por tentativa que **iniciou**. A linha é inserida dentro da
transação de claim — existe antes do handler ser invocado.

| Coluna | Tipo | Notas |
|---|---|---|
| `id` | bigint identity PK | também a chave de ordem cronológica por job |
| `job_id` | uuid FK → jobs | |
| `generation` | int | geração do job no momento do claim |
| `attempt` | int | 1-based dentro da geração |
| `claim_token` | uuid | único por claim; resolve commits ambíguos |
| `started_at` | timestamptz | `clock_timestamp()` dentro da tx de claim |
| `finished_at` | timestamptz | NULL em execução; instante de detecção para `interrupted` |
| `outcome` | text | NULL em execução; senão `succeeded`,`failed`,`interrupted` |
| `error` | text | NULL salvo falha/interrupção |

Restrições:

```sql
UNIQUE (job_id, generation, attempt)
CHECK ((outcome IS NULL) = (finished_at IS NULL))
-- uma tentativa aberta por job:
CREATE UNIQUE INDEX attempts_one_open ON attempts (job_id) WHERE outcome IS NULL;
```

`attempts_one_open` é a garantia em nível de banco por trás de "um job nunca
roda em dois workers": um segundo claim nem commita.

Semântica dos outcomes:

| outcome | quem grava | significado |
|---|---|---|
| `succeeded` | worker que concluiu | handler retornou sem erro |
| `failed` | worker que concluiu | handler lançou exceção; `error` = mensagem |
| `interrupted` | recovery no startup | processo morreu no meio; `finished_at` é o instante de detecção (a hora real da morte é incognoscível) |

## `background_jobs.history`

Auditoria append-only das transições de estado — incluindo a criação.

| Coluna | Tipo | Notas |
|---|---|---|
| `id` | bigint identity PK | ordem cronológica dentro do job |
| `job_id` | uuid FK → jobs | |
| `state` | text | o estado **em que entrou** |
| `at` | timestamptz | `clock_timestamp()` do Postgres |
| `reason` | text | strings fixas em inglês |

A ordenação é por `id`, não por `at` — duas transições podem empatar em
timestamp, `id` não.

Reasons emitidas pela implementação: `created (immediate)`,
`created (scheduled)`, `became eligible`, `claimed by worker`,
`attempt succeeded`, `retry scheduled`, `attempts exhausted`,
`recovered interrupted attempt`, `interrupted attempt exhausted limit`,
`manually deleted`, `manually requeued`.

## Sequência

`background_jobs.enqueue_seq` — atribuída em toda transição para `enqueued`
(criação imediata, promoção, recovery, requeue). Combinada com `enqueued_at`
define a ordem FIFO no caso de worker único.

## Advisory lock

Chave `0x62676A6F62733031` ("bgjobs01") do `pg_advisory_lock`, numa conexão
dedicada fora do pool. Vale pela vida inteira do motor; liberada
explicitamente no shutdown limpo e pelo próprio Postgres quando a conexão
morre. Um segundo executor falha no startup em vez de competir.

## Canal de notificação

`bgjobs_wakeup` (configurável via `BackgroundJobsOptions.NotifyChannel`). O
payload não carrega dados — `'wakeup'` basta; os ouvintes sempre releem as
tabelas. `pg_notify` é emitido dentro das transações mutadoras, então só
dispara depois do commit.

## Exemplo de ciclo de vida

`fail-n-times{failTimes:2}`, `maxAttempts=3`, `retryDelayMs=500`:

```
jobs.state:   enqueued → processing → scheduled → enqueued → processing
              → scheduled → enqueued → processing → succeeded

attempts:  #1 failed (error='demo failure')
           #2 failed (error='demo failure')
           #3 succeeded

history:   created (immediate) / claimed by worker / retry scheduled /
           became eligible / claimed by worker / retry scheduled /
           became eligible / claimed by worker / attempt succeeded
```

Kill -9 durante a tentativa #3:

```
attempts:  #1 failed, #2 failed, #3 interrupted   (finished_at = detecção)
próximo start: attempts(3) == max_attempts(3) → jobs.state=failed,
               history += interrupted attempt exhausted limit
```
