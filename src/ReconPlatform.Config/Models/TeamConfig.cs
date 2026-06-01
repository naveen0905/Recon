namespace ReconPlatform.Config.Models;

public sealed record TeamConfig
{
    public string Team { get; init; } = string.Empty;

    public int StaleAfterDays { get; init; } = 7;

    public TeamAuthConfig? Auth { get; init; }

    public TeamDeduplicationConfig Dedup { get; init; } = new();

    public List<SourceConfig> Sources { get; init; } = [];
}

public sealed record TeamAuthConfig
{
    public string EntraAppId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
}

public sealed record TeamDeduplicationConfig
{
    public ConflictResolution DefaultConflictResolution { get; init; } = ConflictResolution.LastWrite;
    public string? CustomResolver { get; init; }
}
