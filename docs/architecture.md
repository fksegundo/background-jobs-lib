# Architecture

> [Versão em português](architecture.pt-BR.md)

How the background-jobs library works internally: the engine, the state
machine, the Postgres claim protocol, the wakeup path, recovery and shutdown.

## Components

```
┌──────────────────────────────────────────────────────────────────┐
│ host application (demo / any ASP.NET or Generic Host app)        │
│                                                                  │
│  JobRegistry ── name → typed handler                             │
│  IBackgroundJobsClient ── enqueue/get/list/delete/requeue        │
│  BackgroundJobs.AspNetCore ── /api/jobs, /health, /jobs/dashboard│
│                                                                  │
│  JobEngine (BackgroundService)                                   │
│   ├─ owner connection ── pg_advisory_lock (executor exclusivity) │
│   ├─ Migrations ── embedded SQL, versioned                       │
│   ├─ recovery ── interrupted attempts                            │
│   ├─ NotificationListener ── LISTEN bgjobs_wakeup                │
│   ├─ SchedulerLoop ── promote due scheduled jobs                 │
│   ├─ OwnerWatchdog ── pings owner conn every 2s                  │
│   └─ WorkerLoop × WorkerCount ── claim → invoke → complete       │
│                                                                  │
│  JobStore ── all SQL (Npgsql, no ORM)                            │
└──────────────────────────┬───────────────────────────────────────┘
                           │ NpgsqlDataSource (pooled)
                    ┌──────▼──────┐
                    │  Postgres   │  jobs / attempts / history
                    └─────────────┘
```

Project split:

| Project | Role |
|---|---|
| `src/BackgroundJobs` | engine, storage, registry, client; depends only on Npgsql + `Microsoft.Extensions.*` abstractions. Usable without HTTP. |
| `src/BackgroundJobs.AspNetCore` | management endpoints and the embedded dashboard. Sees internals via `InternalsVisibleTo`. |
| `demo/BackgroundJobs.Demo` | wires the library, registers the three demo jobs, validates `POST /demo/jobs`, serves `/demo/events`. |

## State machine

| From | To | Trigger | History reason |
|---|---|---|---|
| — | `enqueued` | create, no `runAt` or `runAt` in the past | `created (immediate)` |
| — | `scheduled` | create with future `runAt` | `created (scheduled)` |
| `scheduled` | `enqueued` | `scheduled_at <= now` (promotion) | `became eligible` |
| `enqueued` | `processing` | atomic claim by a free worker | `claimed by worker` |
| `processing` | `succeeded` | handler returned | `attempt succeeded` |
| `processing` | `scheduled` | handler threw, budget left | `retry scheduled` |
| `processing` | `failed` | handler threw, budget exhausted | `attempts exhausted` |
| `processing` | `enqueued` | startup recovery, budget left | `recovered interrupted attempt` |
| `processing` | `failed` | startup recovery, no budget | `interrupted attempt exhausted limit` |
| `scheduled`,`enqueued` | `deleted` | manual delete | `manually deleted` |
| `succeeded`,`failed`,`deleted` | `enqueued` | manual requeue | `manually requeued` |

Every transition writes the job row **and** the history row in the same
transaction — there is no committed state change without a history entry.

`interrupted` is an **attempt outcome**, not a job state. Attempts are counted
at claim time (the `attempts` counter is incremented inside the claim
transaction), so a killed attempt already consumed budget: with
`maxAttempts=1`, recovery transitions the job straight to `failed`.

Requeue does not delete history. It bumps `generation` and resets `attempts`
to 0; new attempt rows number from 1 again because the unique key is
`(job_id, generation, attempt)`.

## Startup sequence

`JobEngine.ExecuteAsync` runs strictly in order:

1. `AcquireOwnershipAsync` — unpooled connection + `pg_try_advisory_lock`.
   Connect retries up to ~30s (Postgres may still be initializing);
   lock contention fails fast — only one executor per storage is supported.
2. `Migrations.ApplyAsync` — embedded `*.sql` resources, applied in
   `*.sql` filename order, each guarded by `_migrations` rows.
3. `RecoverInterruptedAsync` — every `processing` job gets its open attempt
   closed as `interrupted`, then `enqueued` or `failed`.
4. `NotificationListener.Start` — `LISTEN` on `bgjobs_wakeup` **before** the
   first reconciliation, so no notify can fall in a gap.
5. `PromoteDueAsync` — overdue `scheduled` jobs become `enqueued`.
6. Scheduler, watchdog and `WorkerCount` workers start;
   `ServerStatus = Running` makes `/health` return `200 ok`.

## Claim protocol

`JobStore.TryClaimAsync` runs one transaction per claim:

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

`SKIP LOCKED` lets workers race without blocking. With one worker the order is
FIFO on `(enqueued_at, enqueue_seq)`; with several, no ordering guarantee.

The row lock is released at commit — **before** the handler runs. Long-running
jobs never hold Postgres locks.

### Ambiguous commit reconciliation

If the claim commit throws (timeout/connection drop), the outcome is unknown:
the attempt row may or may not exist. Re-executing blindly could run the job
twice (another worker may claim it meanwhile). Each claim therefore writes a
unique `claim_token` into the attempt row; on commit failure the worker polls
`SELECT EXISTS(... claim_token=@token AND outcome IS NULL)` up to 3×/300ms and
only invokes the handler if its own token was persisted. If not, it throws and
tries a fresh claim. Worst case of a still-in-flight commit: the job stays
`processing` and is recovered as `interrupted` on the next process start —
correct, just delayed.

## Worker pool and the concurrency invariant

`WorkerCount` (default 4) `WorkerLoop` tasks are created at startup and live
for the process lifetime:

