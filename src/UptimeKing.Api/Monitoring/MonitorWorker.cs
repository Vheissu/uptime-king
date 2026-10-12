using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Data;
using UptimeKing.Api.Domain;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Api.Monitoring;

public sealed class MonitorWorker(IServiceScopeFactory scopes, ILogger<MonitorWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
                var now = DateTimeOffset.UtcNow;
                await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);
                var due = await db
                    .Monitors.FromSqlInterpolated(
                        $"""
                        SELECT * FROM "Monitors" WHERE NOT "Paused" AND NOT "IsDemo"
                        AND "NextCheckAt" <= {now} AND ("LeaseUntil" IS NULL OR "LeaseUntil" < {now})
                        ORDER BY "NextCheckAt" LIMIT 8 FOR UPDATE SKIP LOCKED
                        """
                    )
                    .ToListAsync(stoppingToken);
                foreach (var monitor in due)
                {
                    monitor.LeaseToken = Guid.NewGuid();
                    monitor.LeaseUntil = now.AddMinutes(2);
                }
                await db.SaveChangesAsync(stoppingToken);
                await tx.CommitAsync(stoppingToken);
                await Parallel.ForEachAsync(
                    due,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 8,
                        CancellationToken = stoppingToken,
                    },
                    async (monitor, ct) =>
                    {
                        try
                        {
                            using var checkScope = scopes.CreateScope();
                            await checkScope
                                .ServiceProvider.GetRequiredService<MonitorRunner>()
                                .RunAsync(monitor, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Check failed for monitor {MonitorId}", monitor.Id);
                        }
                    }
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitoring scheduler failed; retrying on the next tick");
            }
        }
    }
}

public sealed class MonitorRunner(UptimeDb db, IHttpProbe probe, IHubContext<DashboardHub> hub)
{
    public async Task RunAsync(Monitor claimed, CancellationToken ct)
    {
        var result = await probe.CheckAsync(claimed, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db
            .Monitors.FromSqlInterpolated(
                $"SELECT * FROM \"Monitors\" WHERE \"Id\" = {claimed.Id} FOR UPDATE"
            )
            .SingleOrDefaultAsync(ct);
        // A pause, edit, deletion or newer lease invalidates an in-flight result.
        if (monitor is null || monitor.Paused || monitor.LeaseToken != claimed.LeaseToken)
            return;
        MonitorState.Apply(monitor, result);
        monitor.LeaseToken = null;
        monitor.LeaseUntil = null;
        db.Checks.Add(result);
        var incident = await db.Incidents.SingleOrDefaultAsync(
            x => x.MonitorId == monitor.Id && x.ResolvedAt == null,
            ct
        );
        if (monitor.Status == "down" && incident is null)
        {
            incident = new Incident
            {
                MonitorId = monitor.Id,
                StartedAt = result.CheckedAt,
                Cause = result.Error ?? "Unreachable",
            };
            db.Incidents.Add(incident);
            await QueueAsync(
                incident,
                "down",
                $"{monitor.Name} is down. {incident.Cause}. {monitor.Url}",
                ct
            );
        }
        else if (result.Success && incident is not null)
        {
            incident.ResolvedAt = result.CheckedAt;
            await QueueAsync(
                incident,
                "recovered",
                $"{monitor.Name} is back up after {Math.Ceiling((result.CheckedAt - incident.StartedAt).TotalMinutes)} minutes. {monitor.Url}",
                ct
            );
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await hub.Clients.All.SendAsync("dashboardChanged", ct);
    }

    private async Task QueueAsync(
        Incident incident,
        string kind,
        string message,
        CancellationToken ct
    )
    {
        var settings = await db.Settings.SingleOrDefaultAsync(ct);
        if (settings?.EmailEnabled == true)
            db.Deliveries.Add(
                new()
                {
                    IncidentId = incident.Id,
                    Kind = kind,
                    Channel = "email",
                    Message = message,
                }
            );
        if (settings?.DiscordEnabled == true)
            db.Deliveries.Add(
                new()
                {
                    IncidentId = incident.Id,
                    Kind = kind,
                    Channel = "discord",
                    Message = message,
                }
            );
    }
}
