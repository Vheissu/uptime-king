using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Domain;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Api.Data;

public static class DemoSeeder
{
    public static async Task SeedAsync(UptimeDb db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(817239)");
        if (await db.Monitors.AnyAsync(x => x.IsDemo))
            return;
        var now = DateTimeOffset.UtcNow;
        string[] names =
        [
            "Northline Store",
            "Fieldwork Studio",
            "Studio June",
            "Meridian API",
            "Goodkind Coffee",
            "Northline Journal",
        ];
        string[] clients =
        [
            "Northline",
            "Fieldwork",
            "Studio June",
            "Meridian",
            "Goodkind",
            "Northline",
        ];
        string[] urls =
        [
            "shop.northline.example",
            "fieldwork.example",
            "studiojune.example",
            "api.meridian.example",
            "goodkind.example",
            "journal.northline.example",
        ];
        int[] baselines = [240, 186, 203, 126, 319, 174];
        for (var i = 0; i < names.Length; i++)
        {
            var monitor = new Monitor
            {
                Name = names[i],
                Client = clients[i],
                Url = $"https://{urls[i]}",
                IsDemo = true,
                CreatedAt = now.AddDays(-30),
            };
            db.Monitors.Add(monitor);
            for (var n = 0; n < 144; n++)
            {
                var failed = i == 0 && n >= 141 || i == 3 && n is >= 64 and <= 66;
                var check = new CheckResult
                {
                    MonitorId = monitor.Id,
                    CheckedAt = now.AddMinutes((n - 143) * 10),
                    Success = !failed,
                    StatusCode = failed ? 503 : 200,
                    ResponseMs = failed
                        ? 1024
                        : baselines[i] + Math.Sin(n * 1.3 + i) * 27 + Math.Cos(n * .35) * 18,
                    Error = failed ? "HTTP 503" : null,
                };
                db.Checks.Add(check);
                MonitorState.Apply(monitor, check);
            }
            if (i == 0)
                db.Incidents.Add(
                    new()
                    {
                        MonitorId = monitor.Id,
                        StartedAt = now.AddMinutes(-20),
                        Cause = "HTTP 503 · Service unavailable",
                    }
                );
            if (i == 3)
                db.Incidents.Add(
                    new()
                    {
                        MonitorId = monitor.Id,
                        StartedAt = now.AddHours(-13),
                        ResolvedAt = now.AddHours(-12.5),
                        Cause = "HTTP 503 · Service unavailable",
                    }
                );
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
