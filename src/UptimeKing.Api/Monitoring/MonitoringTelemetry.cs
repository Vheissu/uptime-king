using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UptimeKing.Api.Monitoring;

public static class MonitoringTelemetry
{
    public const string SourceName = "UptimeKing.Monitoring";
    public static readonly ActivitySource Activities = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    public static readonly Counter<long> Checks = Meter.CreateCounter<long>(
        "uptime.checks",
        "{check}",
        "Completed HTTP probes"
    );
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "uptime.check.duration",
        "ms",
        "Probe duration including retries"
    );
}
