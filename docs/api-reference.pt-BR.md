# Referência da API

> [English version](api-reference.md)

Tudo em JSON. Timestamps em ISO-8601 UTC (forma `+00:00`). Ids são strings
opacas (formato uuid hoje). Respostas de erro são sempre:

```json
{ "error": "mensagem legível" }
```

| Status | Quando |
|---|---|
| 400 | body/params/options malformados, nome de job desconhecido, opção fora da faixa |
| 404 | id de job inexistente ou mal formado |
| 409 | delete/requeue contra estado incompatível |
| 503 | `/health` enquanto o motor não está rodando |

---

## Criação de jobs (superfície da demo)

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

- `job` — string obrigatória, precisa ser um nome de job registrado.
- `params` — objeto JSON obrigatório; os campos dependem do job (abaixo).
- `options` — objeto opcional; todos os campos opcionais.
  - `runAt` — timestamp ISO-8601. Omitido ou no passado → enfileira já.
  - `maxAttempts` — int 1..10, padrão 3 (inclui a primeira execução).
  - `retryDelayMs` — int 0..60000, padrão 1000.

**Resposta `202`**

```json
{ "jobId": "ef2a482d-4c07-4ea4-9570-0d68f75b0557" }
```

O id só é devolvido depois do job estar commitado no Postgres.

**Params dos jobs da demo** (todos obrigatórios):

| job | params | validação |
|---|---|---|
| `succeed-after` | `{"delayMs": int}` | 0..60000 |
| `fail-n-times` | `{"failTimes": int}` | 0..10 |
| `log-message` | `{"message": string}` | ≤200 chars |

**Erros** — todos `400 {"error": ...}`: JSON malformado, `params` ausente,
`params` não-objeto, campos faltando/inválidos, `job` desconhecido,
`maxAttempts`/`retryDelayMs`/`runAt` fora da faixa ou tipo errado.

---

## API de gerenciamento

### `GET /api/jobs`

Resumo de todos os jobs, ordenados por criação.

**Resposta `200`**

```json
{ "items": [
  { "id": "...", "name": "log-message", "state": "succeeded", "attempts": 1 }
] }
```

### `GET /api/jobs/{id}`

**Resposta `200`**

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

Notas de campo:

- `attempts` — tentativas iniciadas desde a criação ou o último requeue.
- `scheduledAt` — instante de elegibilidade **somente em `state="scheduled"`**;
  `null` nos demais estados.
- `attemptHistory` — cronológico; tentativa em execução tem
  `finishedAt=null`, `outcome=null`.
- `outcome` — `succeeded` | `failed` | `interrupted`.
- leituras de job usam snapshot repeatable-read: estado, histórico e
  tentativas são mutuamente consistentes.

**Erros**: `404` para ids inexistentes ou imparsáveis.

### `DELETE /api/jobs/{id}`

Só jobs `scheduled` e `enqueued` podem ser deletados; a mudança de estado é
atômica com o caminho de claim, então delete confirmado nunca executa.

**Resposta `200`**

```json
{ "id": "...", "state": "deleted" }
```

**Erros**: `404` id inexistente; `409` em qualquer outro estado
(`processing`/`succeeded`/`failed`/`deleted`).

### `POST /api/jobs/{id}/requeue`

Só jobs `succeeded`, `failed` e `deleted` podem ser reenfileirados. Zera
`attempts`, preserva todo histórico e tentativas (nova geração — a numeração
recomeça em 1).

**Resposta `202`**

```json
{ "jobId": "..." }
```

**Erros**: `404` id inexistente; `409` em `scheduled`/`enqueued`/`processing`.

### `GET /health`

- `200 {"status":"ok"}` — motor inicializado, ownership mantido, storage acessível.
- `503 {"status":"unavailable"}` — inicializando, parando, ou ownership perdido.

### `GET /demo/events`

Eventos observáveis em memória escritos pelas funções da demo (não é o
histórico da biblioteca — perdido no restart).

**Resposta `200`**

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

`200 text/html` — dashboard embutido (contadores por estado, lista filtrável,
detalhe completo do job, ações de deletar/reenfileirar, feed de eventos,
polling de 1s). Hospedado sob prefixo reservado; a raiz da aplicação fica livre.

---

## API da biblioteca (sem HTTP)

```csharp
var registry = services.AddBackgroundJobs(o =>
{
    o.ConnectionString = "Host=...;Database=jobs;Username=jobs;Password=jobs";
    o.WorkerCount      = 4;                              // padrão 4
    o.PollFallback     = TimeSpan.FromMilliseconds(500); // fallback do LISTEN
    o.NotifyChannel    = "bgjobs_wakeup";                // canal do LISTEN
});

registry.AddJob<MyArgs>("my-job", async (args, ctx) => { ... });
// ctx.JobId : string      ctx.Attempt : int (1-based)
```

`IBackgroundJobsClient` (injetável):

```csharp
Task<string>            EnqueueAsync(string job, object args, JobOptions? = null, CancellationToken);
Task<JobDetail>         GetAsync(string jobId, CancellationToken);
Task<IReadOnlyList<JobSummary>> ListAsync(CancellationToken);
Task                    DeleteAsync(string jobId, CancellationToken);
Task                    RequeueAsync(string jobId, CancellationToken);
```

`JobOptions` — `RunAt: DateTimeOffset?`, `MaxAttempts = 3`,
`RetryDelayMs = 1000`; `Validate()` lança `JobValidationException`.

Mapeamento exceção → HTTP feito pelo `MapBackgroundJobsApi`:
`JobValidationException`→400, `JobNotFoundException`→404,
`JobConflictException`→409.

`AddJob` com nome duplicado lança `InvalidOperationException` no startup.
Enfileirar nome não registrado lança `JobValidationException`.

Fiação ASP.NET no host:

```csharp
app.MapBackgroundJobsApi();                                   // /api/*, /health
app.MapJobsDashboard(o => o.EventsUrl = "/demo/events");      // /jobs/dashboard
```

## Variáveis de ambiente (demo)

| Var | Padrão | Significado |
|---|---|---|
| `PORT` | `8080` | porta HTTP do container (demo + gerenciamento + dashboard) |
| `WORKER_COUNT` | `4` | tamanho do pool de workers |
| `DATA_DIR` | `./data` | diretório de dados do Postgres (bind mount no compose) |
| `DEMO_PORT` | `18080` | porta do host mapeada para a demo |
| `POSTGRES_PORT` | `15432` | porta do host para acesso direto ao Postgres (exemplos/testes) |
| `ConnectionStrings__Jobs` | — | connection string Npgsql da demo |
