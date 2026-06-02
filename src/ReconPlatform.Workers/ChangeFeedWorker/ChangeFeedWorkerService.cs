using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ReconPlatform.Common.Models;
using ReconPlatform.Engine;
using ReconPlatform.Skills;
using ReconPlatform.Storage;

namespace ReconPlatform.Workers.ChangeFeedWorker;

/// <summary>
/// Polls a Service Bus queue for asset change-event messages and triggers
/// matching skill actions (e.g. source retriggers) for each event.
///
/// Architecture note: In a full production deployment this worker would be
/// replaced by a Cosmos DB Change Feed processor hosted in a dedicated
/// Container App that uses the Azure Cosmos DB Change Feed SDK
/// (Microsoft.Azure.Cosmos.ChangeFeedProcessor). The Service Bus queue
/// approach used here provides equivalent behaviour while keeping the worker
/// stateless and infrastructure-agnostic in the current phase.
///
/// Configuration keys (environment variables / app settings):
///   CHANGE_EVENTS_QUEUE                    — Service Bus queue name for change events.
///   SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE  — e.g. mybus.servicebus.windows.net
///   COSMOS_CHANGE_FEED_POLL_INTERVAL_SECONDS — poll interval in seconds (default: 30).
/// </summary>
internal sealed class ChangeFeedWorkerService : BackgroundService
{
    private const int DefaultPollIntervalSeconds = 30;
    private const int MaxMessagesPerReceive = 20;

    private readonly CosmosDbClient _cosmosClient;
    private readonly SkillRegistry _skillRegistry;
    private readonly RetriggerOrchestrator _retriggerOrchestrator;
    private readonly string? _queueName;
    private readonly string? _serviceBusNamespace;
    private readonly int _pollIntervalSeconds;
    private readonly ILogger<ChangeFeedWorkerService> _logger;

    public ChangeFeedWorkerService(
        CosmosDbClient cosmosClient,
        SkillRegistry skillRegistry,
        RetriggerOrchestrator retriggerOrchestrator,
        IConfiguration configuration,
        ILogger<ChangeFeedWorkerService> logger)
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNull(skillRegistry);
        ArgumentNullException.ThrowIfNull(retriggerOrchestrator);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _cosmosClient = cosmosClient;
        _skillRegistry = skillRegistry;
        _retriggerOrchestrator = retriggerOrchestrator;
        _logger = logger;

        _queueName = configuration["CHANGE_EVENTS_QUEUE"];
        _serviceBusNamespace = configuration["SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE"];

