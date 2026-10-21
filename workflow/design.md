---
agent: devin-local
session: cake-barometer
created: 2026-10-21T21:00:13Z
---
# Design da biblioteca de background jobs

Construir um motor próprio em .NET 11 com persistência transacional Postgres, execução limitada por workers e integração HTTP/dashboard separada, validado por um oráculo antes da implementação.

## Fontes, decisões e gates

- Fonte da verdade confirmada pelo usuário: `C:\Users\filon\Documents\Codigos\Background-jobs-lib\project-detail.md`; o caminho originalmente indicado no diretório pai não existe.
- Regras atuais: `CLAUDE.md`. Referências antigas de stack/harness em `workflow/setup.md` não substituem essas regras.
- Workspace atual contém apenas documentos; não há código, testes, CI nem repositório Git inicializado.
- Decisões aprovadas nas perguntas: Npgsql puro; Minimal APIs em integração ASP.NET Core separada; dashboard estático embutido; DATA_DIR como diretório persistente do Postgres no Compose; executor único com advisory lock e restart controlado após falhas; handler ausente em job persistido segue falha normal de tentativa.
- Gate 1: revisão e aprovação deste design. Gate 2: escrever somente o oráculo, submeter à revisão e aguardar antes de implementar a biblioteca/demo. Gate 3: revisão do resultado com aceitação e kill real.
- Nenhum código será escrito nesta fase. A aprovação do design autoriza somente a fase do oráculo.

## Máquina de estados

| Origem | Destino | Condição/motivo |
|---|---|---|
| inexistente | enqueued | criação imediata ou runAt no passado/presente |
| inexistente | scheduled | criação com runAt futuro |
| scheduled | enqueued | elegibilidade atingida, por agendamento ou retry |
| enqueued | processing | claim atômico de worker disponível |
| processing | succeeded | função retorna sem erro |
| processing | scheduled | função falha e attempts < maxAttempts |
| processing | failed | função falha e attempts = maxAttempts |
| processing | enqueued | recovery: tentativa interrupted e orçamento restante |
| processing | failed | recovery: tentativa interrupted esgotou orçamento |
| scheduled/enqueued | deleted | deleção manual |
| succeeded/failed/deleted | enqueued | reenfileiramento manual |

Toda transição, inclusive criação, gera history na mesma transação que altera jobs. Motivos estáveis e legíveis em inglês, por exemplo created, scheduled time reached, retry delay elapsed, attempt succeeded, retry scheduled, attempts exhausted, recovered interrupted attempt, interrupted attempt exhausted limit, manually deleted, manually requeued.

`interrupted` é resultado de tentativa, não estado de job. Cada claim incrementa attempts, inclusive a tentativa que depois é interrompida. Recovery não incrementa novamente. Com maxAttempts=1, a primeira tentativa interrompida termina em failed. Com orçamento restante, volta imediatamente a enqueued; o próximo claim inicia a tentativa seguinte.

Requeue mantém ID, args, opções, createdAt, histórico e tentativas anteriores. Incrementa uma geração interna, zera attempts e limpa scheduledAt; a nova numeração pública começa em 1. Deleted não apaga fisicamente o job.

Garantia at-least-once sujeita a maxAttempts. Efeitos da função não são transacionados com a conclusão no storage; queda após o efeito pode gerar repetição. Recomendar idempotência ao consumidor.

## Schema Postgres

Schema próprio `background_jobs`; migrations SQL versionadas, controladas pela biblioteca.

### jobs

- id uuid PK, gerado pela biblioteca e exposto como string opaca.
- name text NOT NULL; args jsonb NOT NULL.
- state text NOT NULL com CHECK dos seis estados.
- attempts integer NOT NULL DEFAULT 0; max_attempts integer NOT NULL; retry_delay_ms integer NOT NULL.
- generation integer NOT NULL DEFAULT 0.
- scheduled_at timestamptz NULL: preenchido se e somente se state=scheduled.
- enqueued_at timestamptz NULL e enqueue_sequence bigint NULL: ordem da entrada atual na fila, atribuídos novamente em cada entrada em enqueued.
- created_at, updated_at timestamptz NOT NULL.
- CHECKs: 0 <= attempts <= max_attempts; max_attempts entre 1 e 10; retry_delay_ms entre 0 e 60000; generation >= 0.
- Índice parcial (enqueued_at, enqueue_sequence, id) WHERE state='enqueued'.
- Índice parcial (scheduled_at, id) WHERE state='scheduled'.
- Índice parcial para localizar processing durante recovery.

### attempts

