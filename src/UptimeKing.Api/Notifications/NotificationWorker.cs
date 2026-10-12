using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using UptimeKing.Api.Data;
using UptimeKing.Api.Domain;

namespace UptimeKing.Api.Notifications;

public static class NotificationValidation
{
    public static bool IsDiscordWebhook(string value) =>
        value.Length <= 2048
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https"
        && uri.Host == "discord.com"
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && System.Text.RegularExpressions.Regex.IsMatch(
            uri.AbsolutePath,
            @"^/api/webhooks/\d+/[A-Za-z0-9_-]+$"
        );
}

public interface INotificationSender
{
    Task SendAsync(
        NotificationDelivery delivery,
        NotificationSettings settings,
        CancellationToken ct
    );
}

public sealed class NotificationSender(
    IHttpClientFactory clients,
    IConfiguration config,
    IDataProtectionProvider protection
) : INotificationSender
{
    public async Task SendAsync(
        NotificationDelivery delivery,
        NotificationSettings settings,
        CancellationToken ct
    )
    {
        if (delivery.Channel == "discord")
        {
            if (!settings.DiscordEnabled)
                throw new InvalidOperationException("Channel disabled");
            var url = protection
                .CreateProtector("discord-webhook")
                .Unprotect(settings.ProtectedDiscordWebhook);
            if (!NotificationValidation.IsDiscordWebhook(url))
                throw new InvalidOperationException("Invalid Discord webhook");
            using var client = clients.CreateClient("notifications");
            using var response = await client.PostAsJsonAsync(
                url + "?wait=true",
                new
                {
                    content = DiscordContent(delivery.Message),
                    allowed_mentions = new { parse = Array.Empty<string>() },
                },
                ct
            );
            response.EnsureSuccessStatusCode();
            return;
        }
        if (delivery.Channel != "email")
            throw new InvalidOperationException("Unknown notification channel");
        if (!settings.EmailEnabled)
            throw new InvalidOperationException("Channel disabled");
        using var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(config["Smtp:From"] ?? "uptime@localhost"));
        mail.To.Add(MailboxAddress.Parse(settings.Email));
        mail.Subject =
            $"Uptime King · {(delivery.Kind == "down" ? "Incident opened" : "Service recovered")}";
        mail.Body = new TextPart("plain") { Text = delivery.Message };
        using var smtp = new SmtpClient { Timeout = 15000 };
        var security = config.GetValue("Smtp:UseTls", true)
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;
        await smtp.ConnectAsync(
            config["Smtp:Host"] ?? "localhost",
            config.GetValue("Smtp:Port", 1025),
            security,
            ct
        );
        if (!string.IsNullOrEmpty(config["Smtp:Username"]))
            await smtp.AuthenticateAsync(
                config["Smtp:Username"]!,
                config["Smtp:Password"] ?? "",
                ct
            );
        await smtp.SendAsync(mail, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    // Discord caps content at 2,000 characters. Preserve a valid UTF-16 boundary.
    private static string DiscordContent(string message)
    {
        if (message.Length <= 2000)
            return message;
        var length = char.IsHighSurrogate(message[1998]) ? 1998 : 1999;
        return message[..length] + "…";
    }
}

public sealed class NotificationWorker(
    IServiceScopeFactory scopes,
    ILogger<NotificationWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope
                    .ServiceProvider.GetRequiredService<NotificationDispatcher>()
                    .DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification dispatcher failed");
            }
        }
    }
}
