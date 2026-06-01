using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReconPlatform.Common.Models;
using ReconPlatform.Config.Models;

namespace ReconPlatform.Engine;

/// <summary>
/// Maps a raw source row (Dictionary of string→object?) into a <see cref="CanonicalAsset"/>
/// using the <see cref="FieldMapping"/> declared in the <see cref="SourceConfig"/>.
///
/// Mapping values are either:
///   • a plain column name (e.g. "host")  → direct dictionary look-up
///   • a JSONPath expression (e.g. "$.data.hostname") → evaluated against a JObject
///     built from the row
///
/// AssetId and DedupKey are intentionally left empty; the caller (DeduplicationEngine)
/// fills them in after normalization.
/// </summary>
public sealed class Normalizer
{
    private readonly ILogger<Normalizer> _logger;

    public Normalizer(ILogger<Normalizer> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Normalizes a raw source <paramref name="row"/> into a <see cref="CanonicalAsset"/>.
    /// </summary>
    /// <param name="row">Raw key-value pairs from the connector.</param>
    /// <param name="team">Team that owns the source.</param>
    /// <param name="source">Source configuration including field mapping.</param>
    /// <param name="pulledAt">Timestamp when the connector fetched the data.</param>
    public CanonicalAsset Normalize(
        Dictionary<string, object?> row,
        string team,
        SourceConfig source,
        DateTimeOffset pulledAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(source);

        cancellationToken.ThrowIfCancellationRequested();

        var mapping = source.Mapping;

        // Build a JObject once so JSONPath expressions can be evaluated efficiently.
        JObject? jRow = null;

        string? Resolve(string? mappingKey)
        {
            if (string.IsNullOrWhiteSpace(mappingKey))
                return null;

            if (mappingKey.StartsWith('$'))
            {
                // JSONPath — build the JObject lazily
                jRow ??= JObject.FromObject(row);
                var token = jRow.SelectToken(mappingKey);
                return token?.ToString();
            }

            // Plain column name
            if (row.TryGetValue(mappingKey, out var val) && val is not null)
                return val.ToString();

            return null;
        }

        int? ResolvePort(string? mappingKey)
        {
            var raw = Resolve(mappingKey);
            if (raw is null) return null;
            return int.TryParse(raw, out var p) ? p : null;
        }

        double ResolveConfidence(string? mappingKey)
        {
            var raw = Resolve(mappingKey);
            if (raw is null) return 1.0;
            return double.TryParse(raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var score) ? score : 1.0;
        }

        var raw = JsonConvert.SerializeObject(row);

        _logger.LogDebug(
            "Normalizing row for source={SourceId} team={Team}",
            source.Id, team);

        return new CanonicalAsset
        {
            // Identity — AssetId and DedupKey filled by DeduplicationEngine after this call
            AssetId = string.Empty,
            DedupKey = string.Empty,
            Team = team,
            SourceId = source.Id,

            // Network
            Host = Resolve(mapping?.Host),
            Ip = Resolve(mapping?.Ip),
            Port = ResolvePort(mapping?.Port),
            Service = Resolve(mapping?.Service),
            VersionStr = Resolve(mapping?.VersionStr),

            // Risk
            Severity = Resolve(mapping?.Severity),
            Finding = Resolve(mapping?.Finding),
            Evidence = Resolve(mapping?.Evidence),

            // Metadata
            Owner = Resolve(mapping?.Owner),
            ConfidenceScore = ResolveConfidence(mapping?.ConfidenceScore),
            Tags = [],

            // De-duplication / provenance
            SourcePriority = source.Dedup.SourcePriority,
            ContributingSources = [],

            // Lifecycle
            PulledAt = pulledAt,
            LastChanged = null,
            RetriggerScheduled = false,
            Version = 1,

            // Raw payload — never logged
            Raw = raw,
        };
    }
}
