using YamlDotNet.Serialization;

namespace ReconPlatform.Config.Models;

// Top-level catalog block attached to a source; describes named queries the source exposes.
public sealed record SourceCatalog
{
    public string Description { get; init; } = string.Empty;
    public List<CatalogQuery> Queries { get; init; } = [];
}

// A single named query that the agent may execute against the source.
public sealed record CatalogQuery
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
    public List<QueryParameter> Parameters { get; init; } = [];

    [YamlMember(Alias = "output_fields")]
    public List<OutputFieldDescriptor> OutputFields { get; init; } = [];
}

// A typed, named parameter that fills a {placeholder} in the query template.
public sealed record QueryParameter
{
    public string Name { get; init; } = string.Empty;

    /// <summary>One of: string, int, double, kql_expression, sql_expression.</summary>
    public string Type { get; init; } = "string";

    public string? Default { get; init; }
    public string Description { get; init; } = string.Empty;
}

// Describes one field returned by the query and its optional mapping to a canonical asset field.
public sealed record OutputFieldDescriptor
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "string";

    [YamlMember(Alias = "maps_to")]
    public string? MapsTo { get; init; }
}
