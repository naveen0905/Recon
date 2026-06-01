using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Workers.ConnectorWorker;

// Stub — full implementation in Task 3.4
internal sealed class ConnectorWorkerService : BackgroundService
{
    private readonly ILogger<ConnectorWorkerService> _logger;

    public ConnectorWorkerService(ILogger<ConnectorWorkerService> logger)
        => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConnectorWorker started — awaiting Service Bus messages");
        return Task.CompletedTask;
    }
}
