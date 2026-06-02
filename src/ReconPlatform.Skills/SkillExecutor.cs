using Microsoft.Extensions.Logging;
using ReconPlatform.Common.Models;
using ReconPlatform.Skills.Models;

namespace ReconPlatform.Skills;

public sealed class SkillExecutor
{
    private readonly SkillRegistry _registry;
    private readonly ILogger<SkillExecutor> _logger;

    public SkillExecutor(SkillRegistry registry, ILogger<SkillExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    public async Task<SkillExecutionResult> ExecuteAsync(
        string skillId,
        CanonicalAsset triggeringAsset,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(triggeringAsset);

        if (!_registry.TryGetSkill(skillId, out var skill) || skill is null)
        {
            _logger.LogWarning("Skill not found: {SkillId}", skillId);
            return SkillExecutionResult.NotFound(skillId);
        }

        if (!skill.Enabled)
        {
            _logger.LogInformation("Skill {SkillId} is disabled, skipping execution.", skillId);
            return SkillExecutionResult.Skipped(skillId, "disabled");
        }

        var actionsExecuted = 0;

        foreach (var action in skill.Actions)
        {
            ct.ThrowIfCancellationRequested();

            switch (action.Type)
            {
                case "retrigger_sources":
                    _logger.LogInformation(
                        "Skill {SkillId}: retrigger_sources would be scheduled for asset {AssetId}. Scope={Scope}, Sources=[{Sources}]",
                        skillId,
                        triggeringAsset.AssetId,
                        action.Scope,
                        string.Join(", ", action.Sources));
                    break;

                case "notify":
                    _logger.LogInformation(
                        "Skill {SkillId}: notify via channel={Channel}, queue={Queue} for asset {AssetId}.",
                        skillId,
                        action.Channel,
                        action.Queue,
                        triggeringAsset.AssetId);
                    break;

                default:
                    _logger.LogWarning("Skill {SkillId}: unknown action type '{ActionType}', skipping.", skillId, action.Type);
                    continue;
            }

            actionsExecuted++;
        }

        // Satisfy async contract — no actual I/O in this executor layer.
        await Task.CompletedTask.ConfigureAwait(false);

        return SkillExecutionResult.Success(skillId, actionsExecuted);
    }
}

public sealed record SkillExecutionResult(
    bool Succeeded,
    string? SkillId,
    string? Reason,
    int ActionsExecuted)
{
    public static SkillExecutionResult Success(string skillId, int actionsExecuted) =>
        new(Succeeded: true, SkillId: skillId, Reason: null, ActionsExecuted: actionsExecuted);

    public static SkillExecutionResult NotFound(string skillId) =>
        new(Succeeded: false, SkillId: skillId, Reason: "not_found", ActionsExecuted: 0);

    public static SkillExecutionResult Skipped(string skillId, string reason) =>
        new(Succeeded: false, SkillId: skillId, Reason: reason, ActionsExecuted: 0);
}
