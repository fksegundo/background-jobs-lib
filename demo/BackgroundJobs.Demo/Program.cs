using System.Text.Json;
using BackgroundJobs;
using BackgroundJobs.AspNetCore;
using BackgroundJobs.Demo;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var workerCount = int.Parse(Environment.GetEnvironmentVariable("WORKER_COUNT") ?? "4");
var connectionString = builder.Configuration.GetConnectionString("Jobs")
    ?? throw new InvalidOperationException("ConnectionStrings:Jobs is required");

// A graceful stop may wait for running job functions without a time bound.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = Timeout.InfiniteTimeSpan);

var eventLog = new DemoEventLog();
builder.Services.AddSingleton(eventLog);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var registry = builder.Services.AddBackgroundJobs(o =>
{
    o.ConnectionString = connectionString;
    o.WorkerCount = workerCount;
});

// ------------------------------------------------------------- demo handlers

async Task Track(JobContext ctx, Func<Task> work)
{
    eventLog.Started(ctx);
    try
    {
        await work();
        eventLog.Finished(ctx, "ok");
    }
    catch
    {
        eventLog.Finished(ctx, "error");
        throw;
    }
}

registry.AddJob<SucceedAfterArgs>("succeed-after",
    (a, ctx) => Track(ctx, () => Task.Delay(a.DelayMs)));

registry.AddJob<FailNTimesArgs>("fail-n-times", (a, ctx) => Track(ctx, () =>
{
    if (ctx.Attempt <= a.FailTimes) throw new InvalidOperationException("demo failure");
    return Task.CompletedTask;
}));

registry.AddJob<LogMessageArgs>("log-message", (a, ctx) => Track(ctx, () =>
{
    eventLog.Message(ctx, a.Message);
    return Task.CompletedTask;
}));

var app = builder.Build();

// --------------------------------------------------------- POST /demo/jobs

app.MapPost("/demo/jobs", async (HttpRequest req,
    [FromServices] IBackgroundJobsClient client, CancellationToken ct) =>
{
    CreateJobRequest body;
    try
    {
        body = await req.ReadFromJsonAsync<CreateJobRequest>(ct)
               ?? throw new JsonException("empty body");
    }
    catch (Exception e) when (e is JsonException or InvalidOperationException)
    {
        return BadRequest($"invalid request body: {e.Message}");
    }

    if (string.IsNullOrWhiteSpace(body.Job))
        return BadRequest("'job' is required");
    if (!registry.Contains(body.Job))
        return BadRequest($"unknown job '{body.Job}'");
    if (body.Params.ValueKind != JsonValueKind.Object)
        return BadRequest("'params' must be a JSON object");

    var error = ValidateParams(body.Job, body.Params);
    if (error is not null) return BadRequest(error);

    JobOptions options;
    try
    {
        options = ParseOptions(body.Options);
    }
    catch (JobValidationException e)
    {
        return BadRequest(e.Message);
    }

    try
    {
        var id = await client.EnqueueAsync(body.Job, body.Params, options, ct);
        return Results.Json(new { jobId = id }, statusCode: 202);
    }
    catch (JobValidationException e)
    {
        return BadRequest(e.Message);
    }
});

app.MapGet("/demo/events", (DemoEventLog log) =>
    Results.Json(new { items = log.Snapshot() }));

app.MapBackgroundJobsApi();
app.MapJobsDashboard(o => o.EventsUrl = "/demo/events");

app.Run();

// ------------------------------------------------------------- local helpers

static IResult BadRequest(string message)
    => Results.Json(new { error = message }, statusCode: 400);

static string? ValidateParams(string job, JsonElement p) => job switch
{
    "succeed-after" => RequireInt(p, "delayMs", 0, 60000),
    "fail-n-times" => RequireInt(p, "failTimes", 0, 10),
    "log-message" => RequireString(p, "message", 200),
    _ => $"unknown job '{job}'",
};

static string? RequireInt(JsonElement p, string field, int min, int max)
{
    if (!p.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Number
        || !v.TryGetInt32(out var n))
        return $"'{field}' is required and must be an integer";
    return n < min || n > max ? $"'{field}' must be between {min} and {max}" : null;
}

static string? RequireString(JsonElement p, string field, int maxLen)
{
    if (!p.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String)
        return $"'{field}' is required and must be a string";
    return v.GetString()!.Length > maxLen ? $"'{field}' must be at most {maxLen} characters" : null;
}

static JobOptions ParseOptions(JsonElement o)
{
    if (o.ValueKind == JsonValueKind.Undefined || o.ValueKind == JsonValueKind.Null)
        return new JobOptions();
    if (o.ValueKind != JsonValueKind.Object)
        throw new JobValidationException("'options' must be a JSON object");

    DateTimeOffset? runAt = null;
    if (o.TryGetProperty("runAt", out var ra))
    {
        if (ra.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(ra.GetString(), out var parsed))
            throw new JobValidationException("'runAt' must be an ISO-8601 timestamp");
        runAt = parsed.ToUniversalTime();
    }

    var maxAttempts = JobOptions.DefaultMaxAttempts;
    if (o.TryGetProperty("maxAttempts", out var ma))
    {
        if (ma.ValueKind != JsonValueKind.Number || !ma.TryGetInt32(out maxAttempts))
            throw new JobValidationException("'maxAttempts' must be an integer");
    }

    var retryDelayMs = JobOptions.DefaultRetryDelayMs;
    if (o.TryGetProperty("retryDelayMs", out var rd))
    {
        if (rd.ValueKind != JsonValueKind.Number || !rd.TryGetInt32(out retryDelayMs))
            throw new JobValidationException("'retryDelayMs' must be an integer");
    }

    var opts = new JobOptions { RunAt = runAt, MaxAttempts = maxAttempts, RetryDelayMs = retryDelayMs };
    opts.Validate();
    return opts;
}

// -------------------------------------------------------------- request DTOs

public sealed record CreateJobRequest(string Job, JsonElement Params, JsonElement Options);

internal sealed record SucceedAfterArgs(int DelayMs);
internal sealed record FailNTimesArgs(int FailTimes);
internal sealed record LogMessageArgs(string Message);
