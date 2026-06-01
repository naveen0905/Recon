namespace ReconPlatform.Common.Models;

/// <summary>
/// Canonical representation of a recon asset stored in Cosmos DB.
/// All connector output is normalized into this shape before de-duplication and persistence.
/// </summary>
public sealed record CanonicalAsset
{
    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>Stable document ID: "{team}::{dedup_key}"</summary>
    public string AssetId { get; init; } = string.Empty;

    public string Team { get; init; } = string.Empty;

    /// <summary>Source that first or last wrote this record.</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>Hash of match-key values; used as Cosmos partition / upsert key.</summary>
    public string DedupKey { get; init; } = string.Empty;

    // ── Classification ────────────────────────────────────────────────────────

    public string Type { get; init; } = string.Empty;

    // ── Network fields ────────────────────────────────────────────────────────

    public string? Host { get; init; }
    public string? Ip { get; init; }
    public int? Port { get; init; }
    public string? Service { get; init; }
    public string? VersionStr { get; init; }

    // ── Risk fields ───────────────────────────────────────────────────────────

    public string? Severity { get; init; }
    public string? Finding { get; init; }
    public string? Evidence { get; init; }

    // ── Metadata fields ───────────────────────────────────────────────────────

    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Owner { get; init; }

    // ── De-duplication & provenance ───────────────────────────────────────────

    public double ConfidenceScore { get; init; } = 1.0;
    public int SourcePriority { get; init; } = 100;

    /// <summary>All source IDs that have contributed to this merged record.</summary>
    public IReadOnlyList<string> ContributingSources { get; init; } = [];

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public DateTimeOffset PulledAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastChanged { get; init; }
    public bool RetriggerScheduled { get; init; }
    public int Version { get; init; } = 1;

    // ── Raw payload (never logged) ────────────────────────────────────────────

    /// <summary>JSON-serialized original source row. Never exposed in API responses.</summary>
    public string? Raw { get; init; }
}
