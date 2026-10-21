# Arquitetura

> [English version](architecture.md)

Como a biblioteca funciona por dentro: o motor, a máquina de estados, o
protocolo de claim no Postgres, o caminho de wakeup, recovery e parada.

## Componentes

```
┌──────────────────────────────────────────────────────────────────┐
│ aplicação hospedeira (demo / qualquer app ASP.NET ou Generic Host) │
│                                                                  │
│  JobRegistry ── nome → handler tipado                            │
│  IBackgroundJobsClient ── enqueue/get/list/delete/requeue        │
│  BackgroundJobs.AspNetCore ── /api/jobs, /health, /jobs/dashboard│
│                                                                  │
│  JobEngine (BackgroundService)                                   │
│   ├─ conexão de ownership ── pg_advisory_lock (exclusividade)    │
│   ├─ Migrations ── SQL embutido, versionado                      │
│   ├─ recovery ── tentativas interrupted                          │
│   ├─ NotificationListener ── LISTEN bgjobs_wakeup                │
│   ├─ SchedulerLoop ── promove scheduled vencidos                 │
│   ├─ OwnerWatchdog ── ping na conexão de ownership a cada 2s     │
│   └─ WorkerLoop × WorkerCount ── claim → invoca → completa       │
│                                                                  │
│  JobStore ── todo o SQL (Npgsql, sem ORM)                        │
└──────────────────────────┬───────────────────────────────────────┘
                           │ NpgsqlDataSource (pool)
                    ┌──────▼──────┐
                    │  Postgres   │  jobs / attempts / history
                    └─────────────┘
```

Separação de projetos:

| Projeto | Papel |
|---|---|
| `src/BackgroundJobs` | motor, storage, registry, client; depende só de Npgsql + abstrações `Microsoft.Extensions.*`. Usável sem HTTP. |
| `src/BackgroundJobs.AspNetCore` | endpoints de gerenciamento e dashboard embutido. Acessa internals via `InternalsVisibleTo`. |
| `demo/BackgroundJobs.Demo` | conecta a biblioteca, registra os 3 jobs da demo, valida `POST /demo/jobs`, serve `/demo/events`. |

## Máquina de estados

| De | Para | Gatilho | Reason no histórico |
|---|---|---|---|
| — | `enqueued` | criação sem `runAt` ou `runAt` no passado | `created (immediate)` |
| — | `scheduled` | criação com `runAt` futuro | `created (scheduled)` |
| `scheduled` | `enqueued` | `scheduled_at <= now` (promoção) | `became eligible` |
| `enqueued` | `processing` | claim atômico por worker livre | `claimed by worker` |
| `processing` | `succeeded` | handler retornou | `attempt succeeded` |
| `processing` | `scheduled` | handler lançou erro, há orçamento | `retry scheduled` |
| `processing` | `failed` | handler lançou erro, orçamento esgotado | `attempts exhausted` |
| `processing` | `enqueued` | recovery no startup, há orçamento | `recovered interrupted attempt` |
| `processing` | `failed` | recovery no startup, sem orçamento | `interrupted attempt exhausted limit` |
| `scheduled`,`enqueued` | `deleted` | deleção manual | `manually deleted` |
| `succeeded`,`failed`,`deleted` | `enqueued` | reenfileiramento manual | `manually requeued` |

Toda transição grava a linha do job **e** a de histórico na mesma transação —
não existe mudança de estado commitada sem entrada no histórico.

`interrupted` é **outcome de tentativa**, não estado de job. Tentativas são
contadas no claim (o contador `attempts` é incrementado dentro da transação de
claim), então uma tentativa morta por kill já consumiu orçamento: com
`maxAttempts=1`, o recovery leva o job direto a `failed`.

Requeue não apaga histórico. Incrementa `generation` e zera `attempts`; as
novas tentativas numeram de 1 porque a chave única é
`(job_id, generation, attempt)`.

## Sequência de startup

`JobEngine.ExecuteAsync` roda estritamente em ordem:

1. `AcquireOwnershipAsync` — conexão fora do pool + `pg_try_advisory_lock`.
   Conexão tenta até ~30s (Postgres pode estar inicializando); contenção de
   lock falha rápido — só um executor por storage é suportado.
2. `Migrations.ApplyAsync` — recursos `*.sql` embutidos, em ordem de nome,
   registrados em `_migrations`.
3. `RecoverInterruptedAsync` — todo job `processing` tem a tentativa aberta
   fechada como `interrupted`, e vai a `enqueued` ou `failed`.
