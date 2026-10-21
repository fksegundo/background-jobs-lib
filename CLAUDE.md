# Background Jobs Library

Biblioteca de background jobs em .NET + aplicação demo HTTP.
O enunciado completo está em `../project-detail.md` — é a fonte da verdade; leia antes de qualquer decisão de design.

## Stack fixa (decidida, não propor alternativas)

- .NET 11 + C#; HTTP com ASP.NET Core (Minimal APIs ou MVC — propor no design).
- Postgres como storage (a demo sobe via `docker-compose`).
- Claim atômico de jobs via `SELECT ... FOR UPDATE SKIP LOCKED`.
- Wakeup dos workers via `LISTEN/NOTIFY` (evitar polling agressivo; requisito de início em até 2s).
- Acesso a dados: propor no design (Npgsql puro, Dapper ou EF Core), justificar a escolha.

## Motor próprio

O motor de processamento/agendamento é implementado NESTE projeto.
Proibido delegar isso a bibliotecas de job queue (Hangfire, Quartz.NET, Coravel e similares).
Pacotes NuGet de HTTP, serialização, acesso a dados e utilidades são permitidos.

## Convenções

- Código, comentários e commits em inglês.
- Commits: conventional commits, single-line (ex.: `feat: add job scheduler`). Explicação longa vai no corpo/descrição, não no subject — e só se necessário.
- Comentários no código somente quando estritamente necessários.

## Interação

- Decisão ambígua (arquitetura, semântica do enunciado, trade-off material): NÃO decida sozinho — pergunte, propondo a opção recomendada e o porquê.
- Não declare trabalho pronto sem evidência: rode testes/aceitação e mostre a saída.
- Antes de ações externas irreversíveis (push, PR), confirme comigo.

## Workflow desta sessão (ordem obrigatória)

1. **Design primeiro**: máquina de estados, schema Postgres, protocolo de claim, recuperação pós `kill -9`, dispatch via NOTIFY, estrutura do workspace e milestones. Apresentar e aguardar aprovação antes de codar.
2. **Oráculo**: com o design aprovado, escrever `acceptance/` — script black-box via HTTP que cobre os 10 cenários de aceite + contrato de erros. Também aguarda minha revisão antes da implementação.
3. **Implementação**: por milestones, um commit por milestone.
