using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReconPlatform.Common.Models;
using ReconPlatform.Storage;
using System.Net;
using Xunit;

namespace ReconPlatform.UnitTests.Storage;

public class CosmosDbClientTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static CanonicalAsset MakeAsset(string id = "net-sec::abc", string team = "net-sec",
        int version = 1) => new()
    {
        AssetId = id,
        Team = team,
        SourceId = "src",
        DedupKey = "abc",
        Version = version,
        PulledAt = DateTimeOffset.UtcNow,
    };

    private static (CosmosDbClient client, Mock<Container> containerMock) BuildClient()
    {
        var containerMock = new Mock<Container>();
        var dbMock = new Mock<Database>();
        var cosmosMock = new Mock<CosmosClient>();

        cosmosMock
            .Setup(c => c.GetContainer(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(containerMock.Object);

        var client = new CosmosDbClient(cosmosMock.Object, "recon-db",
            NullLogger<CosmosDbClient>.Instance);

        return (client, containerMock);
    }

    // ── UpsertAssetAsync — new document ───────────────────────────────────────

    [Fact]
    public async Task UpsertAssetAsync_NewAsset_UsesVersionOne()
    {
        var (client, containerMock) = BuildClient();
        var asset = MakeAsset();

        // Simulate "not found" for the read
        containerMock
            .Setup(c => c.ReadItemAsync<CanonicalAsset>(
                asset.AssetId, It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Not found", HttpStatusCode.NotFound, 0, string.Empty, 0));

        CanonicalAsset? stored = null;
        containerMock
            .Setup(c => c.UpsertItemAsync(
                It.IsAny<CanonicalAsset>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<CanonicalAsset, PartitionKey?, ItemRequestOptions?, CancellationToken>(
                (item, _, _, _) => stored = item)
            .ReturnsAsync(Mock.Of<ItemResponse<CanonicalAsset>>());

        await client.UpsertAssetAsync(asset);

        stored.Should().NotBeNull();
        stored!.Version.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAssetAsync_ExistingAsset_IncrementsVersion()
    {
        var (client, containerMock) = BuildClient();
        var existing = MakeAsset(version: 3);
        var incoming = MakeAsset(version: 1);

        var itemResponse = Mock.Of<ItemResponse<CanonicalAsset>>(r => r.Resource == existing);
        containerMock
            .Setup(c => c.ReadItemAsync<CanonicalAsset>(
                existing.AssetId, It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemResponse);

        CanonicalAsset? stored = null;
        containerMock
            .Setup(c => c.UpsertItemAsync(
                It.IsAny<CanonicalAsset>(), It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<CanonicalAsset, PartitionKey?, ItemRequestOptions?, CancellationToken>(
                (item, _, _, _) => stored = item)
            .ReturnsAsync(Mock.Of<ItemResponse<CanonicalAsset>>());

        await client.UpsertAssetAsync(incoming);

        stored!.Version.Should().Be(4); // existing.Version(3) + 1
    }

    // ── GetAssetAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAssetAsync_NotFound_ReturnsNull()
    {
        var (client, containerMock) = BuildClient();

        containerMock
            .Setup(c => c.ReadItemAsync<CanonicalAsset>(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CosmosException("Not found", HttpStatusCode.NotFound, 0, string.Empty, 0));

        var result = await client.GetAssetAsync("missing-id", "team");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAssetAsync_Found_ReturnsAsset()
    {
        var (client, containerMock) = BuildClient();
        var asset = MakeAsset();

        var itemResponse = Mock.Of<ItemResponse<CanonicalAsset>>(r => r.Resource == asset);
        containerMock
            .Setup(c => c.ReadItemAsync<CanonicalAsset>(
                asset.AssetId, It.IsAny<PartitionKey>(), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemResponse);

        var result = await client.GetAssetAsync(asset.AssetId, asset.Team);

        result.Should().NotBeNull();
        result!.AssetId.Should().Be(asset.AssetId);
    }

    // ── guard clauses ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAssetAsync_NullAsset_ThrowsArgumentNull()
    {
        var (client, _) = BuildClient();
        var act = async () => await client.UpsertAssetAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
