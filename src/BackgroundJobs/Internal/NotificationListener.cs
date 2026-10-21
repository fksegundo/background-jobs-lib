using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackgroundJobs.Internal;

/// <summary>
/// Dedicated LISTEN connection. A notification only means "state may have
/// changed — wake up"; workers and the scheduler always re-read Postgres.
/// Reconnects with backoff; the fallback poll in waiters covers silent periods.
/// </summary>
internal sealed class NotificationListener(
    string connectionString,
    string channel,
    WakeupSignal signal,
    ILogger logger)
{
    private Task? _loop;
    private readonly CancellationTokenSource _stop = new();

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                conn.Notification += (_, _) => signal.Pulse();
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"LISTEN {channel}";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                logger.LogInformation("listening on postgres channel {Channel}", channel);
                signal.Pulse(); // re-scan after (re)connect: notifications may have been missed
                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning(e, "LISTEN connection lost; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