4. `NotificationListener.Start` — `LISTEN` em `bgjobs_wakeup` **antes** da
   primeira reconciliação, para nenhum notify cair num intervalo cego.
5. `PromoteDueAsync` — `scheduled` vencidos viram `enqueued`.
6. Scheduler, watchdog e `WorkerCount` workers sobem;
   `ServerStatus = Running` faz `/health` responder `200 ok`.

## Protocolo de claim

`JobStore.TryClaimAsync` executa uma transação por claim:

```sql
SELECT id FROM background_jobs.jobs
WHERE state='enqueued' AND attempts < max_attempts
ORDER BY enqueued_at, enqueue_seq, id
LIMIT 1 FOR UPDATE SKIP LOCKED;

UPDATE ... SET state='processing', attempts=attempts+1,
       enqueued_at=NULL, enqueue_seq=NULL, updated_at=clock_timestamp()
WHERE id=@id
RETURNING name, args::text, attempts, generation, max_attempts, retry_delay_ms;

INSERT INTO attempts (job_id, generation, attempt, started_at, claim_token) ...;
INSERT INTO history (job_id, state, reason) VALUES (@id,'processing','claimed by worker');
COMMIT;
```

`SKIP LOCKED` deixa os workers disputarem sem bloquear. Com um worker a ordem
é FIFO em `(enqueued_at, enqueue_seq)`; com vários, sem garantia de ordem.

O row lock é liberado no commit — **antes** do handler rodar. Jobs longos
nunca seguram locks no Postgres.

### Reconciliação de commit ambíguo

Se o commit do claim lançar exceção (timeout/queda de conexão), o resultado é
desconhecido: a tentativa pode ou não ter sido persistida. Executar cegamente
poderia rodar o job duas vezes (outro worker pode clamar no meio-tempo). Cada
claim grava um `claim_token` único na linha da tentativa; em falha de commit o
worker consulta `SELECT EXISTS(... claim_token=@token AND outcome IS NULL)`
até 3×/300ms e só invoca o handler se o próprio token foi persistido. Se não,
lança erro e tenta um claim novo. Pior caso de commit ainda em voo: o job
fica `processing` e é recuperado como `interrupted` no próximo start —
correto, apenas atrasado.

## Pool de workers e o invariante de concorrência

`WorkerCount` (padrão 4) tasks `WorkerLoop` são criadas no startup e vivem por
toda a vida do processo:

```
while rodando:
    claim = gate.TryAdmitAsync(store.TryClaimAsync)   // sob o gate
    se nulo: espera no WakeupSignal (≤1s), repete
    invoca handler (aguardado até o fim)
    persiste conclusão (com retry)
    gate.Complete()
```

Como cada loop aguarda o handler terminar antes de clamar de novo, **no máximo
`WorkerCount` funções rodam ao mesmo tempo**, e o índice único parcial
`attempts_one_open` (uma tentativa aberta por job) torna fisicamente
impossível um segundo claim do mesmo job. O motor nunca dispara
fire-and-forget nem colhe tentativa por timeout — esses são os dois jeitos de
quebrar o invariante.

Handlers ausentes (job persistido com nome que não foi mais registrado) falham
a tentativa normalmente — erro `no handler registered for job 'x'` —
consumindo orçamento como qualquer falha. Os demais jobs seguem intactos.

## Elegibilidade, atraso de retry e wakeups

`scheduled_at` significa "não elegível antes de". O scheduler
(`SchedulerLoop`) promove linhas vencidas em ordem, numa transação:

```
SELECT ids devidos ORDER BY scheduled_at, id LIMIT 200 FOR UPDATE SKIP LOCKED
por linha: UPDATE → enqueued (novo enqueued_at + enqueue_seq), history
pg_notify('bgjobs_wakeup', 'wakeup')
```

A promoção atualiza linha a linha dentro da tx para que a ordem de
`enqueue_seq` siga a de `scheduled_at` — FIFO determinístico para vencimentos
iguais.

Em falha com orçamento, `CompleteAttemptAsync` fecha a tentativa e grava
`scheduled_at = finished_at + retryDelayMs` — medido a partir do fim da
tentativa no relógio do banco, então o atraso fixo é respeitado mesmo sob
carga. Com `retryDelayMs=0` o job ainda passa por `scheduled → enqueued`,
preservando as transições visíveis no histórico.

Wakeups são otimização, nunca dependência de correção:

