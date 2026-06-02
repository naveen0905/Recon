using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReconPlatform.Config;
using ReconPlatform.Engine;
using ReconPlatform.Storage;

namespace ReconPlatform.Workers.StalenessTimer;

/// <summary>
/// Periodically scans all team assets for staleness and schedules retrigger
/// messages for any asset whose PulledAt age exceeds the configured threshold.
///
/// Team names are read from IConfiguration["TEAM_NAMES"] (comma-separated).
/// The check interval is controlled by IConfiguration["STALENESS_INTERVAL_HOURS"]
/// (default 6 hours).
/// </summary>
internal sealed class StalenessTimerService : BackgroundService
{
    private readonly StalenessChecker _stalenessChecker;
    private readonly RetriggerOrchestrator _retriggerOrchestrator;
    private readonly CosmosDbClient _cosmosClient;
    private readonly SqlMetadataClient _sqlClient;
    private readonly int _intervalHours;
    private readonly ILogger<StalenessTimerService> _logger;

    public StalenessTimerService(
        StalenessChecker stalenessChecker,
        RetriggerOrchestrator retriggerOrchestrator,
        CosmosDbClient cosmosClient,
        SqlMetadataClient sqlClient,
        IConfiguration configuration,
        ILogger<StalenessTimerService> logger)
    {
        ArgumentNullException.ThrowIfNull(stalenessChecker);
        ArgumentNullException.ThrowIfNull(retriggerOrchestrator);
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNull(sqlClient);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _stalenessChecker = stalenessChecker;
        _retriggerOrchestrator = retriggerOrchestrator;
        _cosmosClient = cosmosClient;
        _sqlClient = sqlClient;
        _logger = logger;

        _intervalHours = int.TryParse(configuration["STALENESS_INTERVAL_HOURS"], out var h) && h > 0
            ? h
            : 6;

        _teamNamesConfig = configuration["TEAM_NAMES"] ?? string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "StalenessTimerService started — interval={IntervalHours}h",
            _intervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(TimeSpan.FromHours(_intervalHours), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("StalenessTimerService stopped.");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        // TEAM_NAMES is a comma-separated list of team names to poll each tick,
        // e.g. "alpha,bravo,charlie". This avoids a full table scan on team_configs
        // while keeping the worker stateless.
        var teamNamesRaw = string.Empty;

        // Read via IConfiguration so the value comes from environment / app settings.
        // We capture it inside RunOnceAsync to pick up any runtime changes (e.g., ConfigMap reload).
        teamNamesRaw = GetTeamNames();

        if (string.IsNullOrWhiteSpace(teamNamesRaw))
        {
            _logger.LogWarning("TEAM_NAMES configuration is empty — no teams to check for staleness.");
            return;
        }

        var teamNames = teamNamesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var team in teamNames)
        {
            ct.ThrowIfCancellationRequested();

            await ProcessTeamAsync(team, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessTeamAsync(string team, CancellationToken ct)
    {
        try
        {
            var yaml = await _sqlClient.GetTeamConfigYamlAsync(team, ct).ConfigureAwait(false);

            if (yaml is null)
            {
                _logger.LogWarning("No team config found in SQL for team={Team} — skipping.", team);
                return;
            }

            var teamConfig = TeamConfigSerializer.Deserialize(yaml);

            var staleAssets = await _stalenessChecker
                .FindStaleAssetsAsync(team, teamConfig.Sources, _cosmosClient, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Staleness check complete: team={Team} staleCount={StaleCount}",
                team,
                staleAssets.Count);

            foreach (var asset in staleAssets)
            {
                ct.ThrowIfCancellationRequested();

                await _cosmosClient
                    .MarkRetriggerScheduledAsync(asset.AssetId, asset.Team, ct)
                    .ConfigureAwait(false);
            }

            await _retriggerOrchestrator
                .ScheduleBatchRetriggerAsync(staleAssets, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing staleness for team={Team}", team);
        }
    }

    private string GetTeamNames() => _teamNamesConfig;

    private readonly string _teamNamesConfig;
}
