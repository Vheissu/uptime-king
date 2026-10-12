using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using UptimeKing.Api.Domain;
using UptimeKing.Api.Monitoring;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder(
        "postgres:17-alpine"
    ).Build();
    private readonly IContainer mail = new ContainerBuilder("axllent/mailpit:v1.27")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025)))
        .Build();
    public HttpClient Inbox { get; private set; } = null!;
    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;
    public string Cookie { get; private set; } = "";
    public const string Password = "integration-test-password-not-real";

    public async Task InitializeAsync()
    {
        await database.StartAsync();
        await mail.StartAsync();
        Inbox = new HttpClient
        {
            BaseAddress = new Uri($"http://{mail.Hostname}:{mail.GetMappedPublicPort(8025)}"),
        };
        Factory = new ApiFactory(
            database.GetConnectionString(),
            mail.Hostname,
            mail.GetMappedPublicPort(1025)
        );
        Admin = Factory.CreateClient();
        Admin.DefaultRequestHeaders.Add("X-UptimeKing", "1");
        var response = await Admin.PostAsJsonAsync("/api/session", new { password = Password });
        response.EnsureSuccessStatusCode();
        Cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    public async Task DisposeAsync()
    {
        Admin.Dispose();
        Inbox.Dispose();
        await Factory.DisposeAsync();
        await database.DisposeAsync();
        await mail.DisposeAsync();
    }
}

public sealed class ApiFactory(string connection, string smtpHost, int smtpPort)
    : WebApplicationFactory<Program>
{
    public readonly FakeProbe Probe = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(
            (_, config) =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:uptime"] = connection,
                        ["Auth:Password"] = ApiFixture.Password,
                        ["Smtp:Host"] = smtpHost,
                        ["Smtp:Port"] = smtpPort.ToString(),
                        ["Smtp:UseTls"] = "false",
                        ["Smtp:From"] = "uptime@localhost",
                        ["Monitoring:Enabled"] = "false",
                        ["Demo:Enabled"] = "true",
                        ["DataProtection:Path"] = Path.Combine(
                            Path.GetTempPath(),
                            "uptime-king-tests",
                            Guid.NewGuid().ToString()
                        ),
                        ["Logging:LogLevel:Default"] = "Warning",
                    }
                )
        );
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpProbe>();
            services.AddSingleton<IHttpProbe>(Probe);
        });
    }
}

public sealed class FakeProbe : IHttpProbe
{
    public bool Success { get; set; } = true;

    public Task<CheckResult> CheckAsync(Monitor monitor, CancellationToken ct) =>
        Task.FromResult(
            new CheckResult
            {
                MonitorId = monitor.Id,
                CheckedAt = DateTimeOffset.UtcNow,
                Success = Success,
                ResponseMs = 120,
                StatusCode = Success ? 200 : 503,
                Error = Success ? null : "HTTP 503",
            }
        );
}
