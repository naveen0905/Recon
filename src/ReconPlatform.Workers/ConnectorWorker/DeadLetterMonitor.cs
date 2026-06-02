using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReconPlatform.Storage;

namespace ReconPlatform.Workers.ConnectorWorker;

/// <summary>
/// Background service that reads from the connector-worker dead-letter sub-queue and
/// writes each poisoned message to <c>audit_log</c> with <c>action="dead_letter_received"</c>.
/// Message bodies are never logged (SOC2).
/// </summary>
public sealed class DeadLetterMonitor : BackgroundService
{
    private readonly ServiceBusClient _serviceBus;
    private readonly string _queueName;
    private readonly SqlMetadataClient _sqlClient;
    private readonly ILogger<DeadLetterMonitor> _logger;

    public DeadLetterMonitor(
        ServiceBusClient serviceBus,
        IConfiguration configuration,
        SqlMetadataClient sqlClient,
        ILogger<DeadLetterMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceBus);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sqlClient);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceBus = serviceBus;
        _queueName  = configuration["CONNECTOR_QUEUE_NAME"]
            ?? throw new InvalidOperationException("CONNECTOR_QUEUE_NAME configuration is required.");
        _sqlClient  = sqlClient;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Dead-letter sub-queue path: {queueName}/$DeadLetterQueue
        var deadLetterPath = $"{_queueName}/$DeadLetterQueue";

        _logger.LogInformation("DeadLetterMonitor started — monitoring {DeadLetterPath}", deadLetterPath);

        await using var processor = _serviceBus.CreateProcessor(deadLetterPath, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls   = 1,
            AutoCompleteMessages = false,
        });

        processor.ProcessMessageAsync += ProcessDeadLetterAsync;
        processor.ProcessErrorAsync   += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("DeadLetterMonitor stopped.");
    }

    private async Task ProcessDeadLetterAsync(ProcessMessageEventArgs args)
    {
        var ct      = args.CancellationToken;
        var message = args.Message;

        var messageId   = message.MessageId;
        var reason      = message.DeadLetterReason ?? "(none)";
        var description = message.DeadLetterErrorDescription ?? "(none)";
        var enqueued    = message.EnqueuedTime;

        // Log metadata only — never the message body (SOC2)
        _logger.LogWarning(
            "Dead-letter received: messageId={MessageId} reason={Reason} description={Description} enqueuedAt={EnqueuedAt}",
            messageId, reason, description, enqueued);

        try
        {
            await _sqlClient.WriteDeadLetterAuditAsync(
                messageId, reason, description, enqueued, ct).ConfigureAwait(false);

            await args.CompleteMessageAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write dead-letter audit for messageId={MessageId} — abandoning.", messageId);
            await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "DeadLetterMonitor processor error: source={Source} entity={Entity}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