- id bigint GENERATED ALWAYS AS IDENTITY PK.
- job_id uuid NOT NULL FK jobs; generation integer NOT NULL; attempt integer NOT NULL.
- started_at timestamptz NOT NULL; finished_at timestamptz NULL.
- outcome text NULL ou succeeded/failed/interrupted; error text NULL.
- UNIQUE(job_id, generation, attempt).
- UNIQUE parcial(job_id) WHERE outcome IS NULL: no máximo uma tentativa aberta por job.
- CHECK de consistência: em andamento, outcome e finished_at ambos NULL; concluída, ambos preenchidos.
- Índice (job_id, id) para leitura cronológica determinística.

### history

- id bigint GENERATED ALWAYS AS IDENTITY PK.
- job_id uuid NOT NULL FK jobs.
- state text NOT NULL com CHECK; at timestamptz NOT NULL; reason text NOT NULL.
- Índice (job_id, id).

Históricos usam a sequência interna como ordem determinística, inclusive quando timestamps empatam ou o relógio é corrigido. Geração e IDs internos de tentativas não alteram o contrato HTTP. GET detalhado usa snapshot consistente para não misturar estado e histórico de commits diferentes.

Timestamps de elegibilidade/transição usam o relógio do Postgres e são serializados em UTC. O instante de término observado pela execução é preservado ao persistir a tentativa; o deadline de retry nunca é anterior a esse término mais retryDelayMs. Relógios de aplicação e banco no ambiente local devem estar sincronizados; testes de intervalos usam também eventos da função.

## Claim, conclusão e operações concorrentes

1. Worker livre inicia transação curta.
2. Seleciona uma linha enqueued em ordem (enqueued_at, enqueue_sequence, id), com LIMIT 1 FOR UPDATE SKIP LOCKED.
3. Atualiza para processing, incrementa attempts e insere tentativa aberta e history.
4. Commit antes de invocar o handler. Não manter transação/row lock durante execução.
5. Executa e aguarda integralmente a Task/ValueTask do handler.
6. Em outra transação, fecha a tentativa e altera job/history: sucesso, retry ou falha terminal.

Com um worker, a ordem é FIFO pela entrada na fila. Com vários, SKIP LOCKED permite claims concorrentes e não se promete ordem de início. Funções nunca são disparadas a partir do listener ou scheduler.

Delete/requeue validam o estado sob row lock e persistem mudança/history atomicamente. Delete e claim disputam a mesma linha: se delete vence, claim não encontra enqueued; se claim vence, delete responde 409. Só responder sucesso após commit.

Persistência de conclusão é idempotente por job/generation/attempt e estado esperado. Falha ou resposta incerta do banco não autoriza reexecutar a função. Reconciliar o commit do claim antes de invocar; enquanto a situação for desconhecida, não executar. Após retorno da função, tentar persistir a mesma conclusão. Reinício com tentativa ainda aberta aplica interrupted.

## WORKER_COUNT e ciclo de vida

- Criar exatamente WORKER_COUNT loops assíncronos duradouros; cada loop possui no máximo uma invocação ativa e aguarda seu término antes de buscar outro job.
- Sem fire-and-forget, Task.WhenAny de timeout liberando slot, recuperação por lease ou cancelamento forçado de handler.
- O limite se refere às invocações de handlers pela biblioteca. Trabalho destacado pelo próprio handler após retornar não pode ser contabilizado como função ainda em execução; documentar que o handler deve aguardar seu trabalho.
- Claim/início e início da parada compartilham uma barreira de sincronização. A parada fecha novas admissões de forma linearizável; claims em andamento devem ser resolvidos sob essa barreira, sem permitir uma invocação nova depois que a parada estiver estabelecida.
- Ao parar: health indisponível, cessar claims/promoções e acordar loops ociosos para saída; aguardar handlers ativos e persistir conclusões; liberar ownership por último.
- Não propagar o token de shutdown para abortar funções; configurar o host para não impor timeout curto de encerramento. Limites externos do Docker/OS podem causar kill e recovery.
- Garantia principal: invocações ativas <= WORKER_COUNT; por job, no máximo uma invocação ativa no executor suportado.

## Exclusividade e recovery

