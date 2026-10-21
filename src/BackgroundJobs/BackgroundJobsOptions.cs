namespace BackgroundJobs;

/// <summary>Engine configuration.</summary>
public sealed class BackgroundJobsOptions
{
    /// <summary>Npgsql connection string to Postgres. Required.</summary>
    public required string ConnectionString { get; set; }

    /// <summary>Max job functions running at once. Never exceeded. Default 4.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Fallback reconciliation interval when LISTEN/NOTIFY is silent.</summary>
    public TimeSpan PollFallback { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Postgres LISTEN channel used for worker wakeups.</summary>
    public string NotifyChannel { get; set; } = "bgjobs_wakeup";
}