        _pollIntervalSeconds = int.TryParse(
            configuration["COSMOS_CHANGE_FEED_POLL_INTERVAL_SECONDS"],
            out var s) && s > 0
            ? s
            : DefaultPollIntervalSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ChangeFeedWorkerService started — pollIntervalSeconds={PollIntervalSeconds}",
            _pollIntervalSeconds);

        if (string.IsNullOrWhiteSpace(_queueName) || string.IsNullOrWhiteSpace(_serviceBusNamespace))
        {
            _logger.LogWarning(
                "CHANGE_EVENTS_QUEUE or SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE is not configured. " +
                "Waiting for Cosmos DB Change Feed SDK integration — no messages will be processed.");

            // Park until cancellation; nothing to poll.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return;
        }

        await using var client = new ServiceBusClient(
            _serviceBusNamespace,
            new Azure.Identity.DefaultAzureCredential());

        await using var receiver = client.CreateReceiver(_queueName);

        _logger.LogInformation(
            "ChangeFeedWorkerService polling queue={QueueName} on namespace={Namespace}",
            _queueName,
            _serviceBusNamespace);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await receiver.ReceiveMessagesAsync(
                    maxMessages: MaxMessagesPerReceive,
                    maxWaitTime: TimeSpan.FromSeconds(_pollIntervalSeconds),
                    cancellationToken: stoppingToken)
                    .ConfigureAwait(false);

                if (messages is null || messages.Count == 0)
                {
                    continue;
                }

                foreach (var message in messages)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await ProcessMessageAsync(receiver, message, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in ChangeFeedWorkerService receive loop.");

                // Brief back-off before retrying to avoid tight error loops.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("ChangeFeedWorkerService stopped.");
    }

    private async Task ProcessMessageAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        CancellationToken ct)
    {
        ChangeEventMessage? changeEvent = null;
        try
        {
            changeEvent = JsonConvert.DeserializeObject<ChangeEventMessage>(
                message.Body.ToString());

            if (changeEvent is null)
            {
                _logger.LogWarning(
                    "Received null or unparseable change-event message messageId={MessageId} — dead-lettering.",
                    message.MessageId);
                await receiver.DeadLetterMessageAsync(
                    message, deadLetterReason: "NullOrUnparseable", cancellationToken: ct)
                    .ConfigureAwait(false);
                return;
            }

            _logger.LogInformation(
                "Processing change event: assetId={AssetId} team={Team} eventType={EventType}",
                changeEvent.AssetId,
                changeEvent.Team,
                changeEvent.EventType);

            await HandleChangeEventAsync(changeEvent, ct).ConfigureAwait(false);

            await receiver.CompleteMessageAsync(message, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Completed change-event message messageId={MessageId}",
                message.MessageId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing change-event message messageId={MessageId} assetId={AssetId} — dead-lettering.",
                message.MessageId,
                changeEvent?.AssetId ?? "(unknown)");

            try
            {
                await receiver.DeadLetterMessageAsync(
                    message,
                    deadLetterReason: ex.GetType().Name,
                    deadLetterErrorDescription: ex.Message,
                    cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (Exception dlEx)
            {
                _logger.LogError(
                    dlEx,
                    "Failed to dead-letter message messageId={MessageId}",
                    message.MessageId);
            }
        }
    }

    private async Task HandleChangeEventAsync(ChangeEventMessage changeEvent, CancellationToken ct)
    {
        // SkillRegistry.GetLoadedSkillIds() returns skill IDs for loaded skills.
        // In this phase the registry is a stub (Task 4.1 will wire trigger filtering).
        // We log the loaded skill count and apply a best-effort match on the event type.
        // When Task 4.1 adds GetSkillsByTriggerType(string triggerType), replace the
        // block below with a direct call to that method.
        var loadedSkillIds = _skillRegistry.GetLoadedSkillIds();

        _logger.LogInformation(
            "Evaluating {SkillCount} loaded skill(s) for eventType={EventType} team={Team}",
            loadedSkillIds.Count,
            changeEvent.EventType,
            changeEvent.Team);

        // Build a synthetic asset so RetriggerOrchestrator can route the message.
        // AssetId and DedupKey are intentionally left as empty strings here because
        // the retrigger targets the source, not a specific stored document.
        var syntheticAsset = new CanonicalAsset
        {
            AssetId = changeEvent.AssetId ?? string.Empty,
            Team = changeEvent.Team ?? string.Empty,
            SourceId = changeEvent.SourceId ?? string.Empty,
            DedupKey = string.Empty,
            Type = "change-event",
            PulledAt = DateTimeOffset.UtcNow,
        };

        // Trigger retrigger for "on_new_asset" and "on_severity_change" event types,
        // which are the two canonical skill trigger types defined in the skills schema.
        if (changeEvent.EventType is "on_new_asset" or "on_severity_change")
        {
            await _retriggerOrchestrator
                .ScheduleRetriggerAsync(syntheticAsset, ct)
                .ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "No retrigger action mapped for eventType={EventType} — skipping.",
                changeEvent.EventType);
        }
    }

    // ── Internal message shape ────────────────────────────────────────────────

    private sealed class ChangeEventMessage
    {
        [JsonProperty("asset_id")]
        public string? AssetId { get; init; }

        [JsonProperty("team")]
        public string? Team { get; init; }

        [JsonProperty("source_id")]
        public string? SourceId { get; init; }

        /// <summary>Expected values: "on_new_asset" | "on_severity_change"</summary>
        [JsonProperty("event_type")]
        public string? EventType { get; init; }
    }
}
