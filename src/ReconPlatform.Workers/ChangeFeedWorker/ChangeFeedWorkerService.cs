using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Workers.ChangeFeedWorker;

// Stub — full implementation in Task 3.5
internal sealed class ChangeFeedWorkerService : BackgroundService
{
    private readonly ILogger<ChangeFeedWorkerService> _logger;

    public ChangeFeedWorkerService(ILogger<ChangeFeedWorkerService> logger)
        => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChangeFeedWorker started — polling Cosmos change feed");
        return Task.CompletedTask;
    }
}
