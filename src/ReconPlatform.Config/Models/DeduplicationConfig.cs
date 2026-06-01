using YamlDotNet.Serialization;

namespace ReconPlatform.Config.Models;

public enum ConflictResolution
{
    [YamlMember(Alias = "last_write")]
    LastWrite,

    [YamlMember(Alias = "highest_confidence")]
    HighestConfidence,

    [YamlMember(Alias = "source_priority")]
    SourcePriority,
}

public sealed record DeduplicationConfig
{
    public List<string> MatchKeys { get; init; } = [];
    public ConflictResolution ConflictResolution { get; init; } = ConflictResolution.LastWrite;
    public int SourcePriority { get; init; } = 100;
    public string? CustomResolver { get; init; }
}