- toda tx mutadora chama `pg_notify` (dispara no commit),
- `NotificationListener` mantém conexão dedicada em `LISTEN` e pulsa o
  `WakeupSignal` em processo (pulsa também após cada reconexão),
- `EnqueueAsync`/`RequeueAsync` do client pulsam em processo (todas as
  mutações nascem neste processo — NOTIFY cobre principalmente escritores
  externos e reconexões do listener),
- polls de fallback: scheduler a cada `PollFallback` (500ms padrão), workers
  ociosos acordam no máximo a cada 1s.

É por isso que o SLA de início em 2s vale sem polling agressivo.

## Corridas de delete e requeue

As duas operações fazem `SELECT ... FOR UPDATE` na linha do job dentro de uma
transação:

- **delete vs claim**: quem pega o row lock primeiro vence. Se o delete
  commita antes, o `WHERE state='enqueued'` do claim não vê nada. Se o claim
  venceu, o delete enxerga `processing` → `409`. Job deletado com confirmação
  nunca executa.
- **requeue vs claim**: requeue só aceita estados finais; job rodando ou na
  fila → `409`.

`404` para id inexistente/mal formado, `409` com mensagem legível nos demais.

## Parada

SIGTERM → o host chama `JobEngine.StopAsync`:

1. `ServerStatus = Stopping` (`/health` vira 503 na hora),
2. `AdmissionGate.StopAndDrainAsync` publica `_stopping` sob o gate — claims
   que observaram `stopping=false` já estão contados em `_active`; depois da
   flag nenhum worker admite claim novo,
3. drena até `_active == 0` — handlers **não** são cancelados e não há timeout
   dentro da biblioteca (o host abre mão do `ShutdownTimeout`),
4. `CleanupAsync` para o listener e faz `pg_advisory_unlock` na conexão de
   ownership.

A linearização é o semáforo: um claim e a escrita da flag de parada não podem
se intercalar, então nenhum job começa depois da decisão de parada. No
`docker compose` a demo tem `stop_grace_period: 120s` antes do Docker escalar
para SIGKILL.

## Perda de ownership em execução

A conexão de ownership é pingada a cada 2s (`OwnerWatchdogAsync`). Se morrer:

1. `ServerStatus = LostOwnership` → `/health` 503,
2. admissões fecham, workers drenam, o motor encerra,
3. **sem readquisição automática**: um lock liberado não prova que o processo
   antigo parou de executar funções (partição de rede). Restart/recovery exige
   confirmação externa de que o executor anterior morreu — decisão de design
   compatível com o escopo de executor único.

## Persistência de conclusão

`PersistCompletionAsync` tenta a escrita 5× (backoff ~200–1000ms) sem
cancelamento. Se a persistência continuar fora, o job permanece `processing`
com tentativa aberta; o próximo startup a marca `interrupted`. Um job nunca é
descartado silenciosamente — at-least-once vale enquanto os dados do Postgres
sobreviverem.

## Modos de falha e limites

| Situação | Resultado |
|---|---|
| crash antes do commit do enqueue | cliente recebe erro; nada persistido |
| crash depois do commit, antes da resposta | job existe; cliente viu erro — possível job órfão, por design (persiste e depois responde) |
| kill durante tentativa | tentativa vira `interrupted` no próximo start; conta no `maxAttempts` |
| commit de claim ambíguo, rollback | job segue `enqueued`, próximo worker clama |
| commit de claim ambíguo, commit tardio | job fica `processing` até o próximo restart (raro, documentado) |
| escrita de conclusão nunca confirma | idem: recuperado como `interrupted` |
| conexão LISTEN cai | polls de fallback mantêm latência ≤1s; listener reconecta |
| conexão de ownership cai (partição) | drena + unhealthy; restart manual necessário |

## Específicos da demo

- `POST /demo/jobs` valida nome, params e options e chama
  `IBackgroundJobsClient.EnqueueAsync` — nenhuma lógica de fila na demo.
- Cada handler escreve eventos observáveis em `DemoEventLog` (em memória,
  perdidos no restart — permitido pelo enunciado): `started`,
  `finished{outcome}`, `message{message}`.
- `fail-n-times` decide apenas por `ctx.Attempt` — sem contadores na demo.
- `Track(ctx, work)` é o wrapper comum: started → trabalho → finished ok/error.
- `HostOptions.ShutdownTimeout` vai para `Infinite` para o host nunca correr
  com o drain.
- Dashboard: `MapJobsDashboard(o => o.EventsUrl = "/demo/events")` serve o
  `index.html` embutido com a URL do feed substituída em `__EVENTS_URL__`.
