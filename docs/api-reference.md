# API reference

> [Versão em português](api-reference.pt-BR.md)

All JSON. Timestamps are ISO-8601 UTC (`+00:00` offset form). Ids are opaque
strings (uuid format today). Error responses are always:

```json
{ "error": "human readable message" }
```

| Status | When |
|---|---|
| 400 | malformed body/params/options, unknown job name, invalid option range |
| 404 | unknown or malformed job id |
| 409 | delete/requeue against an incompatible state |
| 503 | `/health` while the engine is not running |

---

## Job creation (demo surface)

### `POST /demo/jobs`

```json
{
  "job": "fail-n-times",
  "params": { "failTimes": 2 },
  "options": {
    "runAt": "2026-10-07T15:00:00.000Z",
    "maxAttempts": 3,
    "retryDelayMs": 1000
  }
}
```

- `job` — required string, must be a registered job name.
- `params` — required JSON object; fields depend on the job (below).
- `options` — optional object; every field optional.
  - `runAt` — ISO-8601 timestamp. Omitted or in the past → enqueue now.
  - `maxAttempts` — int 1..10, default 3 (includes the first run).
  - `retryDelayMs` — int 0..60000, default 1000.

**Response `202`**

```json
{ "jobId": "ef2a482d-4c07-4ea4-9570-0d68f75b0557" }
```

The id is returned only after the job is durably committed to Postgres.

**Demo job params** (all required):

| job | params | validation |
|---|---|---|
| `succeed-after` | `{"delayMs": int}` | 0..60000 |
| `fail-n-times` | `{"failTimes": int}` | 0..10 |
| `log-message` | `{"message": string}` | ≤200 chars |

**Errors** — all return `400 {"error": ...}`: malformed JSON, missing
`params`, `params` not an object, missing/invalid param fields, unknown `job`,
`maxAttempts`/`retryDelayMs`/`runAt` out of range or wrong type.

---

## Management API

### `GET /api/jobs`

Summary of every job, ordered by creation.

**Response `200`**

```json
{ "items": [
  { "id": "...", "name": "log-message", "state": "succeeded", "attempts": 1 }
] }
```

### `GET /api/jobs/{id}`

**Response `200`**

```json
{
  "id": "ef2a482d-4c07-4ea4-9570-0d68f75b0557",
  "name": "fail-n-times",
  "args": { "failTimes": 2 },
  "state": "succeeded",
  "attempts": 3,
  "maxAttempts": 3,
  "retryDelayMs": 500,
  "scheduledAt": null,
  "createdAt": "2026-10-21T21:00:00.0000000+00:00",
  "updatedAt": "2026-10-21T21:00:02.0000000+00:00",
  "history": [
    { "state": "enqueued",   "at": "...", "reason": "created (immediate)" },
    { "state": "processing", "at": "...", "reason": "claimed by worker" },
    { "state": "scheduled",  "at": "...", "reason": "retry scheduled" },
    { "state": "enqueued",   "at": "...", "reason": "became eligible" },
    { "state": "processing", "at": "...", "reason": "claimed by worker" },
    { "state": "succeeded",  "at": "...", "reason": "attempt succeeded" }
  ],
  "attemptHistory": [
    { "attempt": 1, "startedAt": "...", "finishedAt": "...",
      "outcome": "failed", "error": "demo failure" },
    { "attempt": 2, "startedAt": "...", "finishedAt": "...",
      "outcome": "failed", "error": "demo failure" },
    { "attempt": 3, "startedAt": "...", "finishedAt": "...",
      "outcome": "succeeded", "error": null }
  ]
}
```

Field notes:

- `attempts` — attempts started since creation or the last manual requeue.
- `scheduledAt` — the eligibility instant **only while `state="scheduled"`**;
  `null` in every other state.
- `attemptHistory` — chronological; a running attempt has
  `finishedAt=null`, `outcome=null`.
- `outcome` — `succeeded` | `failed` | `interrupted`.
- job reads use a repeatable-read snapshot: state, history and attempts are
  mutually consistent.

