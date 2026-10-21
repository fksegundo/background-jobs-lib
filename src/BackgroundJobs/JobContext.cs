namespace BackgroundJobs;

/// <summary>Context handed to a job function: the job id and the current attempt number (1-based).</summary>
public sealed record JobContext(string JobId, int Attempt);
