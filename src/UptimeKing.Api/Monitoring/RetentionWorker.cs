using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Data;

namespace UptimeKing.Api.Monitoring;

public sealed class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
                var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
                await db
                    .Checks.Where(x => x.CheckedAt < cutoff && !x.Monitor.IsDemo)
                    .ExecuteDeleteAsync(stoppingToken);
                await db
                    .Deliveries.Where(x => x.DeliveredAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "History retention failed");
            }
        }
    }
}
