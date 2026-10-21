CREATE SCHEMA IF NOT EXISTS background_jobs;

CREATE TABLE IF NOT EXISTS background_jobs._migrations (
    version     integer PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS background_jobs.jobs (
    id              uuid        PRIMARY KEY,
    name            text        NOT NULL,
    args            jsonb       NOT NULL,
    state           text        NOT NULL CHECK (state IN ('scheduled','enqueued','processing','succeeded','failed','deleted')),
    attempts        integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    max_attempts    integer     NOT NULL CHECK (max_attempts BETWEEN 1 AND 10),
    retry_delay_ms  integer     NOT NULL CHECK (retry_delay_ms BETWEEN 0 AND 60000),
    generation      integer     NOT NULL DEFAULT 0 CHECK (generation >= 0),
    scheduled_at    timestamptz NULL,
    enqueued_at     timestamptz NULL,
    enqueue_seq     bigint      NULL,
    created_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (attempts <= max_attempts),
    CHECK ((state = 'scheduled') = (scheduled_at IS NOT NULL))
);

CREATE SEQUENCE IF NOT EXISTS background_jobs.enqueue_seq;

CREATE INDEX IF NOT EXISTS jobs_enqueued_order
    ON background_jobs.jobs (enqueued_at, enqueue_seq, id) WHERE state = 'enqueued';
CREATE INDEX IF NOT EXISTS jobs_scheduled_due
    ON background_jobs.jobs (scheduled_at, id) WHERE state = 'scheduled';
CREATE INDEX IF NOT EXISTS jobs_processing
    ON background_jobs.jobs (id) WHERE state = 'processing';

CREATE TABLE IF NOT EXISTS background_jobs.attempts (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id      uuid        NOT NULL REFERENCES background_jobs.jobs (id),
    generation  integer     NOT NULL,
    attempt     integer     NOT NULL,
    claim_token uuid        NULL,
    started_at  timestamptz NOT NULL,
    finished_at timestamptz NULL,
    outcome     text        NULL CHECK (outcome IN ('succeeded','failed','interrupted')),
    error       text        NULL,
    UNIQUE (job_id, generation, attempt),
    CHECK ((outcome IS NULL) = (finished_at IS NULL))
);

-- At most one open attempt per job: a job can never run in two workers.
CREATE UNIQUE INDEX IF NOT EXISTS attempts_one_open
    ON background_jobs.attempts (job_id) WHERE outcome IS NULL;
CREATE INDEX IF NOT EXISTS attempts_job_order
    ON background_jobs.attempts (job_id, id);

CREATE TABLE IF NOT EXISTS background_jobs.history (
    id      bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id  uuid        NOT NULL REFERENCES background_jobs.jobs (id),
    state   text        NOT NULL CHECK (state IN ('scheduled','enqueued','processing','succeeded','failed','deleted')),
    at      timestamptz NOT NULL DEFAULT clock_timestamp(),
    reason  text        NOT NULL
);

CREATE INDEX IF NOT EXISTS history_job_order
    ON background_jobs.history (job_id, id);

INSERT INTO background_jobs._migrations (version) VALUES (1) ON CONFLICT DO NOTHING;
