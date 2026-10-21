using System.Text.Json;
using BackgroundJobs.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundJobs.AspNetCore;

/// <summary>HTTP management API of the library: /api/jobs and /health.</summary>
public static class BackgroundJobsEndpoints
{
    public static IEndpointRouteBuilder MapBackgroundJobsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", async (IBackgroundJobsClient client, CancellationToken ct) =>
            Results.Json(new { items = await client.ListAsync(ct) }, JsonOptions()));

        app.MapGet("/api/jobs/{id}", async (string id, IBackgroundJobsClient client, CancellationToken ct) =>
            await Guard(async () => Results.Json(await client.GetAsync(id, ct), JsonOptions())));

        app.MapDelete("/api/jobs/{id}", async (string id, IBackgroundJobsClient client, CancellationToken ct) =>
            await Guard(async () =>
            {
                await client.DeleteAsync(id, ct);
                return Results.Json(new { id, state = "deleted" }, JsonOptions());
            }));

        app.MapPost("/api/jobs/{id}/requeue", async (string id, IBackgroundJobsClient client, CancellationToken ct) =>
            await Guard(async () =>
            {
                await client.RequeueAsync(id, ct);
                return Results.Json(new { jobId = id }, JsonOptions(), statusCode: 202);
            }));

        app.MapGet("/health", async (HttpContext ctx) =>
        {
            var server = ctx.RequestServices.GetRequiredService<JobServer>();
            var store = ctx.RequestServices.GetRequiredService<JobStore>();
            var ok = server.Status == ServerStatus.Running
                     && await store.PingAsync(ctx.RequestAborted);
            return Results.Json(new { status = ok ? "ok" : "unavailable" },
                JsonOptions(), statusCode: ok ? 200 : 503);
        });

        return app;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return o;
    }

    /// <summary>Maps library exceptions to the error contract { "error": "..." }.</summary>
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (JobNotFoundException e)
        {
            return Error(e.Message, 404);
        }
        catch (JobConflictException e)
        {
            return Error(e.Message, 409);
        }
        catch (JobValidationException e)
        {
            return Error(e.Message, 400);
        }
    }

    private static IResult Error(string message, int statusCode)
        => Results.Json(new { error = message }, JsonOptions(), statusCode: statusCode);
}
