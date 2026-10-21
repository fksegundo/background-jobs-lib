using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BackgroundJobs.AspNetCore;

/// <summary>
/// Embedded jobs dashboard served by the host app at /jobs/dashboard.
/// Consumes the management API and a configurable observable-events feed.
/// </summary>
public static class DashboardExtensions
{
    public sealed class DashboardOptions
    {
        /// <summary>URL the panel polls for observable app events (e.g. /demo/events).</summary>
        public string EventsUrl { get; set; } = "/demo/events";
    }

    public static IEndpointRouteBuilder MapJobsDashboard(
        this IEndpointRouteBuilder app, Action<DashboardOptions>? configure = null)
    {
        var options = new DashboardOptions();
        configure?.Invoke(options);

        app.MapGet("/jobs/dashboard", () =>
        {
            var asm = typeof(DashboardExtensions).Assembly;
            using var stream = asm.GetManifestResourceStream(
                "BackgroundJobs.AspNetCore.Dashboard.index.html")!;
            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd().Replace("__EVENTS_URL__", options.EventsUrl);
            return Results.Text(html, "text/html");
        });

        return app;
    }
}