**Errors**: `404` for unknown or unparsable ids.

### `DELETE /api/jobs/{id}`

Only `scheduled` and `enqueued` jobs can be deleted; the state change is
atomic with the claim path, so a confirmed delete can never run.

**Response `200`**

```json
{ "id": "...", "state": "deleted" }
```

**Errors**: `404` unknown id; `409` in any other state
(`processing`/`succeeded`/`failed`/`deleted`).

### `POST /api/jobs/{id}/requeue`

Only `succeeded`, `failed` and `deleted` jobs can be requeued. Resets
`attempts`, preserves all history and attempt records (new generation —
attempt numbers restart at 1).

**Response `202`**

```json
{ "jobId": "..." }
```

**Errors**: `404` unknown id; `409` in `scheduled`/`enqueued`/`processing`.

### `GET /health`

- `200 {"status":"ok"}` — engine initialized, ownership held, storage reachable.
- `503 {"status":"unavailable"}` — initializing, stopping, or ownership lost.

### `GET /demo/events`

In-memory observable events written by the demo job functions (not the
library's history — lost on restart).

**Response `200`**

```json
{ "items": [
  { "type": "started",  "jobId": "...", "attempt": 1, "at": "...", "detail": null },
  { "type": "message",  "jobId": "...", "attempt": 1, "at": "...", "detail": { "message": "hi" } },
  { "type": "finished", "jobId": "...", "attempt": 1, "at": "...", "detail": { "outcome": "ok" } }
] }
```

`detail`: `started` → `null`; `finished` → `{outcome:"ok"|"error"}`;
`message` → `{message}`.

### `GET /jobs/dashboard`

`200 text/html` — embedded dashboard (counters per state, filterable job list,
full job detail, delete/requeue actions, live events feed, 1s polling).
Hosted under a reserved prefix; the app root stays free.

---

## Library API (no HTTP)

```csharp
var registry = services.AddBackgroundJobs(o =>
{
    o.ConnectionString = "Host=...;Database=jobs;Username=jobs;Password=jobs";
    o.WorkerCount      = 4;                              // default 4
    o.PollFallback     = TimeSpan.FromMilliseconds(500); // LISTEN fallback
    o.NotifyChannel    = "bgjobs_wakeup";                // LISTEN channel
});

registry.AddJob<MyArgs>("my-job", async (args, ctx) => { ... });
// ctx.JobId : string      ctx.Attempt : int (1-based)
```

`IBackgroundJobsClient` (injectable):

```csharp
Task<string>            EnqueueAsync(string job, object args, JobOptions? = null, CancellationToken);
Task<JobDetail>         GetAsync(string jobId, CancellationToken);
Task<IReadOnlyList<JobSummary>> ListAsync(CancellationToken);
Task                    DeleteAsync(string jobId, CancellationToken);
Task                    RequeueAsync(string jobId, CancellationToken);
```

`JobOptions` — `RunAt: DateTimeOffset?`, `MaxAttempts = 3`,
`RetryDelayMs = 1000`; `Validate()` throws `JobValidationException`.

Exceptions → HTTP mapping done by `MapBackgroundJobsApi`:
`JobValidationException`→400, `JobNotFoundException`→404,
`JobConflictException`→409.

`AddJob` with a duplicate name throws `InvalidOperationException` at startup.
Enqueuing an unregistered name throws `JobValidationException`.

ASP.NET wiring in the host:

```csharp
app.MapBackgroundJobsApi();                                   // /api/*, /health
app.MapJobsDashboard(o => o.EventsUrl = "/demo/events");      // /jobs/dashboard
```

## Environment variables (demo)

| Var | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | container HTTP port (demo + management + dashboard) |
| `WORKER_COUNT` | `4` | worker pool size |
| `DATA_DIR` | `./data` | Postgres data dir (bind mount in compose) |
| `DEMO_PORT` | `18080` | host port mapped to the demo |
| `POSTGRES_PORT` | `15432` | host port for direct Postgres access (examples/tests) |
| `ConnectionStrings__Jobs` | — | demo's Npgsql connection string |
