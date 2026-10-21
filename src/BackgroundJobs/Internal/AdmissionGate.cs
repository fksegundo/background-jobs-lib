namespace BackgroundJobs.Internal;

/// <summary>
/// Linearization point between "claim a new job" and "begin shutdown".
/// A claim that observes stopping=false under the gate is allowed to run to
/// completion and is counted in Active; once StopAsync publishes stopping,
/// no new claim is admitted and StopAsync returns only when Active drains to 0.
/// </summary>
internal sealed class AdmissionGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private int _active;

    public bool IsStopping => Volatile.Read(ref _stopping);
    public int Active => Volatile.Read(ref _active);

    /// <summary>
    /// Runs the claim under the gate. Returns the claim, or null when the queue
    /// was empty or the gate is already closed (check IsStopping to tell apart).
    /// </summary>
    public async Task<JobClaim?> TryAdmitAsync(Func<CancellationToken, Task<JobClaim?>> claim, CancellationToken ct)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopping) return null;
            var c = await claim(ct).ConfigureAwait(false);
            if (c is not null) Interlocked.Increment(ref _active);
            return c;
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>Called by a worker after the attempt outcome was handled.</summary>
    public void Complete()
    {
        _sem.Wait();
        try
        {
            _active--;
            if (_stopping && _active == 0) _drained.TrySetResult();
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>Closes admissions and waits until every admitted job finished.</summary>
    public async Task StopAndDrainAsync(CancellationToken ct)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _stopping = true;
            if (_active == 0) _drained.TrySetResult();
        }
        finally
        {
            _sem.Release();
        }
        await _drained.Task.WaitAsync(ct).ConfigureAwait(false);
    }
}
