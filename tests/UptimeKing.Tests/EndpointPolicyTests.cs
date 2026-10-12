using System.Net;
using UptimeKing.Api.Endpoints;
using UptimeKing.Api.Monitoring;
using UptimeKing.Api.Notifications;

namespace UptimeKing.Tests;

public class EndpointPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("198.19.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("2001::1")]
    public void Blocks_non_public_addresses(string ip) =>
        Assert.False(PublicEndpointPolicy.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Accepts_public_addresses(string ip) =>
        Assert.True(PublicEndpointPolicy.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://localhost")]
    [InlineData("https://example.com:8443")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/#fragment")]
    [InlineData("http://127.1")]
    [InlineData("http://2130706433")]
    [InlineData("http://[::ffff:127.0.0.1]")]
    public void Rejects_unsafe_urls(string url) =>
        Assert.False(PublicEndpointPolicy.IsValidUrl(url));

    [Fact]
    public void Accepts_public_https_url() =>
        Assert.True(PublicEndpointPolicy.IsValidUrl("https://example.com/health?check=1"));

    [Theory]
    [InlineData("https://discord.com.evil.example/api/webhooks/123/token")]
    [InlineData("https://discord.com/api/webhooks/123/token?redirect=x")]
    [InlineData("http://discord.com/api/webhooks/123/token")]
    public void Rejects_invalid_webhooks(string url) =>
        Assert.False(NotificationValidation.IsDiscordWebhook(url));

    [Fact]
    public void Accepts_exact_discord_webhook() =>
        Assert.True(
            NotificationValidation.IsDiscordWebhook(
                "https://discord.com/api/webhooks/123/abc_DEF-456"
            )
        );

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(3601)]
    public void Rejects_invalid_intervals(int seconds) =>
        Assert.NotNull(
            ManagementEndpoints.Validate(new("Site", "https://example.com", "Client", seconds))
        );
}