```
while running:
    claim = gate.TryAdmitAsync(store.TryClaimAsync)   // under the gate
    if none: wait on WakeupSignal (≤1s), loop
    invoke handler (awaited to completion)
    persist completion (retried)
    gate.Complete()
```

Because each loop awaits its handler before claiming again, **at most
`WorkerCount` job functions run at once**, and the partial unique index
`attempts_one_open` (one open attempt per job) makes a second claim of the same
job physically impossible. The engine never fire-and-forgets and never reaps a
running attempt by timeout — those are the two ways the invariant could break.

Missing handlers (a job persisted under a name no longer registered) fail the
attempt normally — error `no handler registered for job 'x'` — consuming
budget like any failure. Other jobs are unaffected.

## Eligibility, retry delay and wakeups

`scheduled_at` means "not eligible before". The scheduler
(`SchedulerLoop`) promotes due rows in order, in one transaction:

```
SELECT due ids ORDER BY scheduled_at, id LIMIT 200 FOR UPDATE SKIP LOCKED
per row: UPDATE → enqueued (new enqueued_at + enqueue_seq), history
pg_notify('bgjobs_wakeup', 'wakeup')
```

Promotion loops row-by-row inside the tx so `enqueue_seq` order matches
`scheduled_at` order — deterministic FIFO for equal-timestamp promotions.

On failure with budget left, `CompleteAttemptAsync` closes the attempt and
sets `scheduled_at = finished_at + retryDelayMs` — measured from the attempt's
DB-side end timestamp, so the fixed delay is honored even under load. With
`retryDelayMs=0` the job still passes through `scheduled → enqueued`,
preserving visible transitions in history.

Wakeups are an optimization, never a correctness dependency:

- every mutating tx calls `pg_notify` (fires on commit),
- `NotificationListener` holds a dedicated `LISTEN` connection and pulses the
  in-process `WakeupSignal` (also pulses once after every reconnect),
- `IBackgroundJobsClient.EnqueueAsync`/`RequeueAsync` pulse in-process too
  (all mutations originate in this process — NOTIFY mainly covers external
  writers and listener reconnects),
- fallback polls: scheduler every `PollFallback` (500ms default), idle workers
  wake at most every 1s.

This is why the 2-second start SLA holds without aggressive polling.

## Delete and requeue races

Both operations take `SELECT ... FOR UPDATE` on the job row inside a
transaction:

- **delete vs claim**: whoever takes the row lock wins. If delete commits
  first, the claim's `WHERE state='enqueued'` sees nothing. If the claim won,
  delete sees `processing` → `409`. A job confirmed deleted never runs.
- **requeue vs claim**: requeue only accepts finished states; a running or
  queued job → `409`.

`404` for unknown/malformed ids, `409` with a readable message otherwise.

## Shutdown

SIGTERM → host calls `JobEngine.StopAsync`:

1. `ServerStatus = Stopping` (health flips to 503 immediately),
2. `AdmissionGate.StopAndDrainAsync` publishes `_stopping` under the gate —
   claims observing `stopping=false` are already counted in `_active`; after
   the flag is set no worker admits a new claim,
3. drains until `_active == 0` — handlers are **not** cancelled and there is
   no timeout inside the library (`ShutdownTimeout` is lifted by the host),
4. `CleanupAsync` stops the listener and `pg_advisory_unlock`s the owner
   connection.

The linearization is the semaphore: a claim and the stopping-flag write cannot
interleave, so no job can start after the stop decision. In `docker compose`
the demo gets `stop_grace_period: 120s` before Docker escalates to SIGKILL.

## Ownership loss mid-run

The owner connection is pinged every 2s (`OwnerWatchdogAsync`). If it dies:

1. `ServerStatus = LostOwnership` → `/health` 503,
2. admissions close, workers drain, engine ends,
3. **no automatic re-acquire**: a released lock does not prove the old process
   stopped running functions (network partition). Restart/recovery requires
   external confirmation the previous executor is gone — by design, matching
   the single-executor scope.

## Completion persistence

`PersistCompletionAsync` retries the write 5×(≈200–1000ms backoff) without
cancellation. If persistence truly stays down, the job remains `processing`
with an open attempt; the next startup marks it `interrupted`. A job is never
silently dropped — at-least-once is preserved as long as Postgres data
survives.

## Failure modes and limits

| Situation | Outcome |
|---|---|
| crash before enqueue commit | client gets error; nothing persisted |
| crash after commit, before response | job exists; client saw an error — possible orphan job, by design (persist-then-respond) |
| kill during attempt | attempt → `interrupted` at next start; counts toward `maxAttempts` |
| ambiguous claim commit, rolled back | job stays `enqueued`, next worker claims it |
| ambiguous claim commit, committed late | job sits `processing` until next restart (rare, documented) |
| completion write never lands | same: recovered as `interrupted` |
| LISTEN conn down | fallback polls keep ≤1s latency; listener reconnects |
| owner conn lost (partition) | drain + unhealthy; manual restart required |

## Demo app specifics

- `POST /demo/jobs` validates name, params and options, then calls
  `IBackgroundJobsClient.EnqueueAsync` — no queue logic in the demo.
- Each handler writes observable events into `DemoEventLog` (in-memory, lost
  on restart — allowed by the spec): `started`, `finished{outcome}`,
  `message{message}`.
- `fail-n-times` decides purely from `ctx.Attempt` — no demo-side counters.
- `Track(ctx, work)` is the shared wrapper: started → work → finished ok/error.
- `HostOptions.ShutdownTimeout` is set to `Infinite` so the host never races
  the drain.
- Dashboard: `MapJobsDashboard(o => o.EventsUrl = "/demo/events")` serves the
  embedded `index.html` with the feed URL substituted into `__EVENTS_URL__`.