- Antes de migrations/recovery/workers, adquirir pg_try_advisory_lock de sessão com chave reservada à biblioteca no banco.
- Usar conexão dedicada, mantida viva durante toda a execução; outro executor com lock indisponível falha na inicialização, sem recuperar nada.
- Não retornar conexão com lock à pool; liberar explicitamente no encerramento normal e descartar a conexão.
- Ao perder ownership/conexão: fechar admissões, health=unavailable, drenar handlers e encerrar. Não readquirir automaticamente o lock para continuar.
- Advisory lock não prova morte do processo após partição de rede. Política aprovada: restart/recovery somente depois da confirmação externa de que o executor anterior terminou. Sem takeover automático ou coordenação multi-host.
- Em kill -9 real, as funções do processo cessam; a conexão é liberada pelo banco. Novo processo espera/adquire ownership antes de recovery.
- Recovery, antes de iniciar workers: para cada processing, fechar tentativa aberta como interrupted, com horário de detecção e mensagem explícita; com orçamento, enqueued sem atraso; sem orçamento, failed; history na mesma transação.
- O horário exato da morte é desconhecido: finishedAt de interrupted é o momento de detecção.
- Promover scheduled vencidos, estabelecer LISTEN e fazer varredura de reconciliação antes de health=ok.
- Queda durante recovery é segura: transação não confirmada é refeita; transação confirmada já removeu processing.

## Agendamento, retry e wakeup

- Scheduler da biblioteca promove scheduled com scheduled_at <= clock_timestamp() em lotes transacionais com FOR UPDATE SKIP LOCKED.
- Ao promover, grava enqueued_at/sequence, limpa scheduled_at e escreve history. Empates de agendamento têm desempate determinístico.
- Falha com orçamento grava scheduled_at a partir do término mais atraso fixo; atraso zero ainda passa por scheduled e enqueued para preservar transições.
- Mutação que produz trabalho ou altera o próximo vencimento chama pg_notify em sua transação; Postgres entrega após commit.
- NOTIFY contém apenas sinal de invalidação/wakeup; jobs e prazos são sempre relidos do banco. Perda/coalescência de notificações é tolerada.
- Uma conexão dedicada executa LISTEN; notificação acorda scheduler e workers via sinalização assíncrona local limitada/coalescida, sem usar payload como fila.
- Acordar workers suficientes para drenar backlog, sem depender de uma notificação por job. Após conclusão, worker verifica a fila antes de dormir.
- Scheduler calcula o próximo deadline a partir do relógio do banco e usa timer; NOTIFY interrompe a espera se surgir prazo anterior.
- Fallback configurável, inicialmente 500 ms, para reconciliação de scheduled vencidos e jobs enqueued; um coordenador faz a varredura ociosa, evitando polling por worker.
- Assinar LISTEN antes da varredura inicial; repetir varredura após reconexão para eliminar janela de notificações perdidas.
- Listener é separado da conexão de ownership: falha só do LISTEN permite fallback; falha de ownership fecha admissões.
- Sem iniciar antes de runAt. Em ambiente local sem sobrecarga, com worker livre, NOTIFY é o caminho rápido e fallback deixa margem para SLA de 2 s. Aceitação mede o resultado.

## Estrutura do workspace

```text
BackgroundJobs.slnx
src/
  BackgroundJobs/                 # contratos, registry, motor, Npgsql, migrations, hosting
  BackgroundJobs.AspNetCore/      # Minimal APIs e dashboard embutido
demo/
  BackgroundJobs.Demo/            # configuração, três handlers, eventos e endpoint de criação
examples/
  BackgroundJobs.Console/        # registro/enqueue sem HTTP
tests/
  BackgroundJobs.Tests/
  BackgroundJobs.IntegrationTests/
acceptance/
  run.py                         # stdlib Python, oráculo black-box
  README.md
docker-compose.yml
README.md
workflow/
```

Npgsql puro mantém SQL e fronteiras transacionais explícitos, sem ORM para um modelo pequeno e operações específicas do Postgres. Core usa Generic Host/DI sem depender de ASP.NET. Não criar interfaces abstratas de múltiplos storages fora do escopo.

API pública proposta: registrar handlers tipados por nome estável, configurar conexão/workers, enfileirar/agendar e consultar/deletar/reenfileirar por um cliente injetável. Contexto contém jobId e attempt. Registro é reconstruído no startup; args ficam em JSON. Identificador duplicado falha no startup; nome inválido na criação é erro de entrada. Falta de handler de job já persistido segue falha de execução com mensagem explícita e orçamento normal, preservando os dados.

Demo usa ProjectReference, registra succeed-after, fail-n-times e log-message. POST /demo/jobs valida parâmetros/opções e chama o cliente; não implementa loops, retry ou scheduling. Eventos em memória sincronizados registram entrada, término e mensagem; fail-n-times usa somente attempt do contexto.

Integração ASP.NET mapeia /api/jobs, /api/jobs/{id}, deleção, requeue e /health, com JSON e status exatos do enunciado. IDs são opacos no contrato: procurar ID desconhecido retorna 404; erros de body/validação retornam 400; estado incompatível, 409. Bad JSON e erros de binding também usam { "error": "..." }. Health considera ownership, recovery concluído e capacidade de acesso ao storage.

