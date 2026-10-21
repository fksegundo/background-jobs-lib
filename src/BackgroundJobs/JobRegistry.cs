using System.Text.Json;

namespace BackgroundJobs;

/// <summary>
/// Registry mapping stable job names to typed handlers. Handlers are resolved
/// after restarts by name; the stored args jsonb is deserialized to TArgs.
/// </summary>
public sealed class JobRegistry
{
    private readonly Dictionary<string, Func<JsonElement, JobContext, Task>> _handlers = new();

    /// <summary>Register a handler under a stable name. Duplicate names fail fast.</summary>
    public JobRegistry AddJob<TArgs>(string name, Func<TArgs, JobContext, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(name, (args, ctx) => handler(Deserialize<TArgs>(args), ctx)))
            throw new InvalidOperationException($"job '{name}' is already registered");
        return this;
    }

    public bool Contains(string name) => _handlers.ContainsKey(name);

    /// <summary>Resolves a handler by name or returns null (missing registration after restart).</summary>
    internal Func<JsonElement, JobContext, Task>? Resolve(string name)
        => _handlers.TryGetValue(name, out var h) ? h : null;

    private static TArgs Deserialize<TArgs>(JsonElement args)
    {
        try
        {
            return args.Deserialize<TArgs>(Json.Options)
                   ?? throw new JsonException("args deserialized to null");
        }
        catch (JsonException e)
        {
            throw new JobValidationException($"invalid args: {e.Message}");
        }
    }
}
