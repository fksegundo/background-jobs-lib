namespace BackgroundJobs;

/// <summary>Base type for errors raised by the library.</summary>
public class JobException : Exception
{
    public JobException(string message) : base(message) { }
}

/// <summary>Invalid input (unknown job name, bad options, unserializable args). Maps to HTTP 400.</summary>
public sealed class JobValidationException : JobException
{
    public JobValidationException(string message) : base(message) { }
}

/// <summary>Job id does not exist. Maps to HTTP 404.</summary>
public sealed class JobNotFoundException : JobException
{
    public JobNotFoundException(string id) : base($"job '{id}' does not exist") { }
}

/// <summary>Operation incompatible with the job's current state. Maps to HTTP 409.</summary>
public sealed class JobConflictException : JobException
{
    public JobConflictException(string message) : base(message) { }
}