Dashboard em /jobs/dashboard com assets embutidos: contadores, health, filtro local por estado, lista, detalhes completos, delete/requeue e feed de eventos por polling. URL do feed é configuração da aplicação hospedeira; demo fornece /demo/events. Renderizar argumentos, mensagens e erros como texto seguro. A raiz da aplicação fica livre.

## Configuração e durabilidade

- PORT=8080, WORKER_COUNT=4, DATA_DIR=./data.
- Compose monta DATA_DIR no diretório de dados da versão de Postgres fixada; não é storage de arquivos da biblioteca.
- Biblioteca recebe connection string via configuração; apontar para Postgres externo transfere gestão do volume para esse servidor.
- Preservar dados em restart; não desligar fsync/synchronous_commit para acelerar testes.
- Validar WORKER_COUNT positivo no startup; opções de jobs e parâmetros conforme os limites do contrato.
- Demo sem restart automático após falha de ownership; documentar operação controlada.

## Oráculo e validação

Após Gate 1, escrever acceptance sem implementar o sistema. Python stdlib para HTTP/JSON/assertions e subprocess para Compose. Um comando cria storage isolado por cenário, preserva-o apenas nas etapas de restart e imprime PASS/FAIL com evidências. Controle do processo é externo; observação funcional usa exclusivamente endpoints, sem SQL ou classes internas.

Cobrir os 10 cenários: imediato; futuro/passado e SLA; FIFO; sobreposição/limite/ausência de duplicação pelos eventos; retry fixo; exaustão isolada; delete/requeue e histórico; persistência após kill; interrupted com/sem orçamento; parada drenando ativo sem iniciar pendente.

Kill real do processo da demo em container Linux, mantendo Postgres/dados; graceful stop por SIGTERM, com processo .NET como PID 1 e espera externa suficiente. Eventos podem sumir no restart, conforme contrato.

Contrato adicional: 400 para JSON/parâmetros/opções inválidos; 404 para ID ausente; 409 em estados incompatíveis; defaults, formatos/UTC/nulls, listagem, job completo, eventos e health. Validar attempts/history após requeue e também deleted reenfileirado. Assertions não exigem texto exato de reason/error além de demo failure quando especificado.

Testes internos complementares para atomicidade delete/claim, segundo executor rejeitado, perda de ownership, falha de persistência, recovery transacional e barreira de shutdown. Dashboard exige revisão manual além de verificação HTTP de assets/rota. Não substituir oráculo HTTP por testes de storage.

## Milestones e commits

0. Após aprovação, inicializar Git e registrar design/decisões aprovadas e contexto real do workflow, preservando registros históricos.
1. Oráculo: acceptance completo, comando e evidências esperadas; commit `test: add HTTP acceptance oracle`. Parar no Gate 2. A ausência de implementação deve ser reportada como bloqueio esperado, nunca como aprovação dos cenários.
2. Fundação: solution/projetos, registry e contratos, Postgres/Compose/migrations, persistência, consultas e validação HTTP; commit `feat: add persistent job storage and HTTP contracts`.
3. Execução: ownership, claim, workers limitados, handlers/eventos e conclusão; commit `feat: add bounded job execution`.
4. Agendamento e retry: scheduler, LISTEN/NOTIFY, timer/fallback, opções e FIFO de reentrada; commit `feat: add scheduling retries and worker wakeups`.
5. Ciclo de vida: recovery, shutdown, delete/requeue, resiliência de persistência; commit `feat: add recovery shutdown and job management`.
6. Dashboard: painel da biblioteca, ações e feed configurável; commit `feat: add embedded jobs dashboard`.
7. Entrega: exemplo sem HTTP, documentação reproduzível, evidências completas e workflow; commit `docs: add usage examples and acceptance evidence`.

Em cada milestone de implementação, executar aceitação e testes pertinentes, mostrar saída real e distinguir cenários ainda não implementados. Ao final, todos os 10 cenários e contratos devem passar; revisão humana do dashboard e dos cenários com kill real. Um commit por milestone, subjects em inglês/conventional commits. Push/PR exigem confirmação prévia.

## Limites e pontos a revisar no Gate 1

- O invariante de concorrência pressupõe o executor único e restart controlado aprovados; não existe failover automático durante partição.
- Durabilidade de aceitação depende do commit no Postgres; efeitos externos dos handlers podem repetir.
- Queda após execução e antes de persistir sucesso pode consumir a última tentativa como interrupted/failed.
- Handler ausente após mudança de aplicação usa o caminho de falha normal, conforme decisão explícita do usuário.
- Este documento é proposta de design, sem evidência de implementação/testes executados.
