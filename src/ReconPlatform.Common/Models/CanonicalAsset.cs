namespace ReconPlatform.Common.Models;

// Stub — full implementation in Task 1.11
public sealed record CanonicalAsset
{
    public string AssetId { get; init; } = string.Empty;
    public string Team { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string DedupKey { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Host { get; init; }
    public string? Ip { get; init; }
    public int? Port { get; init; }
    public string? Service { get; init; }
    public string? Severity { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Owner { get; init; }
    public double ConfidenceScore { get; init; } = 1.0;
    public int SourcePriority { get; init; } = 100;
    public IReadOnlyList<string> ContributingSources { get; init; } = [];
    public DateTimeOffset PulledAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Raw { get; init; }
}
