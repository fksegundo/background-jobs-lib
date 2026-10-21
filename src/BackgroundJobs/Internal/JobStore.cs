using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace BackgroundJobs.Internal;

/// <summary>
/// All persistence for the engine. Every state transition writes the job row,
/// the attempt row and the history entry in one transaction. All timestamps
/// are taken from the Postgres clock so eligibility math is consistent.
/// </summary>
internal sealed class JobStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly string _notifyChannel;

    public JobStore(string connectionString, string notifyChannel)
    {
        _ds = NpgsqlDataSource.Create(connectionString);
        _notifyChannel = notifyChannel;
    }

    private async Task NotifyAsync(NpgsqlCommand cmd)
    {
        cmd.CommandText = "SELECT pg_notify(@ch, 'wakeup')";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("ch", _notifyChannel);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
        => await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);

    // ------------------------------------------------------------ mutations

    /// <summary>Persist a new job + creation history, then notify. Returns id after commit.</summary>
    public async Task<Guid> EnqueueAsync(string name, JsonElement args, JobOptions opts, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var due = opts.RunAt?.UtcDateTime;
        var immediate = due is null || due <= DateTime.UtcNow;
        var state = immediate ? "enqueued" : "scheduled";
        var reason = immediate ? "created (immediate)" : "created (scheduled)";

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO background_jobs.jobs
                    (id, name, args, state, max_attempts, retry_delay_ms, scheduled_at, enqueued_at, enqueue_seq)
                VALUES (@id, @name, @args::jsonb, @state, @max, @delay, @sched,
                        CASE WHEN @state = 'enqueued' THEN clock_timestamp() END,
                        CASE WHEN @state = 'enqueued' THEN nextval('background_jobs.enqueue_seq') END);
                INSERT INTO background_jobs.history (job_id, state, reason)
                VALUES (@id, @state, @reason);
                """;
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("args", args.GetRawText());
            cmd.Parameters.AddWithValue("state", state);
            cmd.Parameters.AddWithValue("max", opts.MaxAttempts);
            cmd.Parameters.AddWithValue("delay", opts.RetryDelayMs);
            cmd.Parameters.AddWithValue("sched", NpgsqlDbType.TimestampTz,
                immediate ? DBNull.Value : due!.Value);
            cmd.Parameters.AddWithValue("reason", reason);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; await NotifyAsync(cmd); }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Atomically claim the oldest enqueued job for this worker.
    /// The transaction commits BEFORE the handler is invoked.
    /// </summary>
    public async Task<JobClaim?> TryClaimAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        Guid jobId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id FROM background_jobs.jobs
                WHERE state = 'enqueued' AND attempts < max_attempts
                ORDER BY enqueued_at, enqueue_seq, id
                LIMIT 1 FOR UPDATE SKIP LOCKED;
                """;
            var found = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return null; }
            jobId = (Guid)found;
        }

        string name; JsonElement args; int attempt; int generation; int maxAttempts; int retryDelay;
        var claimToken = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE background_jobs.jobs
                SET state = 'processing', attempts = attempts + 1,
                    enqueued_at = NULL, enqueue_seq = NULL, updated_at = clock_timestamp()
                WHERE id = @id
                RETURNING name, args::text, attempts, generation, max_attempts, retry_delay_ms;
                """;
            cmd.Parameters.AddWithValue("id", jobId);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            name = r.GetString(0);
            args = JsonSerializer.Deserialize<JsonElement>(r.GetString(1));
            attempt = r.GetInt32(2);
            generation = r.GetInt32(3);
            maxAttempts = r.GetInt32(4);
            retryDelay = r.GetInt32(5);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO background_jobs.attempts (job_id, generation, attempt, started_at, claim_token)
                VALUES (@id, @gen, @att, clock_timestamp(), @token);
                INSERT INTO background_jobs.history (job_id, state, reason)
                VALUES (@id, 'processing', 'claimed by worker');
                """;
            cmd.Parameters.AddWithValue("id", jobId);
            cmd.Parameters.AddWithValue("gen", generation);
            cmd.Parameters.AddWithValue("att", attempt);
            cmd.Parameters.AddWithValue("token", claimToken);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        try
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Commit outcome unknown: only invoke the handler if our claim —
            // identified by its unique token — was actually persisted.
            if (!await ClaimCommittedAsync(jobId, claimToken).ConfigureAwait(false)) throw;
        }
        return new JobClaim(jobId, name, args, generation, attempt, maxAttempts, retryDelay);
    }

    /// <summary>
    /// After an ambiguous claim commit (timeout/connection drop), checks whether
    /// this specific claim — not another worker's — was persisted.
    /// </summary>
    private async Task<bool> ClaimCommittedAsync(Guid jobId, Guid claimToken)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                await using var conn = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM background_jobs.attempts
                        WHERE job_id = @id AND claim_token = @token AND outcome IS NULL);
                    """;
                cmd.Parameters.AddWithValue("id", jobId);
                cmd.Parameters.AddWithValue("token", claimToken);
                return (bool)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
            }
            catch
            {
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        return false;
    }

    /// <summary>
    /// Close the attempt and transition the job: succeeded, scheduled (retry)
    /// or failed (budget exhausted). Idempotent on (job, generation, attempt).
    /// </summary>
    public async Task CompleteAttemptAsync(JobClaim c, bool succeeded, string? error, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        DateTimeOffset finishedAt;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE background_jobs.attempts
                SET finished_at = clock_timestamp(), outcome = @oc, error = @err
                WHERE job_id = @id AND generation = @gen AND attempt = @att AND outcome IS NULL
                RETURNING finished_at;
                """;
            cmd.Parameters.AddWithValue("id", c.JobId);
            cmd.Parameters.AddWithValue("gen", c.Generation);
            cmd.Parameters.AddWithValue("att", c.Attempt);
            cmd.Parameters.AddWithValue("oc", succeeded ? "succeeded" : "failed");
            cmd.Parameters.AddWithValue("err", NpgsqlDbType.Text,
                error is null ? DBNull.Value : error);
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (res is null)
            {
                // Attempt already closed (duplicate completion): nothing to do.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }
            finishedAt = (DateTimeOffset)res;
        }

        string nextState;
        string reason;
        if (succeeded)
        {
            nextState = "succeeded"; reason = "attempt succeeded";
        }
        else if (c.Attempt < c.MaxAttempts)
        {
            nextState = "scheduled"; reason = "retry scheduled";
        }
        else
        {
            nextState = "failed"; reason = "attempts exhausted";
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE background_jobs.jobs
                SET state = @state,
                    scheduled_at = CASE WHEN @state = 'scheduled'
                                        THEN @fin + (@delay * interval '1 millisecond') END,
                    updated_at = clock_timestamp()
                WHERE id = @id AND state = 'processing';
                INSERT INTO background_jobs.history (job_id, state, reason)
                SELECT @id, @state, @reason
                WHERE EXISTS (SELECT 1 FROM background_jobs.jobs WHERE id = @id AND state = @state);
                """;
            cmd.Parameters.AddWithValue("id", c.JobId);
            cmd.Parameters.AddWithValue("state", nextState);
            cmd.Parameters.AddWithValue("reason", reason);
            cmd.Parameters.AddWithValue("fin", finishedAt);
            cmd.Parameters.AddWithValue("delay", c.RetryDelayMs);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; await NotifyAsync(cmd); }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Promote due scheduled jobs to enqueued, preserving scheduled_at order.</summary>
    public async Task<int> PromoteDueAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var due = new List<Guid>(128);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id FROM background_jobs.jobs
                WHERE state = 'scheduled' AND scheduled_at <= clock_timestamp()
                ORDER BY scheduled_at, id
                LIMIT 200 FOR UPDATE SKIP LOCKED;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) due.Add(r.GetGuid(0));
        }

        foreach (var id in due)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE background_jobs.jobs
                SET state = 'enqueued', scheduled_at = NULL,
                    enqueued_at = clock_timestamp(),
                    enqueue_seq = nextval('background_jobs.enqueue_seq'),
                    updated_at = clock_timestamp()
                WHERE id = @id;
                INSERT INTO background_jobs.history (job_id, state, reason)
                VALUES (@id, 'enqueued', 'became eligible');
                """;
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (due.Count > 0)
        {
            await using var n = conn.CreateCommand();
            n.Transaction = tx;
            await NotifyAsync(n);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return due.Count;
    }

    /// <summary>Next scheduled eligibility instant, for the scheduler timer.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(scheduled_at) FROM background_jobs.jobs WHERE state = 'scheduled';
            """;
        var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return res is DateTimeOffset dto ? dto : null;
    }

    /// <summary>
    /// Startup recovery: every job left in processing has its open attempt
    /// closed as interrupted. With budget left -> enqueued now; without -> failed.
    /// </summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var stuck = new List<(Guid Id, int Attempts, int MaxAttempts, int Generation)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, attempts, max_attempts, generation FROM background_jobs.jobs
                WHERE state = 'processing' FOR UPDATE;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                stuck.Add((r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        }

        foreach (var (id, attempts, maxAttempts, generation) in stuck)
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE background_jobs.attempts
                    SET finished_at = clock_timestamp(), outcome = 'interrupted',
                        error = 'process terminated before the attempt finished'
                    WHERE job_id = @id AND outcome IS NULL;
                    """;
                cmd.Parameters.AddWithValue("id", id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // The interrupted attempt already counted toward maxAttempts.
            var nextState = attempts < maxAttempts ? "enqueued" : "failed";
            var reason = attempts < maxAttempts
                ? "recovered interrupted attempt"
                : "interrupted attempt exhausted limit";

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE background_jobs.jobs
                    SET state = @state, updated_at = clock_timestamp(),
                        enqueued_at = CASE WHEN @state = 'enqueued' THEN clock_timestamp() END,
                        enqueue_seq = CASE WHEN @state = 'enqueued'
                                           THEN nextval('background_jobs.enqueue_seq') END
                    WHERE id = @id;
                    INSERT INTO background_jobs.history (job_id, state, reason)
                    VALUES (@id, @state, @reason);
                    """;
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("state", nextState);
                cmd.Parameters.AddWithValue("reason", reason);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        if (stuck.Count > 0)
        {
            await using var n = conn.CreateCommand();
            n.Transaction = tx;
            await NotifyAsync(n);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>scheduled|enqueued -> deleted. 404/409 via exceptions.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string state;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT state FROM background_jobs.jobs WHERE id = @id FOR UPDATE;";
            cmd.Parameters.AddWithValue("id", id);
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (res is null) { await tx.RollbackAsync(ct).ConfigureAwait(false); throw new JobNotFoundException(id.ToString()); }
            state = (string)res;
        }
        if (state is not ("scheduled" or "enqueued"))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw new JobConflictException($"job is '{state}'; only scheduled or enqueued jobs can be deleted");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE background_jobs.jobs
                SET state = 'deleted', scheduled_at = NULL, updated_at = clock_timestamp()
                WHERE id = @id;
                INSERT INTO background_jobs.history (job_id, state, reason)
                VALUES (@id, 'deleted', 'manually deleted');
                """;
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>succeeded|failed|deleted -> enqueued; attempts reset, generation bumped.</summary>
    public async Task RequeueAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string state;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT state FROM background_jobs.jobs WHERE id = @id FOR UPDATE;";
            cmd.Parameters.AddWithValue("id", id);
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (res is null) { await tx.RollbackAsync(ct).ConfigureAwait(false); throw new JobNotFoundException(id.ToString()); }
            state = (string)res;
        }
        if (state is not ("succeeded" or "failed" or "deleted"))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw new JobConflictException($"job is '{state}'; only finished jobs can be requeued");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE background_jobs.jobs
                SET state = 'enqueued', attempts = 0, generation = generation + 1,
                    scheduled_at = NULL, enqueued_at = clock_timestamp(),
                    enqueue_seq = nextval('background_jobs.enqueue_seq'),
                    updated_at = clock_timestamp()
                WHERE id = @id;
                INSERT INTO background_jobs.history (job_id, state, reason)
                VALUES (@id, 'enqueued', 'manually requeued');
                """;
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var cmd = conn.CreateCommand()) { cmd.Transaction = tx; await NotifyAsync(cmd); }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- reads

    public async Task<IReadOnlyList<JobSummary>> ListAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, state, attempts FROM background_jobs.jobs
            ORDER BY created_at, id;
            """;
        var list = new List<JobSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new JobSummary(r.GetGuid(0).ToString(), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return list;
    }

    /// <summary>Consistent snapshot of job + history + attempts (repeatable read tx).</summary>
    public async Task<JobDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        JobDetail? detail = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, name, args::text, state, attempts, max_attempts, retry_delay_ms,
                       scheduled_at, created_at, updated_at
                FROM background_jobs.jobs WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                detail = new JobDetail(
                    r.GetGuid(0).ToString(), r.GetString(1),
                    JsonSerializer.Deserialize<JsonElement>(r.GetString(2)),
                    r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
                    r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
                    r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9),
                    Array.Empty<HistoryEntry>(), Array.Empty<AttemptRecord>());
            }
        }
        if (detail is null) { await tx.RollbackAsync(ct).ConfigureAwait(false); return null; }

        var history = new List<HistoryEntry>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT state, at, reason FROM background_jobs.history
                WHERE job_id = @id ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                history.Add(new HistoryEntry(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2)));
        }

        var attempts = new List<AttemptRecord>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT attempt, started_at, finished_at, outcome, error
                FROM background_jobs.attempts WHERE job_id = @id ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                attempts.Add(new AttemptRecord(
                    r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1),
                    r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return detail with { History = history, AttemptHistory = attempts };
    }

    /// <summary>Storage liveness probe used by /health.</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 3;
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}
