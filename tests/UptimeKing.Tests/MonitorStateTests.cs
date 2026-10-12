using UptimeKing.Api.Domain;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Tests;

public class MonitorStateTests
{
    [Fact]
    public void One_failure_is_degraded_two_failures_are_down()
    {
        var monitor = new Monitor();
        MonitorState.Apply(monitor, Result(false));
        Assert.Equal("degraded", monitor.Status);
        MonitorState.Apply(monitor, Result(false));
        Assert.Equal("down", monitor.Status);
        Assert.Equal(2, monitor.ConsecutiveFailures);
    }

    [Fact]
    public void Successful_check_recovers_and_resets_failures()
    {
        var monitor = new Monitor
        {
            Status = "down",
            ConsecutiveFailures = 8,
            IntervalSeconds = 300,
        };
        var result = Result(true);
        MonitorState.Apply(monitor, result);
        Assert.Equal("up", monitor.Status);
        Assert.Equal(0, monitor.ConsecutiveFailures);
        Assert.Equal(result.CheckedAt.AddSeconds(300), monitor.NextCheckAt);
        Assert.Equal(result.CheckedAt, monitor.LastCheckedAt);
    }

    [Fact]
    public void Successful_but_slow_response_is_degraded_without_failure()
    {
        var monitor = new Monitor { SlowThresholdMs = 500 };
        var result = Result(true);
        result.ResponseMs = 500;
        MonitorState.Apply(monitor, result);
        Assert.Equal("degraded", monitor.Status);
        Assert.Equal(0, monitor.ConsecutiveFailures);
    }

    [Fact]
    public void Intermittent_failures_do_not_accumulate()
    {
        var monitor = new Monitor();
        MonitorState.Apply(monitor, Result(false));
        MonitorState.Apply(monitor, Result(true));
        MonitorState.Apply(monitor, Result(false));
        Assert.Equal("degraded", monitor.Status);
        Assert.Equal(1, monitor.ConsecutiveFailures);
    }

    private static CheckResult Result(bool success) =>
        new()
        {
            CheckedAt = DateTimeOffset.UtcNow,
            Success = success,
            ResponseMs = 100,
        };
}
