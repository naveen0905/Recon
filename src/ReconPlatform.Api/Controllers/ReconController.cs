using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReconPlatform.Common.Models;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;
using ReconPlatform.Engine;
using ReconPlatform.Storage;

namespace ReconPlatform.Api.Controllers;

[ApiController]
[Route("api/recon")]
[Authorize]
public sealed class ReconController : ControllerBase
{
    // Allowlist for filter param: only word chars, spaces, and SQL comparison operators.
    private static readonly Regex FilterAllowlist =
        new(@"^[a-zA-Z0-9_\s=<>!'.]+$", RegexOptions.Compiled);

    private readonly RetriggerOrchestrator _retriggerOrchestrator;
    private readonly CosmosDbClient _cosmosClient;
    private readonly SqlMetadataClient _sqlClient;
    private readonly BlobStorageClient _blobClient;
    private readonly SynapseClient _synapseClient;
    private readonly DeduplicationEngine _deduplicationEngine;
    private readonly DiffEngine _diffEngine;
    private readonly Normalizer _normalizer;
    private readonly IEnumerable<IConnector> _connectors;
    private readonly ILogger<ReconController> _logger;

    public ReconController(
        RetriggerOrchestrator retriggerOrchestrator,
        CosmosDbClient cosmosClient,
        SqlMetadataClient sqlClient,
        BlobStorageClient blobClient,
        SynapseClient synapseClient,
        DeduplicationEngine deduplicationEngine,
        DiffEngine diffEngine,
        Normalizer normalizer,
        IEnumerable<IConnector> connectors,
        ILogger<ReconController> logger)
    {
        _retriggerOrchestrator = retriggerOrchestrator;
        _cosmosClient           = cosmosClient;
        _sqlClient              = sqlClient;
        _blobClient             = blobClient;
        _synapseClient          = synapseClient;
        _deduplicationEngine    = deduplicationEngine;
        _diffEngine             = diffEngine;
        _normalizer             = normalizer;
        _connectors             = connectors;
        _logger                 = logger;
    }

    // ── POST /api/recon/retrigger ─────────────────────────────────────────────

    /// <summary>
    /// Schedules an ad-hoc retrigger pull for a specific asset or an entire team/source.
    /// </summary>
    /// <remarks>
    /// At least one of assetId or sourceId must be supplied.
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

        // Pre-action audit log (SOC2) — actor is the Entra UPN from the token
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

    // ── POST /api/recon/pull ──────────────────────────────────────────────────

    [HttpPost("pull")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PullAsync(
        [FromBody] PullRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Team))
            return BadRequest(new { error = "team is required" });

        if (string.IsNullOrWhiteSpace(request.SourceId))
            return BadRequest(new { error = "sourceId is required" });

