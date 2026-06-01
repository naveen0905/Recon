namespace ReconPlatform.Config.Models;

public enum ConflictResolution
{
    LastWrite,
    HighestConfidence,
    SourcePriority,
}

public sealed record DeduplicationConfig
{
    /// <summary>Fields used to compute the dedup key for this source.</summary>
    public IReadOnlyList<string> MatchKeys { get; init; } = [];

    public ConflictResolution ConflictResolution { get; init; } = ConflictResolution.LastWrite;

    /// <summary>Lower value = higher priority. Used when ConflictResolution = SourcePriority.</summary>
    public int SourcePriority { get; init; } = 100;

    /// <summary>
    /// Optional fully-qualified type name of a class implementing IDeduplicationResolver.
    /// Must be located in the plugins/ directory. Null = built-in resolver.
    /// </summary>
    public string? CustomResolver { get; init; }
}
