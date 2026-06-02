using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReconPlatform.Storage;

namespace ReconPlatform.Api.Controllers;

[ApiController]
[Route("api/engagements")]
[Authorize]
public sealed class EngagementsController : ControllerBase
{
    private readonly SqlMetadataClient _sqlClient;
    private readonly SynapseClient _synapseClient;
    private readonly ILogger<EngagementsController> _logger;

    public EngagementsController(
        SqlMetadataClient sqlClient,
        SynapseClient synapseClient,
        ILogger<EngagementsController> logger)
    {
        _sqlClient    = sqlClient;
        _synapseClient = synapseClient;
        _logger       = logger;
    }

    // ── POST /api/engagements ─────────────────────────────────────────────────

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateEngagementAsync(
        [FromBody] CreateEngagementRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Team))
            return BadRequest(new { error = "team is required" });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required" });

        if (request.EndDate < request.StartDate)
            return BadRequest(new { error = "endDate must be >= startDate" });

        // Team-claim enforcement for write
        var enforce = EnforceTeamClaim(request.Team);
        if (enforce is not null) return enforce;

        var engagementId = Guid.NewGuid().ToString("N");
        var actor        = User.Identity?.Name ?? "anonymous";

        var scopePayload = new
        {
            name         = request.Name,
            scope_json   = request.ScopeJson,
            start_date   = request.StartDate.ToString("O"),
            end_date     = request.EndDate.ToString("O"),
        };

        var scopeJson = JsonSerializer.Serialize(scopePayload);

        await _sqlClient.UpsertEngagementAsync(engagementId, request.Team, scopeJson, actor, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Engagement created: id={EngagementId} team={Team} by actor={Actor}",
            engagementId, request.Team, actor);

        return Created($"/api/engagements/{engagementId}", new { engagementId });
    }

    // ── GET /api/engagements ──────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListEngagementsAsync(
        [FromQuery] string team,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(team))
            return BadRequest(new { error = "team query parameter is required" });

        var engagements = await _sqlClient.ListEngagementsAsync(team, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Engagements listed: team={Team} count={Count}", team, engagements.Count);

        return Ok(engagements.Select(e => new
        {
            engagementId = e.EngagementId,
            scopeJson    = e.ScopeJson,
        }));
    }

    // ── GET /api/engagements/{id}/assets ──────────────────────────────────────

    [HttpGet("{id}/assets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEngagementAssetsAsync(
        string id,
        [FromQuery] string team,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(team))
            return BadRequest(new { error = "team query parameter is required" });

        var enforce = EnforceTeamClaim(team);
        if (enforce is not null) return enforce;

        // Find the engagement to get scope details.
        var engagements = await _sqlClient.ListEngagementsAsync(team, ct).ConfigureAwait(false);
        var engagement  = engagements.FirstOrDefault(e =>
            string.Equals(e.EngagementId, id, StringComparison.OrdinalIgnoreCase));

        if (engagement == default)
            return NotFound(new { error = $"Engagement '{id}' not found for team '{team}'" });

        // Parse scope_targets from scope JSON if present, to narrow the asset query.
        var sqlBuilder = new StringBuilder(
            "SELECT TOP 1000 * FROM assets WHERE team = @team");

        var parameters = new Dictionary<string, object?> { ["@team"] = team };

        try
        {
            using var doc = JsonDocument.Parse(engagement.ScopeJson);
            if (doc.RootElement.TryGetProperty("scope_targets", out var targets) &&
                targets.ValueKind == JsonValueKind.Array)
            {
                var hosts = targets.EnumerateArray()
                    .Select(t => t.GetString())
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .ToList();

                if (hosts.Count > 0)
                {
                    var paramNames = hosts.Select((_, i) => $"@host{i}").ToList();
                    sqlBuilder.Append($" AND host IN ({string.Join(", ", paramNames)})");

                    for (var i = 0; i < hosts.Count; i++)
                        parameters[$"@host{i}"] = hosts[i];
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "Could not parse scope_json for engagement={EngagementId}: {Message}",
                id, ex.Message);
        }

        var results = new List<Dictionary<string, object?>>();
        await foreach (var row in _synapseClient.QueryAsync(sqlBuilder.ToString(), parameters, ct))
            results.Add(row);

        _logger.LogInformation(
            "Engagement assets queried: id={EngagementId} team={Team} rows={Rows}",
            id, team, results.Count);

        return Ok(results);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private IActionResult? EnforceTeamClaim(string team)
    {
        var claimValue = HttpContext.Items.TryGetValue("team_claim", out var tc)
            ? tc as string
            : null;

        if (string.IsNullOrWhiteSpace(claimValue) ||
            !string.Equals(claimValue, team, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Team claim mismatch: claim={Claim} route={Route}", claimValue, team);
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Insufficient team privileges" });
        }

        return null;
    }
}

public sealed record CreateEngagementRequest(
    string Team,
    string Name,
    string ScopeJson,
    DateOnly StartDate,
    DateOnly EndDate);
