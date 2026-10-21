namespace BackgroundJobs.Demo;

/// <summary>
/// In-memory observable event feed written by the demo job functions
/// (independent of the library's persisted history). Lost on restart by design.
/// </summary>
public sealed class DemoEventLog
{
    private readonly object _gate = new();
    private readonly List<DemoEvent> _events = new();

    public void Started(JobContext ctx)
        => Add(new DemoEvent("started", ctx.JobId, ctx.Attempt, DateTimeOffset.UtcNow, null));

    public void Finished(JobContext ctx, string outcome)
        => Add(new DemoEvent("finished", ctx.JobId, ctx.Attempt, DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["outcome"] = outcome }));

    public void Message(JobContext ctx, string message)
        => Add(new DemoEvent("message", ctx.JobId, ctx.Attempt, DateTimeOffset.UtcNow,
            new Dictionary<string, string?> { ["message"] = message }));

    private void Add(DemoEvent e)
    {
        lock (_gate) _events.Add(e);
    }

    public IReadOnlyList<DemoEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }
}

public sealed record DemoEvent(string Type, string JobId, int Attempt, DateTimeOffset At, object? Detail);
