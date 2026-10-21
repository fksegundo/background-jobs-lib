# Background Jobs Library

.NET background-jobs library + demo HTTP app. Jobs are persisted in Postgres,
claimed atomically (`SELECT ... FOR UPDATE SKIP LOCKED`), executed in-process
by a bounded worker pool, retried with a fixed delay, and recovered after an
abrupt kill. Workers wake via `LISTEN/NOTIFY` plus a timed fallback.

## Layout

```
src/BackgroundJobs/            the library (engine, storage, registry, client)
src/BackgroundJobs.AspNetCore/ management API + embedded dashboard
demo/BackgroundJobs.Demo/      demo app: 3 job types, /demo/* endpoints
examples/BackgroundJobs.Console  enqueue without HTTP
acceptance/                    black-box oracle covering the 10 scenarios
tests/BackgroundJobs.Tests/    unit tests
docker-compose.yml             postgres + demo
```

## Requirements

- .NET SDK 11 (`net11.0`)
- Docker + compose for the demo and acceptance runs

## Run the demo

```bash
docker compose up --build        # demo on http://localhost:18080, dashboard at /jobs/dashboard
```

Environment: `PORT` (8080), `WORKER_COUNT` (4), `DATA_DIR` (./data — Postgres
data directory bind-mounted into the postgres container), `DEMO_PORT` (host
port, 18080).

Restart with the same data: `docker compose up` keeps `./data`. Wipe with
`rm -rf data` (or a new `DATA_DIR`).

## Acceptance

```bash
python acceptance/run.py          # all 10 scenarios + error contract
```

Drives the demo through docker compose; kills the demo container with SIGKILL
for the recovery scenarios and SIGTERM for the graceful-stop scenario.

## Library API (no HTTP)

See `examples/BackgroundJobs.Console/Program.cs`:

```csharp
var registry = services.AddBackgroundJobs(o =>
{
    o.ConnectionString = "Host=localhost;...";
    o.WorkerCount = 2;
});
registry.AddJob<HelloArgs>("hello", (a, ctx) => { /* work */ });

var jobs = provider.GetRequiredService<IBackgroundJobsClient>();
var id = await jobs.EnqueueAsync("hello", new HelloArgs("world"),
    new JobOptions { MaxAttempts = 3, RetryDelayMs = 500 });
```

## HTTP API

| Endpoint | Contract |
|---|---|
| `POST /demo/jobs` | `202 {jobId}`; body `{job, params, options?}` |
| `GET /api/jobs` | `200 {items:[{id,name,state,attempts}]}` |
| `GET /api/jobs/{id}` | full detail incl. `history` and `attemptHistory` |
| `DELETE /api/jobs/{id}` | `200 {id,state:"deleted"}`; 409 outside scheduled/enqueued |
| `POST /api/jobs/{id}/requeue` | `202 {jobId}`; 409 outside succeeded/failed/deleted |
| `GET /health` | `200 {status:"ok"}` while the engine is up, else 503 |
| `GET /demo/events` | observable demo events (in-memory) |
| `GET /jobs/dashboard` | embedded dashboard |

Errors: `{"error":"..."}` with 400 invalid input, 404 unknown id, 409 wrong state.

## Semantics worth knowing

- At-least-once within `maxAttempts`; handlers must be idempotent.
- A `kill -9` marks the open attempt `interrupted`; it counts toward
  `maxAttempts` (with `maxAttempts=1` the job ends `failed`).
- One executor process per storage, enforced by a Postgres advisory lock;
  recovery requires the previous process to be gone.
- Requeue resets the attempt counter and starts numbering at 1 again while
  preserving all prior history (internally a new generation).
