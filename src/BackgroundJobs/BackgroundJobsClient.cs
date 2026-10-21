using System.Text.Json;
using BackgroundJobs.Internal;

namespace BackgroundJobs;

internal sealed class BackgroundJobsClient(
    JobStore store, JobRegistry registry, WakeupSignal signal) : IBackgroundJobsClient
{
    public async Task<string> EnqueueAsync(string job, object args, JobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(job);
        if (!registry.Contains(job))
            throw new JobValidationException($"job '{job}' is not registered");
        var opts = options ?? new JobOptions();
        opts.Validate();

        JsonElement argsJson;
        try
        {
            argsJson = JsonSerializer.SerializeToElement(args, Json.Options);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new JobValidationException($"args are not serializable: {e.Message}");
        }

        var id = await store.EnqueueAsync(job, argsJson, opts, cancellationToken)
            .ConfigureAwait(false);
        signal.Pulse(); // in-process wakeup; pg_notify also fired inside the commit
        return id.ToString();
    }

    public async Task<JobDetail> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var detail = Guid.TryParse(jobId, out var id)
            ? await store.GetAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
        return detail ?? throw new JobNotFoundException(jobId);
    }

    public Task<IReadOnlyList<JobSummary>> ListAsync(CancellationToken cancellationToken = default)
        => store.ListAsync(cancellationToken);

    public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(jobId, out var id)) throw new JobNotFoundException(jobId);
        return store.DeleteAsync(id, cancellationToken);
    }

    public Task RequeueAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(jobId, out var id)) throw new JobNotFoundException(jobId);
        return RequeueAndPulse(id, cancellationToken);
    }

    private async Task RequeueAndPulse(Guid id, CancellationToken ct)
    {
        await store.RequeueAsync(id, ct).ConfigureAwait(false);
        signal.Pulse();
    }
}
