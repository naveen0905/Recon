using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Workers.StalenessTimer;

// Stub — full implementation in Task 3.3
internal sealed class StalenessTimerService : BackgroundService
{
    private readonly ILogger<StalenessTimerService> _logger;

    public StalenessTimerService(ILogger<StalenessTimerService> logger)
        => _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StalenessTimer started — scheduled every 6 hours");
        return Task.CompletedTask;
    }
}
