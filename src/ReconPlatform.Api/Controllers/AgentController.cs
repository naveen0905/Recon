using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ReconPlatform.Api.Controllers;

/// <summary>
/// Proxies agent queries to the Python agent Container App.
/// </summary>
[ApiController]
[Route("api/agent")]
[Authorize]
public sealed class AgentController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentController> _logger;

    public AgentController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AgentController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration     = configuration;
        _logger            = logger;
    }

    // ── POST /api/agent/query ─────────────────────────────────────────────────

    /// <summary>
    /// Forwards a natural-language recon query to the Python agent service.
    /// </summary>
    [HttpPost("query")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> QueryAsync(
        [FromBody] AgentQueryRequest request,
        CancellationToken ct)
    {
        // ── Input validation ────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(request.Team))
            return BadRequest(new { error = "team is required" });

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "question is required" });

        if (request.Question.Length > 2000)
            return BadRequest(new { error = "question must be 2000 characters or fewer" });

        // ── Team-claim enforcement ──────────────────────────────────────────
        var claimValue = HttpContext.Items.TryGetValue("team_claim", out var tc) ? tc as string : null;
        if (string.IsNullOrWhiteSpace(claimValue) ||
            !string.Equals(claimValue, request.Team, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "AgentQuery: team claim mismatch actor={Actor} requestedTeam={Team}",
                User.Identity?.Name ?? "anonymous", request.Team);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Insufficient team privileges" });
        }

        // ── Structured audit log (actor, team, engagement, question length — NOT question text) ──
        var actor = User.Identity?.Name ?? "anonymous";
        _logger.LogInformation(
            "AgentQuery: actor={Actor} team={Team} engagementId={EngagementId} questionLength={QuestionLength}",
            actor, request.Team, request.EngagementId, request.Question.Length);

        // ── Forward to Python agent ─────────────────────────────────────────
        var agentUrl = _configuration["Agent:Url"] ?? "http://localhost:8000";
        var targetUri = $"{agentUrl.TrimEnd('/')}/query";

        var httpClient = _httpClientFactory.CreateClient("agent");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Forward the incoming Bearer token
        var authHeader = Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..];
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            var downstream = await httpClient.PostAsJsonAsync(targetUri, request, ct)
                .ConfigureAwait(false);

            if (downstream.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Agent service refused access" });

            var body = await downstream.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Content(body, "application/json");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AgentQuery: downstream agent unavailable team={Team}", request.Team);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Agent service unavailable" });
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "AgentQuery: downstream agent timed out team={Team}", request.Team);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Agent service timed out" });
        }
    }
}

// ── Request model ─────────────────────────────────────────────────────────────

public sealed record AgentQueryRequest(
    [property: Required] string Team,
    string? EngagementId,
    [property: Required][property: MaxLength(2000)] string Question,
    string? Model);
