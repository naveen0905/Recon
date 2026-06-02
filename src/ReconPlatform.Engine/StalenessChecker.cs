using Microsoft.Extensions.Logging;
using ReconPlatform.Common.Models;
using ReconPlatform.Config.Models;
using ReconPlatform.Storage;

namespace ReconPlatform.Engine;

/// <summary>
/// Determines whether a <see cref="CanonicalAsset"/> is stale relative to a configured
/// staleness window, and queries Cosmos DB to retrieve all stale assets for a team.
/// </summary>
public sealed class StalenessChecker
{
    private const int DefaultStaleAfterDays = 7;

    private readonly ILogger<StalenessChecker> _logger;

    public StalenessChecker(ILogger<StalenessChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the asset's <see cref="CanonicalAsset.PulledAt"/> is older than
    /// <paramref name="staleAfterDays"/> days ago (relative to UTC now).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="staleAfterDays"/> is less than or equal to zero.
    /// </exception>
    public bool IsStale(CanonicalAsset asset, int staleAfterDays)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (staleAfterDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfterDays),
                staleAfterDays,
                "staleAfterDays must be greater than zero.");
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(staleAfterDays);
        return asset.PulledAt < cutoff;
    }

    /// <summary>
    /// Queries Cosmos DB for all stale assets belonging to <paramref name="team"/>,
    /// using the staleness window configured per source (falling back to
    /// <see cref="DefaultStaleAfterDays"/> when a source does not specify one).
    /// Assets that appear in multiple source queries are deduplicated by
    /// <see cref="CanonicalAsset.AssetId"/>.
    /// </summary>
    public async Task<IReadOnlyList<CanonicalAsset>> FindStaleAssetsAsync(
        string team,
        IReadOnlyList<SourceConfig> sources,
        CosmosDbClient cosmos,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(cosmos);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CanonicalAsset>();

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            var staleAfterDays = source.StaleAfterDays ?? DefaultStaleAfterDays;
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(staleAfterDays);

            var staleAssets = await cosmos
                .GetStaleAssetsAsync(team, cutoff, ct)
                .ConfigureAwait(false);

            foreach (var asset in staleAssets)
            {
                if (seen.Add(asset.AssetId))
                {
                    result.Add(asset);
                }
            }
        }

        _logger.LogInformation(
            "Found {StaleCount} stale asset(s) for team={Team}",
            result.Count,
            team);

        return result;
    }
}
