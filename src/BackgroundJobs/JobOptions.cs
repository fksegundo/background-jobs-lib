namespace BackgroundJobs;

/// <summary>Per-job options. Defaults follow the assignment: 3 attempts, 1s fixed delay.</summary>
public sealed record JobOptions
{
    public const int DefaultMaxAttempts = 3;
    public const int DefaultRetryDelayMs = 1000;
    public const int MaxAttemptsLimit = 10;
    public const int MaxRetryDelayMs = 60000;

    /// <summary>UTC instant the job becomes eligible. Null or a past instant = enqueue now.</summary>
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>Total attempts including the first run. 1..10.</summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>Fixed delay between the end of a failed attempt and the next eligibility. 0..60000.</summary>
    public int RetryDelayMs { get; init; } = DefaultRetryDelayMs;

    /// <summary>Throws JobValidationException when an option is out of range.</summary>
    public void Validate()
    {
        if (MaxAttempts < 1 || MaxAttempts > MaxAttemptsLimit)
            throw new JobValidationException($"maxAttempts must be between 1 and {MaxAttemptsLimit}");
        if (RetryDelayMs < 0 || RetryDelayMs > MaxRetryDelayMs)
            throw new JobValidationException($"retryDelayMs must be between 0 and {MaxRetryDelayMs}");
    }
}
