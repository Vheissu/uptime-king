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
        // Commit each delivery separately so a later failure/shutdown cannot roll back earlier sends.
        for (var batch = 0; batch < 5; batch++)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = time.GetUtcNow();
            var delivery = await db
                .Deliveries.FromSqlInterpolated(
                    $"""
                    SELECT d.* FROM "Deliveries" d
                    WHERE d."DeliveredAt" IS NULL AND d."Attempts" < 6 AND d."NextAttemptAt" <= {now}
                    AND EXISTS (
                        SELECT 1 FROM "Settings" s WHERE s."Id" = 1 AND
                        ((d."Channel" = 'email' AND s."EmailEnabled") OR (d."Channel" = 'discord' AND s."DiscordEnabled"))
                    )
                    AND (d."Kind" <> 'recovered' OR NOT EXISTS (
                        SELECT 1 FROM "Deliveries" opening WHERE opening."IncidentId" = d."IncidentId"
                        AND opening."Channel" = d."Channel" AND opening."Kind" = 'down'
                        AND opening."DeliveredAt" IS NULL AND opening."Attempts" < 6
                    ))
                    ORDER BY d."NextAttemptAt", d."Id" LIMIT 1 FOR UPDATE OF d SKIP LOCKED
                    """
                )
                .SingleOrDefaultAsync(ct);
            if (delivery is null)
                return;
            var settings = await db.Settings.SingleAsync(ct);
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
                // Webhook tokens can appear in HTTP exception messages.
                delivery.LastError = "Delivery failed. Check channel configuration.";
                delivery.NextAttemptAt = time.GetUtcNow()
                    .AddSeconds(Math.Pow(2, delivery.Attempts) * 15);
                logger.LogWarning(
                    "Notification {DeliveryId} attempt {Attempt} failed ({ErrorType})",
                    delivery.Id,
                    delivery.Attempts,
                    ex.GetType().Name
                );
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
