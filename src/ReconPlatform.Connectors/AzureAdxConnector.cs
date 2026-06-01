using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;

namespace ReconPlatform.Connectors;

public sealed class AzureAdxConnector : IConnector, IDisposable
{
    public string ConnectorType => "azure_adx";

    private readonly SecretResolver _secretResolver;
    private readonly ILogger<AzureAdxConnector> _logger;

    private ICslQueryProvider? _queryProvider;
    private string? _currentCluster;
    private bool _disposed;

    public AzureAdxConnector(SecretResolver secretResolver, ILogger<AzureAdxConnector> logger)
    {
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public async Task<IEnumerable<Dictionary<string, object>>> PullAsync(
        SourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var provider = GetOrCreateProvider(config);
        var results = new List<Dictionary<string, object>>();

        var clientRequestProps = new ClientRequestProperties
        {
            ClientRequestId = $"recon;{config.Id};{Guid.NewGuid()}"
        };

        using var reader = await Task.Run(
            () => provider.ExecuteQuery(config.Database, config.Query, clientRequestProps),
            ct).ConfigureAwait(false);

        var columnCount = reader.FieldCount;
        var columnNames = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
            columnNames[i] = reader.GetName(i);

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columnCount; i++)
                row[columnNames[i]] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i);
            results.Add(row);
        }

        _logger.LogInformation(
            "AzureAdxConnector pulled {Count} rows from source {SourceId} database {Database}",
            results.Count, config.Id, config.Database);

        return results;
    }

    public async Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var provider = GetOrCreateProvider(config);

            var clientRequestProps = new ClientRequestProperties
            {
                ClientRequestId = $"recon;{config.Id};health;{Guid.NewGuid()}"
            };

            await Task.Run(
                () =>
                {
                    using var reader = provider.ExecuteQuery(
                        config.Database ?? string.Empty,
                        ".show version",
                        clientRequestProps);
                    return reader.Read();
                },
                ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("TestConnectionAsync failed for {SourceId}: {Message}", config.Id, ex.Message);
            return false;
        }
    }

    private ICslQueryProvider GetOrCreateProvider(SourceConfig config)
    {
        if (_queryProvider is not null && _currentCluster == config.Cluster)
            return _queryProvider;

        _queryProvider?.Dispose();

        var kcsb = new KustoConnectionStringBuilder(config.Cluster)
            .WithAadSystemManagedIdentity();

        _queryProvider = KustoClientFactory.CreateCslQueryProvider(kcsb);
        _currentCluster = config.Cluster;
        return _queryProvider;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _queryProvider?.Dispose();
        _disposed = true;
    }
}
