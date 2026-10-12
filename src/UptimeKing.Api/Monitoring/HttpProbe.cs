using System.Diagnostics;
using Polly.Timeout;
using UptimeKing.Api.Domain;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Api.Monitoring;

public interface IHttpProbe
{
    Task<CheckResult> CheckAsync(Monitor monitor, CancellationToken ct);
}

public sealed class HttpProbe(IHttpClientFactory clients, TimeProvider time) : IHttpProbe
{
    public async Task<CheckResult> CheckAsync(Monitor monitor, CancellationToken ct)
    {
        using var activity = MonitoringTelemetry.Activities.StartActivity("monitor.check");
        activity?.SetTag("monitor.id", monitor.Id);
        var clock = Stopwatch.StartNew();
        var result = new CheckResult { MonitorId = monitor.Id, CheckedAt = time.GetUtcNow() };
        try
        {
            using var client = clients.CreateClient("probe");
            using var request = new HttpRequestMessage(HttpMethod.Get, monitor.Url);
            // Header-only probing keeps large or streaming response bodies bounded.
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            result.StatusCode = (int)response.StatusCode;
            result.Success = result.StatusCode is >= 200 and < 400;
            if (!result.Success)
                result.Error = $"HTTP {result.StatusCode}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TimeoutRejectedException or TaskCanceledException)
        {
            result.Error = ex is TimeoutRejectedException or TaskCanceledException
                ? "Request timed out"
                : "Connection or TLS failure";
        }
        result.ResponseMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
        activity?.SetTag("http.response.status_code", result.StatusCode);
        activity?.SetStatus(
            result.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
            result.Error
        );
        MonitoringTelemetry.Checks.Add(
            1,
            new KeyValuePair<string, object?>("success", result.Success)
        );
        MonitoringTelemetry.Duration.Record(result.ResponseMs);
        return result;
    }
}
