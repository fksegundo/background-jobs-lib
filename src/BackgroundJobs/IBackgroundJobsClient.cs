namespace BackgroundJobs;

/// <summary>Client-facing API of the library. Usable without HTTP.</summary>
public interface IBackgroundJobsClient
{
    /// <summary>
    /// Enqueue a job for immediate execution, or schedule it when
    /// options.RunAt is in the future. Returns the job id only after the
    /// request is durably persisted.
    /// </summary>
    Task<string> EnqueueAsync(string job, object args, JobOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Full job detail, or throws JobNotFoundException.</summary>
    Task<JobDetail> GetAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Summary of every job.</summary>
    Task<IReadOnlyList<JobSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete a scheduled/enqueued job. 409 semantics via JobConflictException.</summary>
    Task DeleteAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Requeue a succeeded/failed/deleted job with a fresh attempt counter.</summary>
    Task RequeueAsync(string jobId, CancellationToken cancellationToken = default);
}
