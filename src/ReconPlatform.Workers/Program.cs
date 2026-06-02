using ReconPlatform.Workers.ChangeFeedWorker;
using ReconPlatform.Workers.ConnectorWorker;
using ReconPlatform.Workers.StalenessTimer;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((cfg) => cfg.WriteTo.Console());

// Each Container App deployment sets WORKER_TYPE to one of:
//   connector-worker | staleness-timer | change-feed
var workerType = builder.Configuration["WORKER_TYPE"]
    ?? throw new InvalidOperationException(
        "WORKER_TYPE environment variable is required. " +
        "Valid values: connector-worker, staleness-timer, change-feed");

switch (workerType)
{
    case "connector-worker":
        builder.Services.AddHostedService<ConnectorWorkerService>();
        builder.Services.AddHostedService<DeadLetterMonitor>();
        break;
    case "staleness-timer":
        builder.Services.AddHostedService<StalenessTimerService>();
        break;
    case "change-feed":
        builder.Services.AddHostedService<ChangeFeedWorkerService>();
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown WORKER_TYPE '{workerType}'. " +
            "Valid values: connector-worker, staleness-timer, change-feed");
}

var host = builder.Build();
host.Run();
