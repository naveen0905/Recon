using Polly;
using ReconPlatform.Config.Models;

namespace ReconPlatform.Connectors.Interfaces;

public interface IConnector
{
    string ConnectorType { get; }
    Task<IEnumerable<Dictionary<string, object>>> PullAsync(SourceConfig config, CancellationToken ct);
    Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct);
}

public static class ConnectorPolicy
{
    public static readonly ResiliencePipeline Default = new ResiliencePipelineBuilder()
        .AddRetry(new Polly.Retry.RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            UseJitter = false,
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutException>(),
        })
        .Build();
}
