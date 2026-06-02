using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ReconPlatform.Common.Models;

namespace ReconPlatform.Engine;

/// <summary>
/// Schedules asset retrigger requests by publishing Service Bus messages to a
/// configured queue. Each message carries enough identity information for a
/// downstream worker to locate and re-pull the asset.
/// </summary>
public sealed class RetriggerOrchestrator
{
    private const int BatchSize = 100;

    private readonly ServiceBusClient _serviceBus;
    private readonly string _queueName;
    private readonly ILogger<RetriggerOrchestrator> _logger;

    public RetriggerOrchestrator(
        ServiceBusClient serviceBus,
        string queueName,
        ILogger<RetriggerOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceBus);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceBus = serviceBus;
        _queueName = queueName;
        _logger = logger;
    }

    /// <summary>
    /// Publishes a single retrigger message for the given asset.
    /// The dedup_key is included in the message body but is never written to logs.
    /// </summary>
    public async Task ScheduleRetriggerAsync(
        CanonicalAsset asset,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        await using var sender = _serviceBus.CreateSender(_queueName);

        var body = new
        {
            asset_id = asset.AssetId,
            team = asset.Team,
            source_id = asset.SourceId,
            dedup_key = asset.DedupKey,
            scheduled_at = DateTimeOffset.UtcNow.ToString("O"),
        };

        var message = new ServiceBusMessage(JsonConvert.SerializeObject(body))
        {
            ContentType = "application/json",
        };

        message.ApplicationProperties["team"] = asset.Team;
        message.ApplicationProperties["source_id"] = asset.SourceId;

        await sender.SendMessageAsync(message, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Scheduled retrigger for assetId={AssetId} team={Team}",
            asset.AssetId,
            asset.Team);
    }

    /// <summary>
    /// Publishes retrigger messages for a list of assets in batches of up to
    /// <c>100</c> messages, respecting Service Bus batch size limits.
    /// </summary>
    public async Task ScheduleBatchRetriggerAsync(
        IReadOnlyList<CanonicalAsset> assets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assets);

        if (assets.Count == 0)
        {
            _logger.LogInformation("ScheduleBatchRetrigger called with empty asset list — nothing to send.");
            return;
        }

        await using var sender = _serviceBus.CreateSender(_queueName);

        var totalSent = 0;
        var batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);

        try
        {
            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();

                var body = new
                {
                    asset_id = asset.AssetId,
                    team = asset.Team,
                    source_id = asset.SourceId,
                    dedup_key = asset.DedupKey,
                    scheduled_at = DateTimeOffset.UtcNow.ToString("O"),
                };

                var message = new ServiceBusMessage(JsonConvert.SerializeObject(body))
                {
                    ContentType = "application/json",
                };

                message.ApplicationProperties["team"] = asset.Team;
                message.ApplicationProperties["source_id"] = asset.SourceId;

                if (!batch.TryAddMessage(message))
                {
                    // Current batch is full — send it and start a new one.
                    await sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
                    totalSent += batch.Count;

                    batch.Dispose();
                    batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);

                    // Add the message that didn't fit to the fresh batch.
                    if (!batch.TryAddMessage(message))
                    {
                        _logger.LogWarning(
                            "Message for assetId={AssetId} exceeds maximum Service Bus message size and will be skipped.",
                            asset.AssetId);
                    }
                }
            }

            // Send any remaining messages.
            if (batch.Count > 0)
            {
                await sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
                totalSent += batch.Count;
            }
        }
        finally
        {
            batch.Dispose();
        }

        _logger.LogInformation(
            "Batch retrigger complete: sent {TotalSent} message(s) to queue={QueueName}",
            totalSent,
            _queueName);
    }
}
