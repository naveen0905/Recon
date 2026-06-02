using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ReconPlatform.Common.Models;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors;
using ReconPlatform.Connectors.Interfaces;
using ReconPlatform.Engine;
using ReconPlatform.Storage;

namespace ReconPlatform.Workers.ConnectorWorker;

/// <summary>
/// KEDA-triggered BackgroundService that consumes retrigger jobs from Service Bus
/// and executes the full pull pipeline end-to-end:
///   receive message → load config → run connector → normalize → dedup → upsert Cosmos
///   → write Parquet to Blob → log connector_run to SQL
/// </summary>
internal sealed class ConnectorWorkerService : BackgroundService
{
    private readonly ServiceBusClient _serviceBus;
    private readonly string _queueName;
    private readonly SqlMetadataClient _sqlClient;
    private readonly CosmosDbClient _cosmosClient;
    private readonly BlobStorageClient _blobClient;
    private readonly Normalizer _normalizer;
    private readonly DeduplicationEngine _dedupEngine;
    private readonly IReadOnlyDictionary<string, IConnector> _connectors;
    private readonly ILogger<ConnectorWorkerService> _logger;

    public ConnectorWorkerService(
        ServiceBusClient serviceBus,
        IConfiguration configuration,
        SqlMetadataClient sqlClient,
        CosmosDbClient cosmosClient,
        BlobStorageClient blobClient,
        Normalizer normalizer,
        DeduplicationEngine dedupEngine,
        IEnumerable<IConnector> connectors,
        ILogger<ConnectorWorkerService> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceBus);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sqlClient);
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNull(blobClient);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(dedupEngine);
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceBus = serviceBus;
        _queueName = configuration["CONNECTOR_QUEUE_NAME"]
            ?? throw new InvalidOperationException("CONNECTOR_QUEUE_NAME configuration is required.");
        _sqlClient = sqlClient;
        _cosmosClient = cosmosClient;
        _blobClient = blobClient;
        _normalizer = normalizer;
        _dedupEngine = dedupEngine;
        _connectors = connectors.ToDictionary(c => c.ConnectorType, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConnectorWorker started — queue={Queue}", _queueName);

        await using var processor = _serviceBus.CreateProcessor(_queueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 4,
            AutoCompleteMessages = false,
        });

        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("ConnectorWorker stopped.");
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var ct = args.CancellationToken;
        RetriggerMessage? msg = null;

        try
        {
            msg = JsonConvert.DeserializeObject<RetriggerMessage>(args.Message.Body.ToString());
            if (msg is null)
                throw new InvalidOperationException("Failed to deserialize retrigger message.");

            _logger.LogInformation(
                "Processing retrigger: team={Team} source={Source} assetId={AssetId}",
                msg.Team, msg.SourceId, msg.AssetId);

            await RunPipelineAsync(msg, ct).ConfigureAwait(false);
            await args.CompleteMessageAsync(args.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process retrigger message — dead-lettering. team={Team} source={Source}",
                msg?.Team ?? "unknown", msg?.SourceId ?? "unknown");

            await args.DeadLetterMessageAsync(args.Message,
                deadLetterReason: ex.GetType().Name,
                deadLetterErrorDescription: ex.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunPipelineAsync(RetriggerMessage msg, CancellationToken ct)
    {
        // 1. Load team config
        var yaml = await _sqlClient.GetTeamConfigYamlAsync(msg.Team, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No config found for team '{msg.Team}'.");

        var teamConfig = TeamConfigSerializer.Deserialize(yaml);
        var source = teamConfig.Sources.FirstOrDefault(s => s.Id == msg.SourceId)
            ?? throw new InvalidOperationException(
                $"Source '{msg.SourceId}' not found in team config for '{msg.Team}'.");

        // 2. Resolve connector
        if (!_connectors.TryGetValue(source.Type.ToString(), out var connector))
            throw new InvalidOperationException($"No connector registered for type '{source.Type}'.");

        // 3. Pull raw data
        var pulledAt = DateTimeOffset.UtcNow;
        var rows = await connector.PullAsync(source, ct).ConfigureAwait(false);
        var rowList = rows.ToList();

        // 4. Normalize + dedup + upsert
        var assetsPulled = 0;
        var assetsDeduped = 0;

        foreach (var row in rowList)
        {
            ct.ThrowIfCancellationRequested();

            var nullableRow = row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var normalized = _normalizer.Normalize(nullableRow, msg.Team, source, pulledAt, ct);

            var dedupKey = _dedupEngine.ComputeDedupKey(normalized, source.Dedup);
            var assetId = $"{msg.Team}::{dedupKey}";
            normalized = normalized with { AssetId = assetId, DedupKey = dedupKey };

            var existing = await _cosmosClient.GetAssetAsync(assetId, msg.Team, ct).ConfigureAwait(false);

            CanonicalAsset toUpsert;
            if (existing is not null)
            {
                toUpsert = _dedupEngine.Resolve(existing, normalized, source.Dedup);
                assetsDeduped++;
            }
            else
            {
                toUpsert = normalized;
            }

            await _cosmosClient.UpsertAssetAsync(toUpsert, ct).ConfigureAwait(false);
            assetsPulled++;
        }

        // 5. Write Parquet snapshot to Blob
        if (rowList.Count > 0)
        {
            var parquetJson = JsonConvert.SerializeObject(rowList);
            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(parquetJson));
            await _blobClient.UploadPullAsync(msg.Team, source.Id, pulledAt, stream, ct)
                .ConfigureAwait(false);
        }

        // 6. Log connector run (SOC2)
        await _sqlClient.InsertConnectorRunAsync(
            msg.Team, source.Id, assetsPulled, assetsDeduped, "success", null, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Connector run complete: team={Team} source={Source} pulled={Pulled} deduped={Deduped}",
            msg.Team, source.Id, assetsPulled, assetsDeduped);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus processor error: source={Source} entity={Entity}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    private sealed record RetriggerMessage(
        string AssetId,
        string Team,
        string SourceId,
        string DedupKey,
        DateTimeOffset ScheduledAt);
}
