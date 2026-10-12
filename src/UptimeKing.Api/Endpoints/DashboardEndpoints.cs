using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Data;

namespace UptimeKing.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet(
                "/api/dashboard",
                (UptimeDb db, CancellationToken ct) => ReadAsync(db, false, ct)
            )
            .RequireAuthorization();
        app.MapGet("/api/demo", (UptimeDb db, CancellationToken ct) => ReadAsync(db, true, ct))
            .AllowAnonymous();
        app.MapGet(
                "/api/monitors/{id:guid}/history",
                async (Guid id, UptimeDb db, CancellationToken ct) =>
                {
                    if (!await db.Monitors.AnyAsync(x => x.Id == id && !x.IsDemo, ct))
                        return Results.NotFound();
                    return Results.Ok(
                        await db
                            .Checks.Where(x => x.MonitorId == id)
                            .OrderByDescending(x => x.CheckedAt)
                            .Take(100)
                            .Select(x => new
                            {
                                x.Id,
                                x.CheckedAt,
                                x.Success,
                                x.StatusCode,
                                x.ResponseMs,
                                x.Error,
                            })
                            .ToListAsync(ct)
                    );
                }
            )
            .RequireAuthorization();
    }

    public static async Task<IResult> ReadAsync(UptimeDb db, bool demo, CancellationToken ct)
    {
        var monitors = await db
            .Monitors.AsNoTracking()
            .Where(x => x.IsDemo == demo)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        var now =
            demo && monitors.Count > 0
                ? monitors.Max(x => x.LastCheckedAt) ?? DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-30);
        var day = now.AddHours(-24);
        var stats = await db
            .Checks.Where(x => x.Monitor.IsDemo == demo && x.CheckedAt >= cutoff)
            .GroupBy(x => x.MonitorId)
            .Select(g => new
            {
                Id = g.Key,
                Total = g.Count(),
                Healthy = g.Count(x => x.Success),
                AverageMs = g.Average(x => x.ResponseMs),
            })
            .ToDictionaryAsync(x => x.Id, ct);
        var buckets = await db
            .Checks.Where(x => x.Monitor.IsDemo == demo && x.CheckedAt >= day)
            .GroupBy(x => new
            {
                x.MonitorId,
                x.CheckedAt.Year,
                x.CheckedAt.Month,
                x.CheckedAt.Day,
                x.CheckedAt.Hour,
            })
            .Select(g => new
            {
                Id = g.Key.MonitorId,
                At = g.Min(x => x.CheckedAt),
                Total = g.Count(),
                Healthy = g.Count(x => x.Success),
                AverageMs = g.Average(x => x.ResponseMs),
            })
            .ToListAsync(ct);
        var incidents = await db
            .Incidents.AsNoTracking()
            .Where(x => x.Monitor.IsDemo == demo)
            .OrderByDescending(x => x.StartedAt)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.MonitorId,
                MonitorName = x.Monitor.Name,
                Client = x.Monitor.Client,
                x.StartedAt,
                x.ResolvedAt,
                x.AcknowledgedAt,
                x.Cause,
            })
            .ToListAsync(ct);
        return Results.Ok(
            new
            {
                IsDemo = demo,
                AsOf = now,
                Monitors = monitors.Select(m => new
                {
                    m.Id,
                    m.Name,
                    m.Url,
                    m.Client,
                    m.IntervalSeconds,
                    m.SlowThresholdMs,
                    m.Paused,
                    Status = m.Paused ? "paused" : m.Status,
                    m.LastCheckedAt,
                    m.LastResponseMs,
                    Uptime = stats.TryGetValue(m.Id, out var s)
                        ? (double?)Math.Round(100d * s.Healthy / s.Total, 2)
                        : null,
                    Checks = stats.GetValueOrDefault(m.Id)?.Total ?? 0,
                    History = buckets
                        .Where(b => b.Id == m.Id)
                        .OrderBy(b => b.At)
                        .Select(b => new
                        {
                            b.At,
                            b.AverageMs,
                            Status = b.Healthy == b.Total ? "up"
                            : b.Healthy == 0 ? "down"
                            : "degraded",
                        }),
                }),
                Summary = new
                {
                    Total = monitors.Count,
                    Up = monitors.Count(m => !m.Paused && m.Status == "up"),
                    Uptime = stats.Count > 0
                        ? (double?)
                            Math.Round(
                                100d
                                    * stats.Values.Sum(s => s.Healthy)
                                    / stats.Values.Sum(s => s.Total),
                                2
                            )
                        : null,
                    AverageMs = stats.Count > 0
                        ? (double?)
                            Math.Round(
                                stats.Values.Sum(s => s.AverageMs * s.Total)
                                    / stats.Values.Sum(s => s.Total)
                            )
                        : null,
                    OpenIncidents = await db.Incidents.CountAsync(
                        x => x.Monitor.IsDemo == demo && x.ResolvedAt == null,
                        ct
                    ),
                },
                Incidents = incidents,
                ResponseHistory = buckets
                    .GroupBy(b => b.At.UtcDateTime.Date.AddHours(b.At.Hour))
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        At = DateTime.SpecifyKind(g.Key, DateTimeKind.Utc),
                        AverageMs = Math.Round(
                            g.Sum(b => b.AverageMs * b.Total) / g.Sum(b => b.Total)
                        ),
                    }),
            }
        );
    }
}
