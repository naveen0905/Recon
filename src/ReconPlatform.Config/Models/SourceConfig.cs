namespace ReconPlatform.Config.Models;

public enum SourceType
{
    RestApi,
    AzureSql,
    AzureAdx,
    Plugin,
}

public sealed record SourceConfig
{
    public string Id { get; init; } = string.Empty;
    public SourceType Type { get; init; }

    /// <summary>Overrides TeamConfig.StaleAfterDays for this source only.</summary>
    public int? StaleAfterDays { get; init; }

    // --- REST API ---
    public string? BaseUrl { get; init; }
    public AuthConfig? Auth { get; init; }
    public PaginationConfig? Pagination { get; init; }

    // --- Azure SQL ---
    public string? ConnectionString { get; init; }   // {{secret:...}} resolved at runtime
    public string? Query { get; init; }

    // --- Azure ADX ---
    public string? Cluster { get; init; }
    public string? Database { get; init; }
    // Query shared with AzureSql above

    // --- Plugin ---
    /// <summary>Fully-qualified type name, e.g. "plugins.MyConnector". Must be in plugins/ dir.</summary>
    public string? PluginClass { get; init; }
    public IReadOnlyDictionary<string, string> Config { get; init; }
        = new Dictionary<string, string>();

    // --- Shared ---
    public FieldMapping? Mapping { get; init; }
    public DeduplicationConfig Dedup { get; init; } = new();
}

/// <summary>Pagination settings for REST API sources.</summary>
public sealed record PaginationConfig
{
    public string Style { get; init; } = "none";   // none | offset | cursor
    public string? PageParam { get; init; }
    public string? CursorPath { get; init; }        // JSONPath to cursor in response
    public int PageSize { get; init; } = 100;
}
