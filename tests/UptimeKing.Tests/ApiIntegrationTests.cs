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
        fixture.Factory.Probe.BeforeCheck = null;
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

    [Fact]
    public async Task Open_incidents_remain_visible_after_more_than_100_recoveries()
    {
        var id = await CreateAsync();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            db.Incidents.Add(
                new()
                {
                    MonitorId = id,
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-7),
                    Cause = "Still down",
                }
            );
            db.Incidents.AddRange(
                Enumerable
                    .Range(1, 101)
                    .Select(n => new Incident
                    {
                        MonitorId = id,
                        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-n * 2),
                        ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-n * 2 + 1),
                        Cause = "Recovered",
                    })
            );
            await db.SaveChangesAsync();
        }
        var dashboard = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var incidents = dashboard.GetProperty("incidents").EnumerateArray().ToArray();
        Assert.Equal(101, incidents.Length);
        Assert.Single(incidents, i => i.GetProperty("resolvedAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Concurrent_schedulers_claim_disjoint_monitors_and_reclaim_expired_leases()
    {
        for (var i = 0; i < 10; i++)
            await CreateAsync();
        async Task<IReadOnlyList<Monitor>> Claim()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<MonitorClaimStore>()
                .ClaimAsync(8, CancellationToken.None);
        }
        var claims = (await Task.WhenAll(Claim(), Claim())).SelectMany(x => x).ToList();
        Assert.Equal(10, claims.Count);
        Assert.Equal(10, claims.Select(x => x.Id).Distinct().Count());
        Assert.All(claims, m => Assert.NotNull(m.LeaseToken));
        Assert.Empty(await Claim());
        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<UptimeDb>();
        var previous = claims[0];
        await db
            .Monitors.Where(m => m.Id == previous.Id)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(m => m.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1))
            );
        var reclaimed = Assert.Single(await Claim());
        Assert.Equal(previous.Id, reclaimed.Id);
        Assert.NotEqual(previous.LeaseToken, reclaimed.LeaseToken);
    }

    [Fact]
    public async Task Slow_probe_does_not_block_reuse_of_other_worker_slots()
    {
        for (var i = 0; i < 9; i++)
            await CreateAsync();
        var started = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        fixture.Factory.Probe.BeforeCheck = async (monitor, ct) =>
        {
            started.Writer.TryWrite(monitor.Id);
            if (Interlocked.Increment(ref first) == 1)
                await release.Task.WaitAsync(ct);
        };
        using var worker = new MonitorWorker(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MonitorWorker>.Instance
        );
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ids = new HashSet<Guid>();
            for (var i = 0; i < 9; i++)
                ids.Add(await started.Reader.ReadAsync(timeout.Token));
            Assert.Equal(9, ids.Count);
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            fixture.Factory.Probe.BeforeCheck = null;
        }
    }

    [Fact]
    public async Task Unclaimed_monitor_cannot_commit_a_check()
    {
        var id = await CreateAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var monitor = await scope
            .ServiceProvider.GetRequiredService<UptimeDb>()
            .Monitors.FindAsync(id);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope
                .ServiceProvider.GetRequiredService<MonitorRunner>()
                .RunAsync(monitor!, CancellationToken.None)
        );
    }

    [Fact]
    public async Task Concurrent_initial_notification_settings_are_serialized()
    {
        var responses = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(i =>
                    fixture.Admin.PutAsJsonAsync(
                        "/api/notifications",
                        new SettingsInput($"operator{i}@example.test", false, null, false)
                    )
                )
        );
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.Single(
            await scope.ServiceProvider.GetRequiredService<UptimeDb>().Settings.ToListAsync()
        );
    }

    [Theory]
    [InlineData("a-different-workspace-password")]
    [InlineData(null)]
    public async Task Password_rotation_or_removal_rejects_existing_cookie(string? password)
    {
        var config =
            fixture.Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        try
        {
            config["Auth:Password"] = password;
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", fixture.Cookie);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.GetAsync("/api/dashboard")).StatusCode
            );
        }
        finally
        {
            config["Auth:Password"] = ApiFixture.Password;
        }
    }

    [Fact]
    public async Task Disabled_notification_channel_preserves_pending_attempts()
    {
        var id = await CreateAsync();
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("test@example.test", true, null, false)
        );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("test@example.test", false, null, false)
        );
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
        await new NotificationDispatcher(
            db,
            new FailingSender(),
            TimeProvider.System,
            NullLogger<NotificationDispatcher>.Instance
        ).DispatchAsync(CancellationToken.None);
        Assert.Equal(0, (await db.Deliveries.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Recovery_waits_for_retrying_opening_alert()
    {
        var id = await CreateAsync();
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("test@example.test", true, null, false)
        );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, true);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
        await new NotificationDispatcher(
            db,
            new FailingSender(),
            TimeProvider.System,
            NullLogger<NotificationDispatcher>.Instance
        ).DispatchAsync(CancellationToken.None);
        var deliveries = await db.Deliveries.ToListAsync();
        Assert.Equal(1, deliveries.Single(d => d.Kind == "down").Attempts);
        Assert.Equal(0, deliveries.Single(d => d.Kind == "recovered").Attempts);
    }

    [Fact]
    public async Task Shutdown_during_second_delivery_does_not_rollback_first_delivery()
    {
        var id = await CreateAsync();
        await fixture.Admin.PutAsJsonAsync(
            "/api/notifications",
            new SettingsInput("test@example.test", true, null, false)
        );
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, false);
        await RunCheckAsync(id, true);
        using var cancellation = new CancellationTokenSource();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            var dispatcher = new NotificationDispatcher(
                db,
                new CancelRecoverySender(cancellation),
                TimeProvider.System,
                NullLogger<NotificationDispatcher>.Instance
            );
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                dispatcher.DispatchAsync(cancellation.Token)
            );
        }
        await using var verify = fixture.Factory.Services.CreateAsyncScope();
        var deliveries = await verify
            .ServiceProvider.GetRequiredService<UptimeDb>()
            .Deliveries.ToListAsync();
        Assert.NotNull(deliveries.Single(d => d.Kind == "down").DeliveredAt);
        Assert.Null(deliveries.Single(d => d.Kind == "recovered").DeliveredAt);
        Assert.Equal(0, deliveries.Single(d => d.Kind == "recovered").Attempts);
    }

    [Fact]
    public async Task Dashboard_distinguishes_missing_history_and_24_hour_response_average()
    {
        var id = await CreateAsync();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
            db.Checks.AddRange(
                new CheckResult
                {
                    MonitorId = id,
                    CheckedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    Success = true,
                    ResponseMs = 1000,
                },
                new CheckResult
                {
                    MonitorId = id,
                    CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Success = true,
                    ResponseMs = 100,
                }
            );
            await db.SaveChangesAsync();
        }
        var dashboard = await fixture.Admin.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(550, dashboard.GetProperty("summary").GetProperty("averageMs").GetDouble());
        Assert.Equal(100, dashboard.GetProperty("responseAverageMs").GetDouble());
        var history = dashboard
            .GetProperty("monitors")[0]
            .GetProperty("history")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(25, history.Length);
        Assert.Single(history, bucket => bucket.GetProperty("status").GetString() == "up");
        Assert.Equal(
            24,
            history.Count(bucket =>
                bucket.GetProperty("status").GetString() == "unknown"
                && bucket.GetProperty("averageMs").ValueKind == JsonValueKind.Null
            )
        );
    }

    [Fact]
    public async Task Missing_pause_flag_cannot_silently_resume_a_monitor()
    {
        var id = await CreateAsync();
        await fixture.Admin.PostAsJsonAsync($"/api/monitors/{id}/pause", new { paused = true });
        var response = await fixture.Admin.PostAsJsonAsync($"/api/monitors/{id}/pause", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.True(
            (
                await scope.ServiceProvider.GetRequiredService<UptimeDb>().Monitors.FindAsync(id)
            )!.Paused
        );
    }

    private sealed class CancelRecoverySender(CancellationTokenSource cancellation)
        : INotificationSender
    {
        public Task SendAsync(NotificationDelivery d, NotificationSettings s, CancellationToken ct)
        {
            if (d.Kind == "recovered")
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }
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
