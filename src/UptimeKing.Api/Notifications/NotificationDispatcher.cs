using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Data;
using UptimeKing.Api.Domain;

namespace UptimeKing.Api.Notifications;

public sealed class NotificationDispatcher(
    UptimeDb db,
    INotificationSender sender,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger
)
{
    public async Task DispatchAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var deliveries = await db
            .Deliveries.FromSqlInterpolated(
                $"""
                SELECT * FROM "Deliveries" WHERE "DeliveredAt" IS NULL AND "Attempts" < 6
                AND "NextAttemptAt" <= {now} ORDER BY "NextAttemptAt" LIMIT 5 FOR UPDATE SKIP LOCKED
                """
            )
            .ToListAsync(ct);
        var settings = await db.Settings.SingleOrDefaultAsync(ct) ?? new();
        foreach (var delivery in deliveries)
        {
            delivery.Attempts++;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await sender.SendAsync(delivery, settings, timeout.Token);
                delivery.DeliveredAt = time.GetUtcNow();
                delivery.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Do not persist exception messages: webhook tokens can appear in HTTP errors.
                delivery.LastError = "Delivery failed. Check channel configuration.";
                delivery.NextAttemptAt = now.AddSeconds(Math.Pow(2, delivery.Attempts) * 15);
                logger.LogWarning(
                    "Notification {DeliveryId} attempt {Attempt} failed ({ErrorType})",
                    delivery.Id,
                    delivery.Attempts,
                    ex.GetType().Name
                );
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
