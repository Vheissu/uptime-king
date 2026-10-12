using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeKing.Api.Monitoring;

namespace UptimeKing.Tests;

public class DashboardUpdatesTests
{
    [Fact]
    public async Task Broadcast_failure_does_not_fail_an_already_committed_operation()
    {
        var updates = new DashboardUpdates(new FailingHub(), NullLogger<DashboardUpdates>.Instance);
        await updates.PublishAsync(CancellationToken.None);
    }

    private sealed class FailingHub : IHubContext<DashboardHub>
    {
        public IHubClients Clients => throw new InvalidOperationException("Transport unavailable");
        public IGroupManager Groups => throw new NotSupportedException();
    }
}
