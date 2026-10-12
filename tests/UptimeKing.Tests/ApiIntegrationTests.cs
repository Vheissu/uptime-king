using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeKing.Api.Data;
using UptimeKing.Api.Domain;
using UptimeKing.Api.Endpoints;
using UptimeKing.Api.Monitoring;
using UptimeKing.Api.Notifications;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Tests;

public sealed class ApiIntegrationTests(ApiFixture fixture)
    : IClassFixture<ApiFixture>,
        IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
        await db.Deliveries.ExecuteDeleteAsync();
        await db.Monitors.Where(m => !m.IsDemo).ExecuteDeleteAsync();
        await db.Settings.ExecuteDeleteAsync();
        fixture.Factory.Probe.Success = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Public_demo_is_separate_from_private_workspace()
    {
        using var guest = fixture.Factory.CreateClient();
        var demo = await guest.GetFromJsonAsync<JsonElement>("/api/demo");
        Assert.Equal(6, demo.GetProperty("monitors").GetArrayLength());
        Assert.True(demo.GetProperty("isDemo").GetBoolean());
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.GetAsync("/api/dashboard")).StatusCode
        );
        var dashboard = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(0, dashboard.GetProperty("monitors").GetArrayLength());
        await CreateAsync();
        demo = await guest.GetFromJsonAsync<JsonElement>("/api/demo");
        Assert.Equal(6, demo.GetProperty("monitors").GetArrayLength());
    }

    [Fact]
    public async Task Unsafe_methods_require_csrf_header_even_for_sign_in()
    {
        using var guest = fixture.Factory.CreateClient();
        var response = await guest.PostAsJsonAsync(
            "/api/session",
            new { password = ApiFixture.Password }
        );
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        guest.DefaultRequestHeaders.Add("X-UptimeKing", "1");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/api/session", new { password = "wrong" })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await guest.PostAsJsonAsync("/api/monitors", Input())).StatusCode
        );
    }

    [Fact]
    public async Task Monitor_crud_validates_input_and_cannot_modify_demo_rows()
    {
        var bad = await fixture.Admin.PostAsJsonAsync(
            "/api/monitors",
            Input() with
            {
                Url = "http://169.254.169.254/latest/meta-data",
            }
        );
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var id = await CreateAsync();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await fixture.Admin.PutAsJsonAsync(
                    $"/api/monitors/{id}",
                    Input() with
                    {
                        Name = "Edited",
                        IntervalSeconds = 300,
                    }
                )
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (
                await fixture.Admin.PostAsJsonAsync(
                    $"/api/monitors/{id}/pause",
                    new { paused = true }
                )
            ).StatusCode
        );
        var dashboard = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var monitor = dashboard.GetProperty("monitors")[0];
        Assert.Equal("paused", monitor.GetProperty("status").GetString());
        Assert.Equal("Edited", monitor.GetProperty("name").GetString());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
        var demoId = await db.Monitors.Where(m => m.IsDemo).Select(m => m.Id).FirstAsync();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await fixture.Admin.DeleteAsync($"/api/monitors/{demoId}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await fixture.Admin.DeleteAsync($"/api/monitors/{id}")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await fixture.Admin.GetAsync($"/api/monitors/{id}/history")).StatusCode
        );
    }

    [Fact]
    public async Task Failure_and_recovery_are_transactional_and_alert_once_per_transition()
    {
        var id = await CreateAsync();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            db.Settings.Add(
                new()
                {
                    Email = "test@example.com",
                    EmailEnabled = true,
                    DiscordEnabled = true,
                    ProtectedDiscordWebhook = "test-only",
                }
            );
            await db.SaveChangesAsync();
        }
        await RunCheckAsync(id, false);
        using (var scope = fixture.Factory.Services.CreateScope())
            Assert.False(
                await scope
                    .ServiceProvider.GetRequiredService<UptimeDb>()
                    .Incidents.AnyAsync(x => x.MonitorId == id)
            );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            Assert.Single(await db.Incidents.Where(x => x.MonitorId == id).ToListAsync());
            Assert.Equal(2, await db.Deliveries.CountAsync());
            var incident = await db.Incidents.SingleAsync(x => x.MonitorId == id);
            Assert.Equal(
                HttpStatusCode.NoContent,
                (
                    await fixture.Admin.PostAsJsonAsync(
                        $"/api/incidents/{incident.Id}/acknowledge",
                        new { }
                    )
                ).StatusCode
            );
        }
        await RunCheckAsync(id, true);
        await RunCheckAsync(id, true);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            var incident = await db.Incidents.SingleAsync(x => x.MonitorId == id);
            Assert.NotNull(incident.ResolvedAt);
            Assert.NotNull(incident.AcknowledgedAt);
            Assert.Equal(4, await db.Deliveries.CountAsync());
            Assert.Equal(5, await db.Checks.CountAsync(x => x.MonitorId == id));
            Assert.Equal("up", (await db.Monitors.FindAsync(id))!.Status);
        }
    }

    [Fact]
    public async Task Lease_fencing_discards_results_after_pause_or_new_lease()
    {
        var id = await CreateAsync();
        Monitor old;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            old = (await db.Monitors.FindAsync(id))!;
            old.LeaseToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        await fixture.Admin.PostAsJsonAsync($"/api/monitors/{id}/pause", new { paused = true });
        using (var scope = fixture.Factory.Services.CreateScope())
            await scope
                .ServiceProvider.GetRequiredService<MonitorRunner>()
                .RunAsync(old, CancellationToken.None);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            Assert.Equal(0, await db.Checks.CountAsync(x => x.MonitorId == id));
            await db
                .Monitors.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.Paused, false)
                        .SetProperty(x => x.LeaseToken, Guid.NewGuid())
                );
        }
        using (var scope = fixture.Factory.Services.CreateScope())
            await scope
                .ServiceProvider.GetRequiredService<MonitorRunner>()
                .RunAsync(old, CancellationToken.None);
        using (var scope = fixture.Factory.Services.CreateScope())
            Assert.Equal(
                0,
                await scope
                    .ServiceProvider.GetRequiredService<UptimeDb>()
                    .Checks.CountAsync(x => x.MonitorId == id)
            );
    }

    [Fact]
    public async Task Concurrent_completion_of_one_lease_records_one_check()
    {
        var id = await CreateAsync();
        Monitor claimed;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            claimed = (await db.Monitors.FindAsync(id))!;
            claimed.LeaseToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(async _ =>
                {
                    using var scope = fixture.Factory.Services.CreateScope();
                    await scope
                        .ServiceProvider.GetRequiredService<MonitorRunner>()
                        .RunAsync(claimed, CancellationToken.None);
                })
        );
        using var verify = fixture.Factory.Services.CreateScope();
        Assert.Equal(
            1,
            await verify
                .ServiceProvider.GetRequiredService<UptimeDb>()
                .Checks.CountAsync(x => x.MonitorId == id)
        );
    }

    [Fact]
    public async Task Dashboard_computes_actual_uptime_and_retains_incident_history()
    {
        var id = await CreateAsync();
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, true);
        var dashboard = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(33.33, dashboard.GetProperty("summary").GetProperty("uptime").GetDouble());
        Assert.Equal(0, dashboard.GetProperty("summary").GetProperty("openIncidents").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("incidents").GetArrayLength());
        Assert.Equal(3, dashboard.GetProperty("monitors")[0].GetProperty("checks").GetInt32());
        var history = await fixture.Admin.GetFromJsonAsync<JsonElement>(
            $"/api/monitors/{id}/history"
        );
        Assert.Equal(3, history.GetArrayLength());
    }

    [Fact]
    public async Task Notification_secrets_are_encrypted_and_never_returned()
    {
        const string webhook = "https://discord.com/api/webhooks/123/secret_test_token";
        var response = await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("alerts@example.com", true, webhook, true)
        );
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var body = await fixture.Admin.GetStringAsync("/api/notifications");
        Assert.DoesNotContain("secret_test_token", body);
        Assert.True(
            JsonDocument.Parse(body).RootElement.GetProperty("hasDiscordWebhook").GetBoolean()
        );
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
        var settings = await db.Settings.SingleAsync();
        Assert.NotEqual(webhook, settings.ProtectedDiscordWebhook);
        Assert.DoesNotContain("secret_test_token", settings.ProtectedDiscordWebhook);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await fixture.Admin.PutAsJsonAsync(
                    "/api/notifications",
                    new SettingsInput("bad", true, null, false)
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task Email_outbox_is_delivered_to_smtp_and_not_sent_twice()
    {
        var id = await CreateAsync();
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("smoke@example.test", true, null, false)
        );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        for (var n = 0; n < 2; n++)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            await scope
                .ServiceProvider.GetRequiredService<NotificationDispatcher>()
                .DispatchAsync(CancellationToken.None);
        }
        using var verify = fixture.Factory.Services.CreateScope();
        var delivery = await verify
            .ServiceProvider.GetRequiredService<UptimeDb>()
            .Deliveries.SingleAsync();
        Assert.NotNull(delivery.DeliveredAt);
        Assert.Equal(1, delivery.Attempts);
        var messages = await fixture.Inbox.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        Assert.Equal(1, messages.GetProperty("total").GetInt32());
        Assert.Contains(
            "Incident opened",
            messages.GetProperty("messages")[0].GetProperty("Subject").GetString()
        );
    }

    [Fact]
    public async Task Failed_delivery_is_retried_later_without_persisting_secrets()
    {
        var id = await CreateAsync();
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("test@example.test", true, null, false)
        );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            var dispatcher = new NotificationDispatcher(
                db,
                new FailingSender(),
                TimeProvider.System,
                NullLogger<NotificationDispatcher>.Instance
            );
            await dispatcher.DispatchAsync(CancellationToken.None);
        }
        using var verify = fixture.Factory.Services.CreateScope();
        var delivery = await verify
            .ServiceProvider.GetRequiredService<UptimeDb>()
            .Deliveries.SingleAsync();
        Assert.Null(delivery.DeliveredAt);
        Assert.Equal(1, delivery.Attempts);
        Assert.True(delivery.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.DoesNotContain("secret-token", delivery.LastError!);
    }

    [Fact]
    public async Task Deleting_monitor_also_removes_queued_alerts()
    {
        var id = await CreateAsync();
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("test@example.test", true, null, false)
        );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        await fixture.Admin.DeleteAsync($"/api/monitors/{id}");
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
        Assert.Empty(await db.Deliveries.ToListAsync());
        Assert.Empty(await db.Checks.Where(x => x.MonitorId == id).ToListAsync());
        Assert.Empty(await db.Incidents.Where(x => x.MonitorId == id).ToListAsync());
    }

    [Fact]
    public async Task Committed_check_notifies_authenticated_live_dashboard()
    {
        var id = await CreateAsync();
        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/dashboard",
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
                    options.Headers["Cookie"] = fixture.Cookie;
                }
            )
            .Build();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On("dashboardChanged", () => received.TrySetResult());
        await connection.StartAsync();
        await RunCheckAsync(id, true);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dashboard = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(1, dashboard.GetProperty("monitors")[0].GetProperty("checks").GetInt32());
    }

    [Fact]
    public async Task Guest_cannot_connect_to_private_live_dashboard()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/dashboard",
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
                }
            )
            .Build();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private sealed class FailingSender : INotificationSender
    {
        public Task SendAsync(
            NotificationDelivery d,
            NotificationSettings s,
            CancellationToken ct
        ) => throw new HttpRequestException("secret-token");
    }

    private async Task<Guid> CreateAsync()
    {
        var response = await fixture.Admin.PostAsJsonAsync("/api/monitors", Input());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
    }

    private static MonitorInput Input() => new("Test site", "https://example.com", "Test client");

    private async Task RunCheckAsync(Guid id, bool success)
    {
        Monitor claimed;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            claimed = (await db.Monitors.FindAsync(id))!;
            claimed.LeaseToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        fixture.Factory.Probe.Success = success;
        using var checkScope = fixture.Factory.Services.CreateScope();
        await checkScope
            .ServiceProvider.GetRequiredService<MonitorRunner>()
            .RunAsync(claimed, CancellationToken.None);
    }
}
