using System.Net.Mail;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UptimeKing.Api.Data;
using UptimeKing.Api.Domain;
using UptimeKing.Api.Monitoring;
using UptimeKing.Api.Notifications;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Api.Endpoints;

public record MonitorInput(
    string Name,
    string Url,
    string Client,
    int IntervalSeconds = 60,
    int SlowThresholdMs = 1500
);

public record SettingsInput(
    string Email,
    bool EmailEnabled,
    string? DiscordWebhook,
    bool DiscordEnabled
);

public record PauseInput([property: JsonRequired] bool Paused);

public static class ManagementEndpoints
{
    public static string? Validate(MonitorInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 100)
            return "Enter a monitor name of 1–100 characters.";
        if (string.IsNullOrWhiteSpace(i.Client) || i.Client.Length > 100)
            return "Enter a client name of 1–100 characters.";
        if (i.Url is null || i.Url.Length > 2048 || !PublicEndpointPolicy.IsValidUrl(i.Url))
            return "Use a public HTTP or HTTPS URL on its standard port, without credentials or fragments.";
        if (i.IntervalSeconds is < 30 or > 3600)
            return "Check interval must be between 30 and 3,600 seconds.";
        if (i.SlowThresholdMs is < 100 or > 10000)
            return "Slow response threshold must be between 100 and 10,000 ms.";
        return null;
    }

    public static void MapManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();
        group.MapPost(
            "/monitors",
            async (MonitorInput i, UptimeDb db, DashboardUpdates updates, CancellationToken ct) =>
            {
                if (Validate(i) is { } error)
                    return Results.BadRequest(new { error });
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(817240)", ct);
                if (await db.Monitors.CountAsync(x => !x.IsDemo, ct) >= 100)
                    return Results.BadRequest(
                        new { error = "This workspace supports up to 100 monitors." }
                    );
                var m = new Monitor
                {
                    Name = i.Name.Trim(),
                    Url = i.Url.Trim(),
                    Client = i.Client.Trim(),
                    IntervalSeconds = i.IntervalSeconds,
                    SlowThresholdMs = i.SlowThresholdMs,
                };
                db.Monitors.Add(m);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                await updates.PublishAsync(ct);
                return Results.Created($"/api/monitors/{m.Id}", new { m.Id });
            }
        );
        group.MapPut(
            "/monitors/{id:guid}",
            async (
                Guid id,
                MonitorInput i,
                UptimeDb db,
                DashboardUpdates updates,
                CancellationToken ct
            ) =>
            {
                if (Validate(i) is { } error)
                    return Results.BadRequest(new { error });
                var changed = await db
                    .Monitors.Where(x => x.Id == id && !x.IsDemo)
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(x => x.Name, i.Name.Trim())
                                .SetProperty(x => x.Url, i.Url.Trim())
                                .SetProperty(x => x.Client, i.Client.Trim())
                                .SetProperty(x => x.IntervalSeconds, i.IntervalSeconds)
                                .SetProperty(x => x.SlowThresholdMs, i.SlowThresholdMs)
                                .SetProperty(x => x.NextCheckAt, DateTimeOffset.UtcNow)
                                .SetProperty(x => x.LeaseToken, (Guid?)null)
                                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null),
                        ct
                    );
                if (changed == 0)
                    return Results.NotFound();
                await updates.PublishAsync(ct);
                return Results.NoContent();
            }
        );
        group.MapPost(
            "/monitors/{id:guid}/pause",
            async (
                Guid id,
                PauseInput i,
                UptimeDb db,
                DashboardUpdates updates,
                CancellationToken ct
            ) =>
            {
                var changed = await db
                    .Monitors.Where(x => x.Id == id && !x.IsDemo)
                    .ExecuteUpdateAsync(
                        s =>
                            s.SetProperty(x => x.Paused, i.Paused)
                                .SetProperty(x => x.NextCheckAt, DateTimeOffset.UtcNow)
                                .SetProperty(x => x.LeaseToken, (Guid?)null)
                                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null),
                        ct
                    );
                if (changed == 0)
                    return Results.NotFound();
                await updates.PublishAsync(ct);
                return Results.NoContent();
            }
        );
        group.MapDelete(
            "/monitors/{id:guid}",
            async (Guid id, UptimeDb db, DashboardUpdates updates, CancellationToken ct) =>
            {
                if (
                    await db.Monitors.Where(x => x.Id == id && !x.IsDemo).ExecuteDeleteAsync(ct)
                    == 0
                )
                    return Results.NotFound();
                await updates.PublishAsync(ct);
                return Results.NoContent();
            }
        );
        group.MapPost(
            "/incidents/{id:guid}/acknowledge",
            async (Guid id, UptimeDb db, DashboardUpdates updates, CancellationToken ct) =>
            {
                var count = await db
                    .Incidents.Where(x =>
                        x.Id == id
                        && !x.Monitor.IsDemo
                        && x.ResolvedAt == null
                        && x.AcknowledgedAt == null
                    )
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.AcknowledgedAt, DateTimeOffset.UtcNow),
                        ct
                    );
                if (count == 0)
                    return Results.NotFound();
                await updates.PublishAsync(ct);
                return Results.NoContent();
            }
        );
        group.MapGet(
            "/notifications",
            async (UptimeDb db, IConfiguration config, CancellationToken ct) =>
            {
                var s = await db.Settings.SingleOrDefaultAsync(ct) ?? new();
                var deliveries = await db
                    .Deliveries.OrderByDescending(x => x.NextAttemptAt)
                    .Take(30)
                    .Select(x => new
                    {
                        x.Id,
                        x.Channel,
                        x.Kind,
                        x.Attempts,
                        x.DeliveredAt,
                        x.LastError,
                        x.Message,
                    })
                    .ToListAsync(ct);
                return Results.Ok(
                    new
                    {
                        s.Email,
                        s.EmailEnabled,
                        s.DiscordEnabled,
                        HasDiscordWebhook = s.ProtectedDiscordWebhook.Length > 0,
                        SmtpConfigured = !string.IsNullOrEmpty(config["Smtp:Host"]),
                        Deliveries = deliveries,
                    }
                );
            }
        );
        group.MapPut(
            "/notifications",
            async (
                SettingsInput i,
                UptimeDb db,
                IDataProtectionProvider protection,
                CancellationToken ct
            ) =>
            {
                if (
                    i.Email is null
                    || i.Email.Length > 254
                    || i.EmailEnabled && !MailAddress.TryCreate(i.Email, out _)
                )
                    return Results.BadRequest(new { error = "Enter a valid email address." });
                if (
                    !string.IsNullOrEmpty(i.DiscordWebhook)
                    && !NotificationValidation.IsDiscordWebhook(i.DiscordWebhook)
                )
                    return Results.BadRequest(new { error = "Enter a discord.com webhook URL." });
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(817241)", ct);
                var s = await db.Settings.SingleOrDefaultAsync(ct);
                if (s is null)
                {
                    s = new();
                    db.Settings.Add(s);
                }
                if (i.DiscordWebhook is { Length: > 0 })
                    s.ProtectedDiscordWebhook = protection
                        .CreateProtector("discord-webhook")
                        .Protect(i.DiscordWebhook);
                if (i.DiscordEnabled && s.ProtectedDiscordWebhook.Length == 0)
                    return Results.BadRequest(new { error = "Add a Discord webhook first." });
                s.Email = i.Email.Trim();
                s.EmailEnabled = i.EmailEnabled;
                s.DiscordEnabled = i.DiscordEnabled;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.NoContent();
            }
        );
    }
}
