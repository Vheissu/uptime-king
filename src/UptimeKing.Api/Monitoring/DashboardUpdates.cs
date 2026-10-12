using Microsoft.AspNetCore.SignalR;

namespace UptimeKing.Api.Monitoring;

// A refresh hint is best effort. A failed broadcast must not turn a committed write into a 500.
public sealed class DashboardUpdates(
    IHubContext<DashboardHub> hub,
    ILogger<DashboardUpdates> logger
)
{
    public async Task PublishAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await hub.Clients.All.SendAsync("dashboardChanged", timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Dashboard refresh notification failed ({ErrorType}); clients will poll",
                ex.GetType().Name
            );
        }
    }
}
