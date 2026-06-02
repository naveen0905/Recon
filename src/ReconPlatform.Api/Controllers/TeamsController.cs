using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;
using ReconPlatform.Storage;

namespace ReconPlatform.Api.Controllers;

[ApiController]
[Route("api/teams/{team}")]
[Authorize]
public sealed class TeamsController : ControllerBase
{
    private static readonly Regex SecretPattern =
        new(@"\{\{secret:[^}]+\}\}", RegexOptions.Compiled);

    private readonly SqlMetadataClient _sqlClient;
    private readonly IEnumerable<IConnector> _connectors;
    private readonly ILogger<TeamsController> _logger;

    public TeamsController(
        SqlMetadataClient sqlClient,
        IEnumerable<IConnector> connectors,
        ILogger<TeamsController> logger)
    {
        _sqlClient  = sqlClient;
        _connectors = connectors;
        _logger     = logger;
    }

    // ── GET /api/teams/{team}/sources ─────────────────────────────────────────

    [HttpGet("sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSourcesAsync(string team, CancellationToken ct)
    {
        var enforce = EnforceTeamClaim(team);
        if (enforce is not null) return enforce;

        var yaml = await _sqlClient.GetTeamConfigYamlAsync(team, ct).ConfigureAwait(false);
        if (yaml is null)
            return NotFound(new { error = $"No config found for team '{team}'" });

        var scrubbed = ScrubSecrets(yaml);
        var config   = TeamConfigSerializer.Deserialize(scrubbed);

        _logger.LogInformation("Sources listed for team={Team}", team);
        return Ok(config.Sources);
    }

    // ── PUT /api/teams/{team}/sources ─────────────────────────────────────────

    [HttpPut("sources")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateSourcesAsync(
        string team,
        [FromBody] UpdateSourcesRequest request,
        CancellationToken ct)
    {
        var enforce = EnforceTeamClaim(team);
        if (enforce is not null) return enforce;

        if (string.IsNullOrWhiteSpace(request.YamlContent))
            return BadRequest(new { error = "yamlContent is required" });

        // Validate — deserialize will throw on malformed YAML.
        TeamConfig parsed;
        try
        {
            parsed = TeamConfigSerializer.Deserialize(request.YamlContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Invalid YAML for team={Team}: {Message}", team, ex.Message);
            return BadRequest(new { error = "Invalid YAML config", detail = ex.Message });
        }

        if (!string.Equals(parsed.Team, team, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Config 'team' field must match route parameter" });

        var validation = TeamConfigValidator.Validate(parsed);
        if (!validation.IsValid)
            return BadRequest(new { error = "Validation failed", errors = validation.Errors });

        var actor = User.Identity?.Name ?? "anonymous";
        await _sqlClient.UpsertTeamConfigAsync(team, request.YamlContent, actor, ct)
            .ConfigureAwait(false);

        _logger.LogInformation("Sources updated for team={Team} by actor={Actor}", team, actor);
        return NoContent();
    }

    // ── DELETE /api/teams/{team}/sources/{sourceId} ───────────────────────────

    [HttpDelete("sources/{sourceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSourceAsync(
        string team, string sourceId, CancellationToken ct)
    {
        var enforce = EnforceTeamClaim(team);
        if (enforce is not null) return enforce;

        var yaml = await _sqlClient.GetTeamConfigYamlAsync(team, ct).ConfigureAwait(false);
        if (yaml is null)
            return NotFound(new { error = $"No config found for team '{team}'" });

        var config = TeamConfigSerializer.Deserialize(yaml);

        var existing = config.Sources.FirstOrDefault(s =>
            string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            return NotFound(new { error = $"Source '{sourceId}' not found in team '{team}'" });

        var updated = config with
        {
            Sources = config.Sources.Where(s =>
                !string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)).ToList(),
        };

        var newYaml = TeamConfigSerializer.Serialize(updated);
        var actor   = User.Identity?.Name ?? "anonymous";

        await _sqlClient.UpsertTeamConfigAsync(team, newYaml, actor, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Source deleted: team={Team} sourceId={SourceId} by actor={Actor}",
            team, sourceId, actor);

        return NoContent();
    }

    // ── POST /api/teams/{team}/sources/{sourceId}/test ────────────────────────

    [HttpPost("sources/{sourceId}/test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestSourceAsync(
        string team, string sourceId, CancellationToken ct)
    {
        var enforce = EnforceTeamClaim(team);
        if (enforce is not null) return enforce;

        var yaml = await _sqlClient.GetTeamConfigYamlAsync(team, ct).ConfigureAwait(false);
        if (yaml is null)
            return NotFound(new { error = $"No config found for team '{team}'" });

        var config = TeamConfigSerializer.Deserialize(yaml);
        var source = config.Sources.FirstOrDefault(s =>
            string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));

        if (source is null)
            return NotFound(new { error = $"Source '{sourceId}' not found in team '{team}'" });

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

        var connected = await connector.TestConnectionAsync(source, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Connection test: team={Team} sourceId={SourceId} connected={Connected}",
            team, sourceId, connected);

        return Ok(new
        {
            connected,
            sourceId,
            testedAt = DateTimeOffset.UtcNow,
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns 403 if the team claim on the token does not match the route team.
    /// Returns null if the claim is satisfied (caller should proceed).
    /// </summary>
    private IActionResult? EnforceTeamClaim(string team)
    {
        var claimValue = HttpContext.Items.TryGetValue("team_claim", out var tc)
            ? tc as string
            : null;

        if (string.IsNullOrWhiteSpace(claimValue) ||
            !string.Equals(claimValue, team, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Team claim mismatch: claim={Claim} route={Route}",
                claimValue, team);
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Insufficient team privileges" });
        }

        return null;
    }

    /// <summary>Replaces all {{secret:*}} placeholders with "[secret]" before returning to callers.</summary>
    private static string ScrubSecrets(string yaml) =>
        SecretPattern.Replace(yaml, "[secret]");
}

public sealed record UpdateSourcesRequest(string YamlContent);
