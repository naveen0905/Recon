namespace ReconPlatform.Config.Models;

// Stub — full implementation in Task 1.3
public sealed record TeamConfig
{
    public string Team { get; init; } = string.Empty;
    public int StaleAfterDays { get; init; } = 7;
    public DeduplicationConfig Dedup { get; init; } = new();
    public IReadOnlyList<SourceConfig> Sources { get; init; } = [];
}

public sealed record SourceConfig
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int? StaleAfterDays { get; init; }
    public DeduplicationConfig Dedup { get; init; } = new();
}

public sealed record DeduplicationConfig
{
    public IReadOnlyList<string> MatchKeys { get; init; } = [];
    public string ConflictResolution { get; init; } = "last_write";
    public int SourcePriority { get; init; } = 100;
    public string? CustomResolver { get; init; }
}
