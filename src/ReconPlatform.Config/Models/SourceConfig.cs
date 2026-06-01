using YamlDotNet.Serialization;

namespace ReconPlatform.Config.Models;

public enum SourceType
{
    [YamlMember(Alias = "rest_api")]
    RestApi,

    [YamlMember(Alias = "azure_sql")]
    AzureSql,

    [YamlMember(Alias = "azure_adx")]
    AzureAdx,

    [YamlMember(Alias = "plugin")]
    Plugin,
}

public sealed record SourceConfig
{
    public string Id { get; init; } = string.Empty;
    public SourceType Type { get; init; }
    public int? StaleAfterDays { get; init; }

    // REST API
    public string? BaseUrl { get; init; }
    public AuthConfig? Auth { get; init; }
    public PaginationConfig? Pagination { get; init; }

    // Azure SQL
    public string? ConnectionString { get; init; }
    public string? Query { get; init; }

    // Azure ADX
    public string? Cluster { get; init; }
    public string? Database { get; init; }

    // Plugin
    public string? PluginClass { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];

    public FieldMapping? Mapping { get; init; }
    public DeduplicationConfig Dedup { get; init; } = new();
}

public sealed record PaginationConfig
{
    public string Style { get; init; } = "none";
    public string? PageParam { get; init; }
    public string? CursorPath { get; init; }
    public int PageSize { get; init; } = 100;
}
