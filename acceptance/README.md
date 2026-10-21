# Acceptance oracle

Black-box verification of the background jobs demo against the assignment
(`project-detail.md`). It drives the demo exclusively through `docker compose`
and observes behavior exclusively through the public HTTP API — no database
queries, no internal APIs.

## Run

```bash
python acceptance/run.py            # all scenarios + error contract
python acceptance/run.py --only 5   # subset
python acceptance/run.py --keep     # leave containers up for debugging
```

Requires: Python 3.10+ (stdlib only), Docker with compose v2, and the repo's
`docker-compose.yml` (delivered with the implementation). Exit code `0` = all
pass, `1` = failures, `2` = environment/implementation missing.

## What it assumes about the compose file

- Services `postgres` and `demo`; `demo` publishes `$DEMO_PORT` (default
  `18080`) -> container `8080`.
- Postgres data dir is a bind mount at `$DATA_DIR` (default `./data`) — the
  oracle points it at a fresh temp dir per scenario for isolation, and reuses
  it across kill/restart steps for the persistence scenarios.
- `WORKER_COUNT` is read from the environment (default 4).
- `docker compose kill -s KILL demo` gives the abrupt termination; `stop`
  gives the graceful drain; `up`/`start` reuses the same `DATA_DIR`.

## Coverage

| Scenario | Assertions |
|---|---|
| S1 | immediate run, full state history, one succeeded attempt, events |
| S2 | no start before runAt, start SLA 2s, past runAt = immediate |
| S3 | FIFO message order with 1 worker |
| S4 | peak concurrency 2, no job executed twice (event evidence) |
| S5 | outcomes failed/failed/succeeded, >=500ms between attempts |
| S6 | exhaustion -> failed, sibling job unaffected |
| S7 | deleted job never runs, requeue resets numbering and keeps history, 409s |
| S8 | kill -9 keeps args/history/attempts; pending+scheduled still run |
| S9 | interrupted counts toward maxAttempts (retry when budget, failed at 1) |
| S10 | graceful stop drains active job only; pending runs after restart |
| C0 | 400/404/409 shapes, response bodies, null fields, /health |

Every PASS line prints the observed evidence (states, timestamps, gaps).
