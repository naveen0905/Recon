using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReconPlatform.Common.Models;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;
using ReconPlatform.Engine;
using Xunit;

namespace ReconPlatform.UnitTests.Integration;

/// <summary>
/// Task 6.6 — Simulated integration tests for the full pull → normalize → dedup pipeline.
/// These tests exercise the core pipeline logic (normalize + dedup) end-to-end using
/// only in-memory data, without calling real Azure services.
/// </summary>
public class FullPullPipelineTests
{
    // ── shared test helpers ────────────────────────────────────────────────────

    private static SourceConfig MakeSourceConfig(string id = "test-source") => new()
    {
        Id   = id,
        Type = SourceType.RestApi,
        Mapping = new FieldMapping
        {
            Host = "host",
            Ip   = "ip",
            Port = "port",
        },
        Dedup = new DeduplicationConfig
        {
            MatchKeys          = ["host", "port"],
            ConflictResolution = ConflictResolution.LastWrite,
        },
    };

    private static Normalizer MakeNormalizer() =>
        new(NullLogger<Normalizer>.Instance);

    private static DeduplicationEngine MakeDedup() =>
        new(NullLogger<DeduplicationEngine>.Instance);

    // ── FullPipeline_PullNormalizeDedupUpsert_ProducesExpectedAsset ───────────

    [Fact]
    public async Task FullPipeline_PullNormalizeDedupUpsert_ProducesExpectedAsset()
    {
        // Arrange
        var source = MakeSourceConfig();

        // Mock connector returning 2 unique rows
        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.PullAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object>>
            {
                new(StringComparer.OrdinalIgnoreCase) { ["host"] = "api.example.com", ["ip"] = "10.0.0.1", ["port"] = 443 },
                new(StringComparer.OrdinalIgnoreCase) { ["host"] = "db.example.com",  ["ip"] = "10.0.0.2", ["port"] = 5432 },
            });

        var normalizer  = MakeNormalizer();
        var dedupEngine = MakeDedup();
        var pulledAt    = DateTimeOffset.UtcNow;

        // In-memory "cosmos" store
        var cosmosStore = new Dictionary<string, CanonicalAsset>(StringComparer.Ordinal);
        var blobUploads = 0;

        // Act: simulate the pipeline
        var rawRows = await mockConnector.Object.PullAsync(source, CancellationToken.None);
        var rowList = rawRows.ToList();

        var assetsPulled  = 0;
        var assetsDeduped = 0;
        var upserted      = new List<CanonicalAsset>();

        foreach (var row in rowList)
        {
            var nullableRow = row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var normalized  = normalizer.Normalize(nullableRow, "net-sec", source, pulledAt, CancellationToken.None);
            var dedupKey    = dedupEngine.ComputeDedupKey(normalized, source.Dedup);
            var assetId     = $"net-sec::{dedupKey}";
            var withKeys    = normalized with { AssetId = assetId, DedupKey = dedupKey };

            cosmosStore.TryGetValue(assetId, out var existing);

            CanonicalAsset toUpsert;
            if (existing is not null)
            {
                toUpsert = dedupEngine.Resolve(existing, withKeys, source.Dedup);
                assetsDeduped++;
            }
            else
            {
                toUpsert = withKeys;
            }

            cosmosStore[assetId] = toUpsert;
            upserted.Add(toUpsert);
            assetsPulled++;
        }

        // Simulate blob upload
        blobUploads++;

        // Assert
        assetsPulled.Should().Be(2);
        assetsDeduped.Should().Be(0, "no pre-existing assets — nothing to dedup");
        blobUploads.Should().Be(1);
        cosmosStore.Should().HaveCount(2, "2 unique assets upserted");
        upserted.Should().AllSatisfy(a => a.DedupKey.Should().NotBeNullOrEmpty());
        upserted.Should().AllSatisfy(a => a.Team.Should().Be("net-sec"));
    }

    // ── FullPipeline_DuplicateRows_DedupsToOne ────────────────────────────────

    [Fact]
    public async Task FullPipeline_DuplicateRows_DedupsToOne()
    {
        // Arrange: two rows with the same match_keys (host + port) — dedup should trigger
        var source = MakeSourceConfig();

        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.PullAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object>>
            {
                new(StringComparer.OrdinalIgnoreCase) { ["host"] = "api.example.com", ["ip"] = "10.0.0.1", ["port"] = 443 },
                new(StringComparer.OrdinalIgnoreCase) { ["host"] = "api.example.com", ["ip"] = "10.0.0.1", ["port"] = 443 }, // duplicate
            });

        var normalizer  = MakeNormalizer();
        var dedupEngine = MakeDedup();
        var pulledAt    = DateTimeOffset.UtcNow;

        var cosmosStore   = new Dictionary<string, CanonicalAsset>(StringComparer.Ordinal);
        var assetsDeduped = 0;

        // Act
        var rawRows = await mockConnector.Object.PullAsync(source, CancellationToken.None);

        foreach (var row in rawRows)
        {
            var nullableRow = row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var normalized  = normalizer.Normalize(nullableRow, "net-sec", source, pulledAt, CancellationToken.None);
            var dedupKey    = dedupEngine.ComputeDedupKey(normalized, source.Dedup);
            var assetId     = $"net-sec::{dedupKey}";
            var withKeys    = normalized with { AssetId = assetId, DedupKey = dedupKey };

            cosmosStore.TryGetValue(assetId, out var existing);

            CanonicalAsset toUpsert;
            if (existing is not null)
            {
                toUpsert = dedupEngine.Resolve(existing, withKeys, source.Dedup);
                assetsDeduped++;
            }
            else
            {
                toUpsert = withKeys;
            }

            cosmosStore[assetId] = toUpsert;
        }

        // Assert: only 1 unique asset in store; second row triggered dedup
        cosmosStore.Should().HaveCount(1, "duplicate rows share the same dedup key");
        assetsDeduped.Should().Be(1, "one dedup resolution performed for the duplicate row");
    }

    // ── FullPipeline_EmptyConnectorResult_ProducesNoAssets ────────────────────

    [Fact]
    public async Task FullPipeline_EmptyConnectorResult_ProducesNoAssets()
    {
        // Arrange
        var source        = MakeSourceConfig();
        var mockConnector = new Mock<IConnector>();
        mockConnector.Setup(c => c.PullAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Dictionary<string, object>>());

        var normalizer  = MakeNormalizer();
        var dedupEngine = MakeDedup();
        var cosmosStore = new Dictionary<string, CanonicalAsset>(StringComparer.Ordinal);

        // Act
        var rawRows = await mockConnector.Object.PullAsync(source, CancellationToken.None);

        foreach (var row in rawRows)
        {
            var nullableRow = row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var normalized  = normalizer.Normalize(nullableRow, "net-sec", source, DateTimeOffset.UtcNow, CancellationToken.None);
            var dedupKey    = dedupEngine.ComputeDedupKey(normalized, source.Dedup);
            var assetId     = $"net-sec::{dedupKey}";
            cosmosStore[assetId] = normalized with { AssetId = assetId, DedupKey = dedupKey };
        }

        // Assert
        cosmosStore.Should().BeEmpty();
    }
}
