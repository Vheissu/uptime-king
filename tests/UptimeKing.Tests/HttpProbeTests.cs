using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UptimeKing.Api.Monitoring;
using Monitor = UptimeKing.Api.Domain.Monitor;

namespace UptimeKing.Tests;

public class HttpProbeTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(301, true)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    public async Task Classifies_http_status_without_reading_the_body(int status, bool success)
    {
        var handler = new Handler(
            (_, _) =>
                Task.FromResult(
                    new HttpResponseMessage((HttpStatusCode)status)
                    {
                        Content = new StreamContent(new ThrowingStream()),
                    }
                )
        );
        var probe = new HttpProbe(new Factory(handler), TimeProvider.System);
        var result = await probe.CheckAsync(
            new Monitor { Url = "https://example.com" },
            CancellationToken.None
        );
        Assert.Equal(success, result.Success);
        Assert.Equal(status, result.StatusCode);
    }

    [Fact]
    public async Task Shutdown_cancellation_is_not_recorded_as_downtime()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new HttpProbe(
            new Factory(new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct))),
            TimeProvider.System
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.CheckAsync(new Monitor { Url = "https://example.com" }, cancellation.Token)
        );
    }

    [Fact]
    public async Task Connection_errors_are_bounded_and_do_not_leak_urls()
    {
        var probe = new HttpProbe(
            new Factory(
                new Handler(
                    (_, _) => throw new HttpRequestException("https://example.com?secret=oops")
                )
            ),
            TimeProvider.System
        );
        var result = await probe.CheckAsync(
            new Monitor { Url = "https://example.com" },
            CancellationToken.None
        );
        Assert.False(result.Success);
        Assert.Equal("Connection or TLS failure", result.Error);
    }

    [Theory]
    [InlineData(503, 2, true)]
    [InlineData(429, 2, true)]
    [InlineData(404, 1, false)]
    public async Task Configured_Polly_pipeline_retries_only_transient_failures(
        int firstStatus,
        int expectedAttempts,
        bool success
    )
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddProbeClient()
            .ConfigurePrimaryHttpMessageHandler(() =>
                new Handler(
                    (_, _) =>
                        Task.FromResult(
                            new HttpResponseMessage(
                                (HttpStatusCode)(++attempts == 1 ? firstStatus : 200)
                            )
                            {
                                Content = new StreamContent(new ThrowingStream()),
                            }
                        )
                )
            );
        await using var provider = services.BuildServiceProvider();
        var probe = new HttpProbe(
            provider.GetRequiredService<IHttpClientFactory>(),
            TimeProvider.System
        );
        var result = await probe.CheckAsync(
            new Monitor { Url = "https://example.com" },
            CancellationToken.None
        );
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(success, result.Success);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Body should not be read");
    }
}
