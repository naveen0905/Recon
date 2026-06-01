using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using ReconPlatform.Common.Models;

namespace ReconPlatform.Storage;

/// <summary>
/// Wraps the Cosmos DB SDK for asset document persistence.
///
/// Container layout:
///   database  : configured at construction
///   container : "assets"   (partition key: /team)
///
/// Every upsert increments the document version and records a diff summary.
/// Stale-asset queries rely on the PulledAt field and a caller-supplied cutoff.
/// </summary>
public sealed class CosmosDbClient : IAsyncDisposable
{
    private readonly CosmosClient _client;
    private readonly string _databaseId;
    private readonly ILogger<CosmosDbClient> _logger;

    private const string AssetsContainer = "assets";

    public CosmosDbClient(string endpoint, string databaseId, ILogger<CosmosDbClient> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(logger);

        _databaseId = databaseId;
        _logger = logger;

        _client = new CosmosClient(endpoint, new DefaultAzureCredential(),
            new CosmosClientOptions { SerializerOptions = new() { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase } });
    }

    // For unit tests — accepts a pre-built CosmosClient mock.
    internal CosmosDbClient(CosmosClient client, string databaseId, ILogger<CosmosDbClient> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _databaseId = databaseId;
        _logger = logger;
    }

    /// <summary>
    /// Upserts a CanonicalAsset. If an existing document is found its version is
    /// incremented and a simple diff summary is stored.
    /// Returns the document as stored (with updated version).
    /// </summary>
    public async Task<CanonicalAsset> UpsertAssetAsync(
        CanonicalAsset asset,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var container = GetContainer();
        var existing = await TryGetAsync(container, asset.AssetId, asset.Team, ct).ConfigureAwait(false);

        var toStore = existing is null
            ? asset
            : asset with { Version = existing.Version + 1, LastChanged = ComputeLastChanged(existing, asset) };

        await container.UpsertItemAsync(toStore, new PartitionKey(toStore.Team), cancellationToken: ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Upserted asset assetId={AssetId} team={Team} version={Version}",
            toStore.AssetId, toStore.Team, toStore.Version);

        return toStore;
    }

    /// <summary>
    /// Returns assets for a team whose PulledAt is older than <paramref name="cutoff"/>.
    /// </summary>
    public async Task<IReadOnlyList<CanonicalAsset>> GetStaleAssetsAsync(
        string team,
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);

        var container = GetContainer();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.team = @team AND c.pulledAt < @cutoff")
            .WithParameter("@team", team)
            .WithParameter("@cutoff", cutoff);

        return await ExecuteQueryAsync(container, query, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks an asset with retrigger_scheduled=true.
    /// </summary>
    public async Task MarkRetriggerScheduledAsync(
        string assetId,
        string team,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(team);

        var container = GetContainer();
        var existing = await TryGetAsync(container, assetId, team, ct).ConfigureAwait(false);
        if (existing is null) return;

        var updated = existing with { RetriggerScheduled = true };
        await container.UpsertItemAsync(updated, new PartitionKey(team), cancellationToken: ct)
            .ConfigureAwait(false);

        _logger.LogInformation("Marked retrigger_scheduled for assetId={AssetId}", assetId);
    }

    /// <summary>
    /// Retrieves a single asset by id and team partition.
    /// Returns null if not found.
    /// </summary>
    public async Task<CanonicalAsset?> GetAssetAsync(
        string assetId,
        string team,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(team);

        return await TryGetAsync(GetContainer(), assetId, team, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private Container GetContainer() =>
        _client.GetContainer(_databaseId, AssetsContainer);

    private static async Task<CanonicalAsset?> TryGetAsync(
        Container container, string id, string team, CancellationToken ct)
    {
        try
        {
            var response = await container
                .ReadItemAsync<CanonicalAsset>(id, new PartitionKey(team), cancellationToken: ct)
                .ConfigureAwait(false);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<CanonicalAsset>> ExecuteQueryAsync(
        Container container, QueryDefinition query, CancellationToken ct)
    {
        var results = new List<CanonicalAsset>();
        using var feed = container.GetItemQueryIterator<CanonicalAsset>(query);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct).ConfigureAwait(false);
            results.AddRange(page);
        }

        return results;
    }

    // Returns a changed timestamp only when a meaningful field changed.
    private static DateTimeOffset? ComputeLastChanged(CanonicalAsset existing, CanonicalAsset incoming)
    {
        if (existing.Host != incoming.Host ||
            existing.Ip != incoming.Ip ||
            existing.Port != incoming.Port ||
            existing.Service != incoming.Service ||
            existing.Severity != incoming.Severity ||
            existing.Finding != incoming.Finding)
        {
            return DateTimeOffset.UtcNow;
        }

        return existing.LastChanged;
    }
}
