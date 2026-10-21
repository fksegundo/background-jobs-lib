using System.Text.Json;

namespace BackgroundJobs.Internal;

/// <summary>A job row claimed for execution by a worker.</summary>
internal sealed record JobClaim(
    Guid JobId,
    string Name,
    JsonElement Args,
    int Generation,
    int Attempt,
    int MaxAttempts,
    int RetryDelayMs);

/// <summary>Internal lifecycle status of the engine, backing GET /health.</summary>
internal enum ServerStatus
{
    Initializing,
    Running,
    Stopping,
    LostOwnership,
    Stopped,
}

internal sealed class JobServer
{
    public ServerStatus Status { get; set; } = ServerStatus.Initializing;
}
