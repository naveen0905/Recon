using YamlDotNet.Serialization;

namespace ReconPlatform.Skills.Models;

public sealed record SkillDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public string? Description { get; init; }
    public SkillTrigger Trigger { get; init; } = new();
    public List<SkillAction> Actions { get; init; } = [];
    public bool Enabled { get; init; } = true;
}

public sealed record SkillTrigger
{
    // on_new_asset | on_severity_change | scheduled | manual
    public string Type { get; init; } = string.Empty;
    public Dictionary<string, object> Filter { get; init; } = [];
}

public sealed record SkillAction
{
    // retrigger_sources | notify
    public string Type { get; init; } = string.Empty;

    [YamlMember(Alias = "scope")]
    public string? Scope { get; init; }

    public List<string> Sources { get; init; } = [];
    public string? Channel { get; init; }
    public string? Queue { get; init; }
}
