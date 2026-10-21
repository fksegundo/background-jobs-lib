using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackgroundJobs.Internal;

/// <summary>
/// The job engine: exclusive executor ownership, startup recovery, the
/// scheduler, the LISTEN/NOTIFY wakeup path and the bounded worker pool.
///
/// Concurrency invariant: exactly WorkerCount loops exist, each awaits its
/// handler to completion before claiming again — never more running job
/// functions than WorkerCount, and never the same job twice.
/// </summary>
internal sealed class JobEngine : BackgroundService
{
    private const long OwnerLockKey = 0x62676A6F62733031; // "bgjobs01"

    private readonly BackgroundJobsOptions _options;
    private readonly JobStore _store;
    private readonly JobRegistry _registry;
    private readonly JobServer _server;
    private readonly WakeupSignal _signal;
    private readonly ILogger<JobEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly AdmissionGate _gate = new();
    private NpgsqlConnection? _owner;
    private NotificationListener? _listener;
    private Task? _scheduler;
    private Task? _watchdog;
    private Task[]? _workers;

    public JobEngine(BackgroundJobsOptions options, JobStore store, JobRegistry registry,
        JobServer server, WakeupSignal signal, ILogger<JobEngine> logger, ILoggerFactory loggerFactory)
    {
        _options = options;
        _store = store;
        _registry = registry;
        _server = server;
        _signal = signal;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _server.Status = ServerStatus.Initializing;

            // 1. Exclusive executor ownership before touching any job state.
            _owner = await AcquireOwnershipAsync(stoppingToken).ConfigureAwait(false);

            // 2. Schema, then recovery — before any worker starts.
            await Migrations.ApplyAsync(_owner, stoppingToken).ConfigureAwait(false);
            await _store.RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);

            // 3. LISTEN before the first reconciliation so no notify is missed.
            _listener = new NotificationListener(
                _options.ConnectionString, _options.NotifyChannel, _signal,
                _loggerFactory.CreateLogger<NotificationListener>());
            _listener.Start();
            await _store.PromoteDueAsync(stoppingToken).ConfigureAwait(false);

