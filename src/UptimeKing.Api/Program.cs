using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;
using UptimeKing.Api.Data;
using UptimeKing.Api.Endpoints;
using UptimeKing.Api.Monitoring;
using UptimeKing.Api.Notifications;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddDbContext<UptimeDb>(o =>
    o.UseNpgsql(
        builder.Configuration.GetConnectionString("uptime")
            ?? throw new InvalidOperationException("ConnectionStrings:uptime is required.")
    )
);
builder.Services.AddSingleton(TimeProvider.System);
builder
    .Services.AddDataProtection()
    .SetApplicationName("UptimeKing")
    .PersistKeysToFileSystem(
        new DirectoryInfo(builder.Configuration["DataProtection:Path"] ?? "keys")
    );
builder
    .Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "uptime-king";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.Events.OnRedirectToLogin = c =>
        {
            c.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = c =>
        {
            c.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy(
        "login",
        c =>
            RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                }
            )
    );
});
builder.Services.AddSignalR();
builder
    .Services.AddHttpClient(
        "probe",
        c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "UptimeKing/1.0 (+https://github.com/Vheissu/uptime-king)"
            );
        }
    )
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(PublicEndpointPolicy.CreateHandler)
    .AddResilienceHandler(
        "probe",
        p =>
        {
            p.AddTimeout(TimeSpan.FromSeconds(25));
            p.AddRetry(
                new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.FromMilliseconds(300),
                    UseJitter = true,
                }
            );
            p.AddTimeout(TimeSpan.FromSeconds(10));
        }
    );
builder
    .Services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(15))
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(PublicEndpointPolicy.CreateHandler);
builder.Services.AddScoped<IHttpProbe, HttpProbe>();
builder.Services.AddScoped<MonitorRunner>();
builder.Services.AddScoped<INotificationSender, NotificationSender>();
builder.Services.AddScoped<NotificationDispatcher>();
if (builder.Configuration.GetValue("Monitoring:Enabled", true))
{
    builder.Services.AddHostedService<MonitorWorker>();
    builder.Services.AddHostedService<NotificationWorker>();
    builder.Services.AddHostedService<RetentionWorker>();
}
builder
    .Services.AddOpenTelemetry()
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddSource(MonitoringTelemetry.SourceName);
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            t.AddOtlpExporter();
    });
builder
    .Services.AddOpenTelemetry()
    .WithMetrics(m =>
    {
        m.AddMeter(
            MonitoringTelemetry.SourceName,
            "Microsoft.AspNetCore.Hosting",
            "System.Net.Http"
        );
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            m.AddOtlpExporter();
    });
var password = builder.Configuration["Auth:Password"];
if (!string.IsNullOrEmpty(password) && password.Length < 16)
    throw new InvalidOperationException("Auth:Password must contain at least 16 characters.");
var app = builder.Build();
app.UseExceptionHandler();
app.Use(
    async (context, next) =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "no-store";
        if (
            context.Request.Path.StartsWithSegments("/api")
            && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")
            && context.Request.Headers["X-UptimeKing"] != "1"
        )
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Missing request header." });
            return;
        }
        await next(context);
    }
);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuth();
app.MapDashboard();
app.MapManagement();
app.MapHub<DashboardHub>(
        "/hubs/dashboard",
        options => options.CloseOnAuthenticationExpiration = true
    )
    .RequireAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet(
        "/health/ready",
        async (UptimeDb db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(503)
    )
    .AllowAnonymous();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<UptimeDb>();
    await db.Database.MigrateAsync();
    if (builder.Configuration.GetValue("Demo:Enabled", true))
        await DemoSeeder.SeedAsync(db);
}
await app.RunAsync();

public partial class Program;
