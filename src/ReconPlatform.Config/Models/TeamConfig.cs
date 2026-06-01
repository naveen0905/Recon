namespace ReconPlatform.Config.Models;

public sealed record TeamConfig
{
    public string Team { get; init; } = string.Empty;

    /// <summary>Default staleness threshold for all sources in this team.</summary>
    public int StaleAfterDays { get; init; } = 7;

    /// <summary>Azure Entra ID app registration for this team.</summary>
    public TeamAuthConfig? Auth { get; init; }

    /// <summary>Team-level dedup defaults; overridden per source.</summary>
    public TeamDeduplicationConfig Dedup { get; init; } = new();

    public IReadOnlyList<SourceConfig> Sources { get; init; } = [];
}

/// <summary>Entra ID identity for this team's app registration.</summary>
public sealed record TeamAuthConfig
{
    /// <summary>{{secret:KEY_NAME}} resolved from Key Vault at runtime.</summary>
    public string EntraAppId { get; init; } = string.Empty;

    /// <summary>{{secret:KEY_NAME}} resolved from Key Vault at runtime.</summary>
    public string TenantId { get; init; } = string.Empty;
}

/// <summary>Team-level dedup settings (source-level settings override these).</summary>
public sealed record TeamDeduplicationConfig
{
    public ConflictResolution DefaultConflictResolution { get; init; } = ConflictResolution.LastWrite;

    /// <summary>
    /// Optional fully-qualified type name for a team-wide custom dedup resolver.
    /// Must implement IDeduplicationResolver and be in the plugins/ directory.
    /// </summary>
    public string? CustomResolver { get; init; }
}
