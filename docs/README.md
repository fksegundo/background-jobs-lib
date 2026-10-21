# Docs

> [Versão em português](README.pt-BR.md)

- **[architecture.md](architecture.md)** — how the engine works: state machine,
  SKIP LOCKED claim protocol, LISTEN/NOTIFY wakeups, concurrency invariant,
  kill -9 recovery, graceful shutdown, ownership and failure modes.
- **[data-model.md](data-model.md)** — Postgres schema (`jobs`, `attempts`,
  `history`), invariants, indexes, sequence, advisory lock, lifecycle example.
- **[api-reference.md](api-reference.md)** — HTTP endpoints and contracts
  (`/demo/jobs`, `/api/jobs`, `/health`, `/demo/events`, dashboard), plus the
  library's C# API surface.

See also: [acceptance/README.md](../acceptance/README.md) for the black-box
oracle covering the 10 acceptance scenarios.
