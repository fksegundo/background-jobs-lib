# Background Jobs Library

> [English version](README.md)

> **Nota:** este repositório é um projeto de estudo — foi construído para
> avaliar workflows de desenvolvimento com agentes de IA (especificação,
> planejamento, oráculo de aceitação antes da implementação). Não é indicado
> para uso em produção; trate como implementação de referência, não como
> dependência para publicar.

Biblioteca de background jobs em .NET + aplicação demo HTTP. Jobs são
persistidos em Postgres, reclamados atomicamente (`SELECT ... FOR UPDATE
SKIP LOCKED`), executados dentro do processo por um pool limitado de workers,
retentados com atraso fixo e recuperados após encerramento abrupto. Os workers
acordam via `LISTEN/NOTIFY` com fallback por temporizador.

## Documentação

Documentação detalhada em [`docs/`](docs/README.pt-BR.md): arquitetura,
modelo de dados e referência completa da API.

## Estrutura

```
src/BackgroundJobs/            biblioteca (motor, storage, registry, client)
src/BackgroundJobs.AspNetCore/ API de gerenciamento + dashboard embutido
demo/BackgroundJobs.Demo/      app demo: 3 tipos de job, endpoints /demo/*
examples/BackgroundJobs.Console  enfileiramento sem HTTP
acceptance/                    oráculo black-box dos 10 cenários
tests/BackgroundJobs.Tests/    testes unitários
docker-compose.yml             postgres + demo
```

## Requisitos

- .NET SDK 11 (`net11.0`)
- Docker + compose para a demo e a aceitação

## Rodar a demo

```bash
docker compose up --build        # demo em http://localhost:18080, painel em /jobs/dashboard
```

Variáveis: `PORT` (8080), `WORKER_COUNT` (4), `DATA_DIR` (./data — diretório
de dados do Postgres, bind-mounted no container), `DEMO_PORT` (porta no host,
18080), `POSTGRES_PORT` (15432, acesso direto ao Postgres pelo host).

Reiniciar com os mesmos dados: `docker compose up` preserva `./data`. Para
zerar, remova `data/` (ou aponte `DATA_DIR` para outro diretório).

## Aceitação

```bash
python acceptance/run.py          # 10 cenários + contrato de erros
```

Controla a demo via docker compose; mata o container com SIGKILL nos cenários
de recuperação e SIGTERM no cenário de parada graciosa.

## API da biblioteca (sem HTTP)

Ver `examples/BackgroundJobs.Console/Program.cs`:

```csharp
var registry = services.AddBackgroundJobs(o =>
{
    o.ConnectionString = "Host=localhost;...";
    o.WorkerCount = 2;
});
registry.AddJob<HelloArgs>("hello", (a, ctx) => { /* trabalho */ });

var jobs = provider.GetRequiredService<IBackgroundJobsClient>();
var id = await jobs.EnqueueAsync("hello", new HelloArgs("world"),
    new JobOptions { MaxAttempts = 3, RetryDelayMs = 500 });
```

## API HTTP

| Endpoint | Contrato |
|---|---|
| `POST /demo/jobs` | `202 {jobId}`; body `{job, params, options?}` |
| `GET /api/jobs` | `200 {items:[{id,name,state,attempts}]}` |
| `GET /api/jobs/{id}` | detalhe completo, inclui `history` e `attemptHistory` |
| `DELETE /api/jobs/{id}` | `200 {id,state:"deleted"}`; 409 fora de scheduled/enqueued |
| `POST /api/jobs/{id}/requeue` | `202 {jobId}`; 409 fora de succeeded/failed/deleted |
| `GET /health` | `200 {status:"ok"}` com o motor no ar, senão 503 |
| `GET /demo/events` | eventos observáveis da demo (em memória) |
| `GET /jobs/dashboard` | dashboard embutido |

Erros: `{"error":"..."}` com 400 entrada inválida, 404 id inexistente,
409 estado incompatível.

## Semânticas importantes

- At-least-once dentro de `maxAttempts`; handlers devem ser idempotentes.
- `kill -9` marca a tentativa aberta como `interrupted` e ela **conta** no
  `maxAttempts` (com `maxAttempts=1` o job termina `failed`).
- Um executor por storage, garantido por advisory lock do Postgres; recovery
  exige que o processo anterior tenha terminado.
- Requeue zera o contador de tentativas e recomeça a numeração em 1,
  preservando todo o histórico anterior (internamente, nova geração).
