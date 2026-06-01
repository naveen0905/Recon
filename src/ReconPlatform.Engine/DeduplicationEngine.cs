using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ReconPlatform.Common.Interfaces;
using ReconPlatform.Common.Models;
using ReconPlatform.Config.Models;

namespace ReconPlatform.Engine;

/// <summary>
/// Computes stable dedup keys and resolves merge conflicts between
/// existing and incoming CanonicalAsset records.
///
/// Conflict strategies (read from DeduplicationConfig):
///   last_write        — incoming always wins
///   highest_confidence — keep whichever asset has the higher ConfidenceScore
///   source_priority   — keep whichever asset has the lower SourcePriority number
///
/// Custom resolvers must implement IDeduplicationResolver and are loaded
/// by the caller (PluginLoader) then passed in as an optional override.
/// </summary>
public sealed class DeduplicationEngine
{
    private readonly ILogger<DeduplicationEngine> _logger;

    public DeduplicationEngine(ILogger<DeduplicationEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Computes a stable, deterministic dedup key from the configured match keys.
    /// Key format: SHA-256( "field1=value1|field2=value2|..." sorted by field name ).
    /// Returns an empty string if no match keys are configured (caller should not upsert).
    /// </summary>
    public string ComputeDedupKey(CanonicalAsset asset, DeduplicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(config);

        if (config.MatchKeys.Count == 0)
            return string.Empty;

        var parts = config.MatchKeys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={ExtractField(asset, k) ?? string.Empty}");

        var raw = string.Join("|", parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Resolves a conflict between an existing and an incoming asset using the
    /// strategy in <paramref name="config"/>. Updates ContributingSources on the winner.
    /// </summary>
    public CanonicalAsset Resolve(
        CanonicalAsset existing,
        CanonicalAsset incoming,
        DeduplicationConfig config,
        IDeduplicationResolver? customResolver = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(config);

        if (customResolver is not null)
        {
            _logger.LogInformation(
                "Dedup: applying custom resolver {Type} for dedup_key={Key}",
                customResolver.GetType().Name, incoming.DedupKey);

            var resolved = customResolver.Resolve(existing, incoming);
            return MergeSources(resolved, existing, incoming);
        }

        var winner = config.ConflictResolution switch
        {
            ConflictResolution.LastWrite => incoming,
            ConflictResolution.HighestConfidence =>
                incoming.ConfidenceScore >= existing.ConfidenceScore ? incoming : existing,
            ConflictResolution.SourcePriority =>
                incoming.SourcePriority <= existing.SourcePriority ? incoming : existing,
            _ => incoming,
        };

        _logger.LogInformation(
            "Dedup: strategy={Strategy} winner={Winner} key={Key}",
            config.ConflictResolution, winner.SourceId, incoming.DedupKey);

        return MergeSources(winner, existing, incoming);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private static CanonicalAsset MergeSources(
        CanonicalAsset winner, CanonicalAsset existing, CanonicalAsset incoming)
    {
        var allSources = existing.ContributingSources
            .Concat(incoming.ContributingSources)
            .Append(existing.SourceId)
            .Append(incoming.SourceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return winner with
        {
            ContributingSources = allSources,
            Version = existing.Version + 1,
        };
    }

    private static string? ExtractField(CanonicalAsset asset, string fieldName) =>
        fieldName switch
        {
            "host"             => asset.Host,
            "ip"               => asset.Ip,
            "port"             => asset.Port?.ToString(),
            "service"          => asset.Service,
            "version_str"      => asset.VersionStr,
            "severity"         => asset.Severity,
            "owner"            => asset.Owner,
            "finding"          => asset.Finding,
            "evidence"         => asset.Evidence,
            "confidence_score" => asset.ConfidenceScore.ToString("F4"),
            "tags"             => string.Join(",", asset.Tags.OrderBy(t => t, StringComparer.Ordinal)),
            _                  => null,
        };
}
