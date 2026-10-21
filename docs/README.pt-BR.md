# Documentação

> [English version](README.md)

- **[architecture.pt-BR.md](architecture.pt-BR.md)** — como o motor funciona:
  máquina de estados, protocolo de claim com SKIP LOCKED, wakeups via
  LISTEN/NOTIFY, invariante de concorrência, recovery pós kill -9, parada
  graciosa, ownership e modos de falha.
- **[data-model.pt-BR.md](data-model.pt-BR.md)** — schema Postgres (`jobs`,
  `attempts`, `history`), invariantes, índices, sequência, advisory lock e
  exemplo de ciclo de vida.
- **[api-reference.pt-BR.md](api-reference.pt-BR.md)** — endpoints HTTP e
  contratos (`/demo/jobs`, `/api/jobs`, `/health`, `/demo/events`, dashboard),
  mais a superfície da API C# da biblioteca.

Ver também: [acceptance/README.md](../acceptance/README.md) para o oráculo
black-box que cobre os 10 cenários de aceite.
