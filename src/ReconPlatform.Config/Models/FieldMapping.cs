namespace ReconPlatform.Config.Models;

/// <summary>
/// Maps source response fields to canonical schema fields.
/// Values may be JSONPath expressions (e.g. "$.data[*].hostname") or plain field names.
/// </summary>
public sealed record FieldMapping
{
    public string? Host { get; init; }
    public string? Ip { get; init; }
    public string? Port { get; init; }
    public string? Service { get; init; }
    public string? VersionStr { get; init; }
    public string? Severity { get; init; }
    public string? Tags { get; init; }
    public string? Owner { get; init; }
    public string? Evidence { get; init; }
    public string? Finding { get; init; }
    public string? ConfidenceScore { get; init; }

    /// <summary>Additional custom mappings not covered by the canonical fields.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; }
        = new Dictionary<string, string>();
}
