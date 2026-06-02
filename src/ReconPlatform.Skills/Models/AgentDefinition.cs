using YamlDotNet.Serialization;

namespace ReconPlatform.Skills.Models;

public sealed record AgentDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; } = 1;

    // llm_agent
    public string Type { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Model { get; init; }

    [YamlMember(Alias = "system_prompt")]
    public string? SystemPrompt { get; init; }

    public List<string> Tools { get; init; } = [];
    public bool Enabled { get; init; } = true;
}
