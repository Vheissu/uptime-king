namespace UptimeKing.Api.Domain;

public sealed class Monitor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Client { get; set; } = "";
    public int IntervalSeconds { get; set; } = 60;
    public int SlowThresholdMs { get; set; } = 1500;
    public bool Paused { get; set; }
    public bool IsDemo { get; set; }
    public string Status { get; set; } = "pending";
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public double? LastResponseMs { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CheckResult
{
    public long Id { get; set; }
    public Guid MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public DateTimeOffset CheckedAt { get; set; }
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public double ResponseMs { get; set; }
    public string? Error { get; set; }
}

public sealed class Incident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string Cause { get; set; } = "";
}

public sealed class NotificationSettings
{
    public int Id { get; set; } = 1;
    public string Email { get; set; } = "";
    public string ProtectedDiscordWebhook { get; set; } = "";
    public bool EmailEnabled { get; set; }
    public bool DiscordEnabled { get; set; }
}

public sealed class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Message { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
}

public static class MonitorState
{
    public const int FailureThreshold = 2;

    public static void Apply(Monitor monitor, CheckResult result)
    {
        monitor.LastCheckedAt = result.CheckedAt;
        monitor.LastResponseMs = result.ResponseMs;
        monitor.ConsecutiveFailures = result.Success ? 0 : monitor.ConsecutiveFailures + 1;
        monitor.Status = result.Success
            ? result.ResponseMs >= monitor.SlowThresholdMs
                ? "degraded"
                : "up"
            : monitor.ConsecutiveFailures >= FailureThreshold
                ? "down"
                : "degraded";
        monitor.NextCheckAt = result.CheckedAt.AddSeconds(monitor.IntervalSeconds);
    }
}
