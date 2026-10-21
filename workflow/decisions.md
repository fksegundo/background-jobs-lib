# Decisões (append-only)

Formato: `- [data] decisão — motivo`.

- [2026-10-21] Npgsql puro + Minimal APIs em projeto AspNetCore separado — SQL transacional explícito, núcleo usável sem HTTP
- [2026-10-21] DATA_DIR = diretório persistente do Postgres no compose — biblioteca recebe connection string
- [2026-10-21] Advisory lock de sessão + restart controlado — sem takeover automático em partição
- [2026-10-21] Handler ausente após reinício falha a tentativa normalmente — preserva dados e orçamento
- [2026-10-21] Oráculo em Python stdlib + compose (kill -9 real) — decisão anterior sobre Rust/Claude em setup.md substituída pelo stack do CLAUDE.md
- [2026-10-21] Claim ambíguo reconciliado por claim_token uuid na tentativa — evita dupla execução em commit incerto
- [2026-10-21] Retry de conexão no startup (30x1s) — Postgres ainda inicializando sob depends_on healthy em bind mount Windows
- [2026-10-21] S10 prova drain por timestamps persistidos — HTTP para de servir quando shutdown começa, eventos inobserváveis durante drain
- [2026-10-21] Postgres publicado em 15432 no host — exemplo console roda local contra o compose
