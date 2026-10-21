using BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Library usage without HTTP: register a named job, enqueue it, and let the
// engine execute it in-process. Requires Postgres (docker compose up postgres)
// and ConnectionStrings:Jobs or JOBS_DB env var.

var connectionString =
    Environment.GetEnvironmentVariable("JOBS_DB")
    ?? "Host=localhost;Port=5432;Database=jobs;Username=jobs;Password=jobs";

var builder = Host.CreateApplicationBuilder(args);
var registry = builder.Services.AddBackgroundJobs(o =>
{
    o.ConnectionString = connectionString;
    o.WorkerCount = 2;
});

registry.AddJob<HelloArgs>("hello", (a, ctx) =>
{
    Console.WriteLine($"job {ctx.JobId} attempt {ctx.Attempt}: hello {a.Name}");
    return Task.CompletedTask;
});

var app = builder.Build();
await app.StartAsync();

var client = app.Services.GetRequiredService<IBackgroundJobsClient>();
var jobId = await client.EnqueueAsync("hello", new HelloArgs("world"));
Console.WriteLine($"enqueued {jobId}");

var scheduled = await client.EnqueueAsync("hello", new HelloArgs("in 3s"),
    new JobOptions { RunAt = DateTimeOffset.UtcNow.AddSeconds(3) });
Console.WriteLine($"scheduled {scheduled}");

await Task.Delay(TimeSpan.FromSeconds(6));
await app.StopAsync();

internal sealed record HelloArgs(string Name);
