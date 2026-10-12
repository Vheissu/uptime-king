using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using UptimeKing.Api.Domain;
using UptimeKing.Api.Notifications;

namespace UptimeKing.Tests;

public sealed class DiscordNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discord_delivery_decrypts_destination_and_disables_mentions(bool longMessage)
    {
        using var handler = new RecordingHandler();
        var protection = new EphemeralDataProtectionProvider();
        var sender = new NotificationSender(
            new ClientFactory(handler),
            new ConfigurationBuilder().Build(),
            protection
        );
        const string webhook = "https://discord.com/api/webhooks/123/test_token";
        var message = longMessage ? new string('x', 2200) : "Site down @everyone";
        await sender.SendAsync(
            new NotificationDelivery { Channel = "discord", Message = message },
            new NotificationSettings
            {
                DiscordEnabled = true,
                ProtectedDiscordWebhook = protection
                    .CreateProtector("discord-webhook")
                    .Protect(webhook),
            },
            CancellationToken.None
        );
        Assert.Equal(webhook + "?wait=true", handler.Url);
        Assert.Equal(HttpMethod.Post, handler.Method);
        using var payload = JsonDocument.Parse(handler.Body!);
        var content = payload.RootElement.GetProperty("content").GetString()!;
        Assert.Equal(longMessage ? message[..1999] + "…" : message, content);
        Assert.True(content.Length <= 2000);
        Assert.Empty(
            payload
                .RootElement.GetProperty("allowed_mentions")
                .GetProperty("parse")
                .EnumerateArray()
        );
    }

    [Fact]
    public async Task Disabled_channel_does_not_make_an_http_request()
    {
        using var handler = new RecordingHandler();
        var sender = new NotificationSender(
            new ClientFactory(handler),
            new ConfigurationBuilder().Build(),
            new EphemeralDataProtectionProvider()
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(
                new NotificationDelivery { Channel = "discord", Message = "Site down" },
                new NotificationSettings { DiscordEnabled = false },
                CancellationToken.None
            )
        );
        Assert.Null(handler.Url);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Url = request.RequestUri!.ToString();
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
