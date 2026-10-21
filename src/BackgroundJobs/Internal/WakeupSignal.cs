namespace BackgroundJobs.Internal;

/// <summary>
/// In-process broadcast pulse. Every waiter is released on the next Pulse().
/// Fed by the LISTEN/NOTIFY listener and by in-process mutations; waiters also
/// pass a timeout so a lost notification degrades to the fallback poll.
/// </summary>
internal sealed class WakeupSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _current = New();

    private static TaskCompletionSource New()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Pulse()
    {
        TaskCompletionSource t;
        lock (_gate)
        {
            t = _current;
            _current = New();
        }
        t.TrySetResult();
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task pending;
        lock (_gate) pending = _current.Task;
        var delay = Task.Delay(timeout, ct);
        return Await(pending, delay);
    }

    private static async Task Await(Task pending, Task delay)
    {
        // Swallow delay cancellation: callers re-check their own conditions.
        var done = await Task.WhenAny(pending, delay).ConfigureAwait(false);
        try { await done.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
