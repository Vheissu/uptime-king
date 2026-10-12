namespace UptimeKing.Api.Monitoring;

public sealed class MonitorWorker(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<MonitorWorker> logger
) : BackgroundService
{
    private const int Concurrency = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>(Concurrency);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        try
        {
            do
            {
                running.RemoveAll(task => task.IsCompleted);
                if (running.Count == Concurrency)
                    continue;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var claims = await scope
                        .ServiceProvider.GetRequiredService<MonitorClaimStore>()
                        .ClaimAsync(Concurrency - running.Count, stoppingToken);
                    foreach (var monitor in claims)
                        running.Add(CheckAsync(monitor, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Monitoring scheduler failed; retrying on the next tick");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // Keep scopes alive until in-flight checks observe shutdown and finish.
            await Task.WhenAll(running);
        }
    }

    private async Task CheckAsync(Domain.Monitor monitor, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MonitorRunner>().RunAsync(monitor, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Check failed for monitor {MonitorId}", monitor.Id);
        }
    }
}