            // 4. Scheduler + watchdog + workers.
            _scheduler = Task.Run(() => SchedulerLoop(stoppingToken), CancellationToken.None);
            _watchdog = Task.Run(() => OwnerWatchdogAsync(stoppingToken), CancellationToken.None);
            _workers = Enumerable.Range(0, _options.WorkerCount)
                .Select(i => Task.Run(() => WorkerLoop(i, stoppingToken), CancellationToken.None))
                .ToArray();
            _server.Status = ServerStatus.Running;
            _logger.LogInformation("job engine started with {Workers} workers", _options.WorkerCount);

            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            _server.Status = ServerStatus.Stopped;
            _logger.LogError(e, "job engine failed");
            throw;
        }
        finally
        {
            if (_server.Status == ServerStatus.Running) _server.Status = ServerStatus.Stopped;
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // SIGTERM path: close admissions first, then drain in-flight handlers.
        // Handlers are never cancelled; there is no bound on the drain wait.
        if (_server.Status == ServerStatus.Running) _server.Status = ServerStatus.Stopping;
        _signal.Pulse();
        try { await _gate.StopAndDrainAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { _logger.LogWarning(e, "drain wait failed"); }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- setup

    private async Task<NpgsqlConnection> AcquireOwnershipAsync(CancellationToken ct)
    {
        // Dedicated unpooled connection: the session-level advisory lock must
        // never leak into a pool, and its lifetime equals this process's.
        var csb = new NpgsqlConnectionStringBuilder(_options.ConnectionString)
        {
            Pooling = false,
            Timeout = 5,
        };

        // Postgres may still be initializing (fresh volume, slow start):
        // retry the connect, but fail fast on lock contention.
        for (var i = 0; ; i++)
        {
            var conn = new NpgsqlConnection(csb.ConnectionString);
            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (i < 30 && !ct.IsCancellationRequested)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
            cmd.Parameters.AddWithValue("key", OwnerLockKey);
            var got = (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            if (got) return conn;
            await conn.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "another executor already holds this job storage; only one executor is supported");
        }
    }

    private async Task OwnerWatchdogAsync(CancellationToken ct)
    {
        // If the ownership session dies, another executor could take over.
        // Close admissions and drain — never keep claiming without the lock.
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                await using var cmd = _owner!.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.CommandTimeout = 5;
                await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "ownership connection lost; stopping admissions and draining");
                _server.Status = ServerStatus.LostOwnership;
                _signal.Pulse();
                try { await _gate.StopAndDrainAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* best effort */ }
                return;
            }
        }
    }

    private async Task CleanupAsync()
    {
        if (_listener is not null) await _listener.StopAsync().ConfigureAwait(false);
        if (_owner is not null)
        {
            try
            {
                await using var cmd = _owner.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                cmd.Parameters.AddWithValue("key", OwnerLockKey);
                await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch { /* connection may already be gone */ }
            await _owner.DisposeAsync().ConfigureAwait(false);
        }
    }

    // --------------------------------------------------------------- workers

    private async Task WorkerLoop(int index, CancellationToken ct)
    {
        while (true)
        {
            JobClaim? claim;
            try
            {
                claim = await _gate.TryAdmitAsync(c => _store.TryClaimAsync(c), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                _logger.LogError(e, "claim failed");
                await SleepAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (claim is null)
            {
                if (_gate.IsStopping || ct.IsCancellationRequested) return;
                await _signal.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var (ok, error) = await InvokeAsync(claim, ct).ConfigureAwait(false);
                await PersistCompletionAsync(claim, ok, error, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Complete();
            }
        }
    }

    /// <summary>Runs the handler and captures its outcome. Never throws.</summary>
    private async Task<(bool Ok, string? Error)> InvokeAsync(JobClaim claim, CancellationToken ct)
    {
        var handler = _registry.Resolve(claim.Name);
        if (handler is null)
            return (false, $"no handler registered for job '{claim.Name}'");
        try
        {
            var ctx = new JobContext(claim.JobId.ToString(), claim.Attempt);
            await handler(claim.Args, ctx).ConfigureAwait(false);
            return (true, null);
        }
        catch (JobValidationException e)
        {
            return (false, e.Message);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "job {JobId} attempt {Attempt} failed", claim.JobId, claim.Attempt);
            return (false, e.Message);
        }
    }

    /// <summary>
    /// Persist the attempt outcome with bounded retries. If persistence stays
    /// broken, the job remains in processing and next-startup recovery marks
    /// the attempt interrupted — never silently lost.
    /// </summary>
    private async Task PersistCompletionAsync(JobClaim claim, bool ok, string? error, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                await _store.CompleteAttemptAsync(claim, ok, error, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (i < 4)
            {
                _logger.LogWarning(e, "completion persist attempt {Try} failed", i + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (i + 1)), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "could not persist outcome for job {JobId}; it will be recovered as interrupted",
                    claim.JobId);
                return;
            }
        }
    }

    // -------------------------------------------------------------- scheduler

    private async Task SchedulerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_gate.IsStopping)
        {
            try
            {
                var promoted = await _store.PromoteDueAsync(ct).ConfigureAwait(false);
                if (promoted > 0)
                {
                    _logger.LogInformation("promoted {Count} scheduled jobs", promoted);
                    _signal.Pulse();
                }
                var next = await _store.NextDueAsync(ct).ConfigureAwait(false);
                var delay = _options.PollFallback;
                if (next is DateTimeOffset due)
                {
                    var until = due - DateTimeOffset.UtcNow;
                    if (until < delay) delay = until > TimeSpan.Zero ? until : TimeSpan.FromMilliseconds(5);
                }
                await _signal.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                _logger.LogWarning(e, "scheduler iteration failed");
                await SleepAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SleepAsync(CancellationToken ct)
    {
        try { await Task.Delay(_options.PollFallback, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
