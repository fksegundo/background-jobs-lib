using System.Text.Json;

namespace BackgroundJobs;

/// <summary>List view of a job.</summary>
public sealed record JobSummary(string Id, string Name, string State, int Attempts);

/// <summary>One entry of the state history.</summary>
public sealed record HistoryEntry(string State, DateTimeOffset At, string Reason);

/// <summary>One attempt record. FinishedAt/Outcome are null while the attempt is running.</summary>
public sealed record AttemptRecord(
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Outcome,
    string? Error);

/// <summary>Full job detail as exposed by the management API.</summary>
public sealed record JobDetail(
    string Id,
    string Name,
    JsonElement Args,
    string State,
    int Attempts,
    int MaxAttempts,
    int RetryDelayMs,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HistoryEntry> History,
    IReadOnlyList<AttemptRecord> AttemptHistory);
