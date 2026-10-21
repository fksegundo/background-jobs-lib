using System.Text.Json;
using Xunit;

namespace BackgroundJobs.Tests;

public class JobOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void MaxAttempts_out_of_range_throws(int max)
    {
        var o = new JobOptions { MaxAttempts = max };
        Assert.Throws<JobValidationException>(() => o.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void RetryDelayMs_out_of_range_throws(int delay)
    {
        var o = new JobOptions { RetryDelayMs = delay };
        Assert.Throws<JobValidationException>(() => o.Validate());
    }

    [Fact]
    public void Defaults_match_spec()
    {
        var o = new JobOptions();
        Assert.Equal(3, o.MaxAttempts);
        Assert.Equal(1000, o.RetryDelayMs);
        o.Validate();
    }
}

public class JobRegistryTests
{
    [Fact]
    public void Duplicate_name_throws()
    {
        var r = new JobRegistry();
        r.AddJob<object>("j", (_, _) => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(
            () => r.AddJob<object>("j", (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void Resolve_returns_null_for_unknown()
    {
        var r = new JobRegistry();
        Assert.Null(r.Resolve("missing"));
        Assert.False(r.Contains("missing"));
    }

    [Fact]
    public async Task Registered_handler_receives_deserialized_args()
    {
        var r = new JobRegistry();
        (int seen, int attempt) got = (0, 0);
        r.AddJob<Args>("j", (a, ctx) => { got = (a.N, ctx.Attempt); return Task.CompletedTask; });
        var h = r.Resolve("j");
        Assert.NotNull(h);
        await h(JsonSerializer.SerializeToElement(new Args(7), Json.Options),
            new JobContext("id", 2));
        Assert.Equal((7, 2), got);
    }

    private sealed record Args(int N);
}
