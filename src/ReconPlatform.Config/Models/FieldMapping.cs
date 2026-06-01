namespace ReconPlatform.Config.Models;

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
    public Dictionary<string, string> Extra { get; init; } = [];
}
