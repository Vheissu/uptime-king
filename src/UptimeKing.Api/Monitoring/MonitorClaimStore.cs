using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Data;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Api.Monitoring;

public sealed class MonitorClaimStore(UptimeDb db, TimeProvider time)
{
    public async Task<IReadOnlyList<Monitor>> ClaimAsync(int capacity, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 8);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var due = await db
            .Monitors.FromSqlInterpolated(
                $"""
                SELECT * FROM "Monitors" WHERE NOT "Paused" AND NOT "IsDemo"
                AND "NextCheckAt" <= {now} AND ("LeaseUntil" IS NULL OR "LeaseUntil" < {now})
                ORDER BY "NextCheckAt", "Id" LIMIT {capacity} FOR UPDATE SKIP LOCKED
                """
            )
            .ToListAsync(ct);
        foreach (var monitor in due)
        {
            monitor.LeaseToken = Guid.NewGuid();
            monitor.LeaseUntil = now.AddMinutes(2);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return due;
    }
}
