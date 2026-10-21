using BackgroundJobs.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BackgroundJobs;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job engine, storage and client. The engine starts with the
    /// host and drains in-flight jobs on shutdown without a time bound.
    /// </summary>
    public static JobRegistry AddBackgroundJobs(
        this IServiceCollection services, Action<BackgroundJobsOptions> configure)
    {
        var options = new BackgroundJobsOptions { ConnectionString = "" };
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        if (options.WorkerCount < 1)
            throw new JobValidationException("WorkerCount must be at least 1");

        services.AddSingleton(options);
        services.AddSingleton(new JobStore(options.ConnectionString, options.NotifyChannel));
        services.AddSingleton<JobServer>();
        services.AddSingleton<WakeupSignal>();
        services.AddSingleton<IBackgroundJobsClient, BackgroundJobsClient>();
        services.AddHostedService<JobEngine>();

        // Graceful stop may wait for running job functions without a bound;
        // hosts should also widen HostOptions.ShutdownTimeout.

        var registry = new JobRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