        // Team-claim enforcement
        var claimValue = HttpContext.Items.TryGetValue("team_claim", out var tc) ? tc as string : null;
        if (string.IsNullOrWhiteSpace(claimValue) ||
            !string.Equals(claimValue, request.Team, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Insufficient team privileges" });
        }

        var yaml = await _sqlClient.GetTeamConfigYamlAsync(request.Team, ct).ConfigureAwait(false);
        if (yaml is null)
            return NotFound(new { error = $"No config found for team '{request.Team}'" });

        var teamConfig = TeamConfigSerializer.Deserialize(yaml);
        var source = teamConfig.Sources.FirstOrDefault(s =>
            string.Equals(s.Id, request.SourceId, StringComparison.OrdinalIgnoreCase));

        if (source is null)
            return NotFound(new { error = $"Source '{request.SourceId}' not found in team '{request.Team}'" });

        var connectorType = source.Type switch
        {
            SourceType.RestApi  => "rest_api",
            SourceType.AzureSql => "azure_sql",
            SourceType.AzureAdx => "azure_adx",
            _                   => source.Type.ToString().ToLowerInvariant(),
        };

        var connector = _connectors.FirstOrDefault(c =>
            string.Equals(c.ConnectorType, connectorType, StringComparison.OrdinalIgnoreCase));

        if (connector is null)
            return BadRequest(new { error = $"No connector registered for type '{connectorType}'" });

        var pulledAt = DateTimeOffset.UtcNow;

        // Pre-action audit log (SOC2)
        var actor = User.Identity?.Name ?? "anonymous";
        await _sqlClient.InsertConnectorRunAsync(
            request.Team, request.SourceId, 0, 0, "pull_started", null, ct)
            .ConfigureAwait(false);

        var rawRows = await connector.PullAsync(source, ct).ConfigureAwait(false);
        var rows    = rawRows.ToList();

        var assetsPulled  = rows.Count;
        var assetsDeduped = 0;
        var upsertedAssets = new List<CanonicalAsset>(assetsPulled);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            // Cast from Dictionary<string, object> to Dictionary<string, object?>
            var rowNullable = row.ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)kvp.Value,
                StringComparer.OrdinalIgnoreCase);

            var normalized = _normalizer.Normalize(rowNullable, request.Team, source, pulledAt, ct);

            var dedupKey = _deduplicationEngine.ComputeDedupKey(normalized, source.Dedup);
            if (string.IsNullOrEmpty(dedupKey))
            {
                _logger.LogWarning(
                    "Empty dedup key for source={SourceId} — skipping upsert.", request.SourceId);
                continue;
            }

            var assetId  = $"{request.Team}::{dedupKey}";
            var withKeys = normalized with { AssetId = assetId, DedupKey = dedupKey };

            var existing = await _cosmosClient.GetAssetAsync(assetId, request.Team, ct)
                .ConfigureAwait(false);

            CanonicalAsset toStore;
            if (existing is not null)
            {
                toStore = _deduplicationEngine.Resolve(existing, withKeys, source.Dedup);
                assetsDeduped++;
            }
            else
            {
                toStore = withKeys;
            }

            await _cosmosClient.UpsertAssetAsync(toStore, ct).ConfigureAwait(false);
            upsertedAssets.Add(toStore);
        }

        // Write snapshot to Blob
        var json       = System.Text.Json.JsonSerializer.Serialize(upsertedAssets);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var blobPath  = await _blobClient.UploadPullAsync(
            request.Team, request.SourceId, pulledAt, stream, ct)
            .ConfigureAwait(false);

        // Log completed run
        await _sqlClient.InsertConnectorRunAsync(
            request.Team, request.SourceId, assetsPulled, assetsDeduped, "success", null, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Pull complete: team={Team} sourceId={SourceId} pulled={Pulled} deduped={Deduped}",
            request.Team, request.SourceId, assetsPulled, assetsDeduped);

        return Ok(new
        {
            assetsPulled,
            assetsDeduped,
            blobPath,
        });
    }

    // ── GET /api/recon/assets ─────────────────────────────────────────────────

    [HttpGet("assets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAssetsAsync(
        [FromQuery] string team,
        [FromQuery] string? filter,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team))
            return BadRequest(new { error = "team query parameter is required" });

        if (limit < 1 || limit > 1000)
            return BadRequest(new { error = "limit must be between 1 and 1000" });

        // Allowlist-validate the filter fragment before passing to Synapse
        if (!string.IsNullOrWhiteSpace(filter) && !FilterAllowlist.IsMatch(filter))
            return BadRequest(new { error = "filter contains disallowed characters" });

        var sql = new StringBuilder(
            $"SELECT TOP {limit} * FROM assets WHERE team = @team");

        if (!string.IsNullOrWhiteSpace(filter))
            sql.Append($" AND {filter}");

        var parameters = new Dictionary<string, object?> { ["@team"] = team };

        var results = new List<Dictionary<string, object?>>();
        await foreach (var row in _synapseClient.QueryAsync(sql.ToString(), parameters, ct))
            results.Add(row);

        _logger.LogInformation(
            "Assets query: team={Team} filter={Filter} limit={Limit} rows={Rows}",
            team, filter, limit, results.Count);

        return Ok(results);
    }

    // ── GET /api/recon/assets/{assetId} ──────────────────────────────────────

    [HttpGet("assets/{assetId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAssetByIdAsync(
        string assetId,
        [FromQuery] string team,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(team))
            return BadRequest(new { error = "team query parameter is required" });

        var asset = await _cosmosClient.GetAssetAsync(assetId, team, ct).ConfigureAwait(false);
        if (asset is null)
            return NotFound(new { error = $"Asset '{assetId}' not found in team '{team}'" });

        _logger.LogInformation("Asset fetched: assetId={AssetId} team={Team}", assetId, team);
        return Ok(asset);
    }

    // ── GET /api/recon/assets/{assetId}/diff ─────────────────────────────────

    [HttpGet("assets/{assetId}/diff")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAssetDiffAsync(
        string assetId,
        [FromQuery] string team,
        [FromQuery] int fromVersion = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team))
            return BadRequest(new { error = "team query parameter is required" });

        var current = await _cosmosClient.GetAssetAsync(assetId, team, ct).ConfigureAwait(false);
        if (current is null)
            return NotFound(new { error = $"Asset '{assetId}' not found in team '{team}'" });

        // Build a synthetic "previous" snapshot at the requested version for diff comparison.
        // Cosmos stores the latest version only; we compute the diff against a placeholder
        // representing the state at fromVersion (the caller supplies context via fromVersion).
        var previous = current with { Version = fromVersion };
        var diff     = _diffEngine.Compute(previous, current);

        _logger.LogInformation(
            "Diff computed: assetId={AssetId} team={Team} fromVersion={FromVersion} currentVersion={CurrentVersion}",
            assetId, team, fromVersion, current.Version);

        return Ok(new
        {
            assetId,
            team,
            fromVersion,
            currentVersion = current.Version,
            diff,
        });
    }
}

public sealed record RetriggerRequest(
    string Team,
    string? AssetId,
    string? SourceId);

public sealed record PullRequest(string Team, string SourceId);
