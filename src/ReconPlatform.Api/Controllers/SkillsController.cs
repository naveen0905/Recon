using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReconPlatform.Skills;
using ReconPlatform.Skills.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReconPlatform.Api.Controllers;

[ApiController]
[Route("api/skills")]
[Authorize]
public sealed class SkillsController : ControllerBase
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly SkillRegistry _registry;
    private readonly ILogger<SkillsController> _logger;

    public SkillsController(SkillRegistry registry, ILogger<SkillsController> logger)
    {
        _registry = registry;
        _logger   = logger;
    }

    // ── POST /api/skills ──────────────────────────────────────────────────────

    [HttpPost]
    [Consumes("text/plain", "application/yaml", "application/x-yaml")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterSkillAsync(CancellationToken ct)
    {
        string yaml;
        using (var reader = new StreamReader(Request.Body))
            yaml = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(yaml))
            return BadRequest(new { error = "Request body (YAML) is required" });

        SkillDefinition skill;
        try
        {
            skill = YamlDeserializer.Deserialize<SkillDefinition>(yaml);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to deserialize skill YAML: {Message}", ex.Message);
            return BadRequest(new { error = "Invalid skill YAML", detail = ex.Message });
        }

        if (string.IsNullOrWhiteSpace(skill.Id))
            return BadRequest(new { error = "skill 'id' must not be empty" });

        if (string.IsNullOrWhiteSpace(skill.Trigger.Type))
            return BadRequest(new { error = "skill 'trigger.type' must not be empty" });

        _registry.RegisterSkill(skill);

        _logger.LogInformation("Skill registered via API: {SkillId}", skill.Id);
        return Created($"/api/skills/{skill.Id}", new { skillId = skill.Id });
    }

    // ── GET /api/skills ───────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetSkillsAsync()
    {
        var ids    = _registry.GetLoadedSkillIds();
        var skills = ids
            .Select(id => _registry.TryGetSkill(id, out var skill) ? skill : null)
            .Where(s => s is not null)
            .ToList();

        return Ok(skills);
    }

    // ── DELETE /api/skills/{id} ───────────────────────────────────────────────

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteSkill(string id)
    {
        _registry.UnregisterSkill(id);
        _logger.LogInformation("Skill unregistered via API: {SkillId}", id);
        return NoContent();
    }
}
