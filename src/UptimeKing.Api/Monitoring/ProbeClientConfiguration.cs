using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace UptimeKing.Api.Monitoring;

public static class ProbeClientConfiguration
{
    public static IHttpClientBuilder AddProbeClient(this IServiceCollection services)
    {
        var client = services
            .AddHttpClient(
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
            .ConfigurePrimaryHttpMessageHandler(PublicEndpointPolicy.CreateHandler);
        client.AddResilienceHandler(
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
        return client;
    }
}
