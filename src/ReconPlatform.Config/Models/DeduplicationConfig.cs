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
    public IReadOnlyList<string> MatchKeys { get; init; } = [];
    public ConflictResolution ConflictResolution { get; init; } = ConflictResolution.LastWrite;

    /// <summary>Lower value = higher priority. Used when ConflictResolution = SourcePriority.</summary>
    public int SourcePriority { get; init; } = 100;

    /// <summary>
    /// Fully-qualified type name of a class implementing IDeduplicationResolver.
    /// Must be in the plugins/ directory. Null = built-in resolver.
    /// </summary>
    public string? CustomResolver { get; init; }
}
