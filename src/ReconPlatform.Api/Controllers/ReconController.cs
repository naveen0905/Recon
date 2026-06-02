using Microsoft.AspNetCore.Mvc;
using ReconPlatform.Common.Models;
using ReconPlatform.Engine;
using ReconPlatform.Storage;

namespace ReconPlatform.Api.Controllers;

[ApiController]
[Route("api/recon")]
public sealed class ReconController : ControllerBase
{
    private readonly RetriggerOrchestrator _retriggerOrchestrator;
    private readonly CosmosDbClient _cosmosClient;
    private readonly SqlMetadataClient _sqlClient;
    private readonly ILogger<ReconController> _logger;

    public ReconController(
        RetriggerOrchestrator retriggerOrchestrator,
        CosmosDbClient cosmosClient,
        SqlMetadataClient sqlClient,
        ILogger<ReconController> logger)
    {
        _retriggerOrchestrator = retriggerOrchestrator;
        _cosmosClient = cosmosClient;
        _sqlClient = sqlClient;
        _logger = logger;
    }

    /// <summary>
    /// Schedules an ad-hoc retrigger pull for a specific asset or an entire team/source.
    /// </summary>
    /// <remarks>
    /// At least one of assetId or sourceId must be supplied.
    /// Authentication and team-claim enforcement wired in Task 4.2.
    /// </remarks>
    [HttpPost("retrigger")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetriggerAsync(
        [FromBody] RetriggerRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Team))
            return BadRequest(new { error = "team is required" });

        if (string.IsNullOrWhiteSpace(request.AssetId) && string.IsNullOrWhiteSpace(request.SourceId))
            return BadRequest(new { error = "at least one of assetId or sourceId is required" });

        _logger.LogInformation(
            "Retrigger requested: team={Team} assetId={AssetId} sourceId={SourceId}",
            request.Team, request.AssetId, request.SourceId);

        // Pre-action audit log (SOC2) — actor is the Entra UPN from the token (Task 4.2 wires this)
        var actor = User.Identity?.Name ?? "anonymous";
        await _sqlClient.InsertConnectorRunAsync(
            request.Team,
            request.SourceId ?? "(ad-hoc)",
            assetsPulled: 0,
            assetsDeduped: 0,
            status: "retrigger_requested",
            errorMessage: null,
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.AssetId))
        {
            // Single-asset retrigger
            var asset = await _cosmosClient
                .GetAssetAsync(request.AssetId, request.Team, ct)
                .ConfigureAwait(false);

            if (asset is null)
                return NotFound(new { error = $"Asset '{request.AssetId}' not found in team '{request.Team}'" });

            await _retriggerOrchestrator.ScheduleRetriggerAsync(asset, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Scheduled single-asset retrigger: team={Team} assetId={AssetId}",
                request.Team, request.AssetId);
        }
        else
        {
            // Source-level retrigger — synthetic asset carrying just team+source routing info
            var syntheticAsset = new CanonicalAsset
            {
                AssetId = $"{request.Team}::source-retrigger::{request.SourceId}",
                Team = request.Team,
                SourceId = request.SourceId!,
                DedupKey = string.Empty,
                PulledAt = DateTimeOffset.UtcNow,
            };

            await _retriggerOrchestrator.ScheduleRetriggerAsync(syntheticAsset, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Scheduled source-level retrigger: team={Team} sourceId={SourceId}",
                request.Team, request.SourceId);
        }

        return Accepted(new
        {
            message = "Retrigger scheduled",
            team = request.Team,
            assetId = request.AssetId,
            sourceId = request.SourceId,
            scheduledAt = DateTimeOffset.UtcNow,
        });
    }
}

public sealed record RetriggerRequest(
    string Team,
    string? AssetId,
    string? SourceId);
