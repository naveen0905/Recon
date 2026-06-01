using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReconPlatform.Storage;
using Xunit;

namespace ReconPlatform.UnitTests.Storage;

/// <summary>
/// Unit tests for BlobStorageClient.
/// Azure SDK calls are intercepted via the internal test constructor
/// that accepts a pre-built BlobServiceClient mock.
/// </summary>
public class BlobStorageClientTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static BlobStorageClient BuildClient(
        Mock<BlobServiceClient> serviceMock,
        Mock<BlobContainerClient> containerMock)
    {
        serviceMock
            .Setup(s => s.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(containerMock.Object);

        return new BlobStorageClient(serviceMock.Object, NullLogger<BlobStorageClient>.Instance);
    }

    private static Mock<BlobClient> SetupBlobUpload(Mock<BlobContainerClient> containerMock)
    {
        var blobMock = new Mock<BlobClient>();

        containerMock
            .Setup(c => c.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                Response.FromValue(
                    BlobsModelFactory.BlobContainerInfo(ETag.All, DateTimeOffset.UtcNow),
                    Mock.Of<Response>()));

        containerMock
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns(blobMock.Object);

        blobMock
            .Setup(b => b.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(ETag.All, DateTimeOffset.UtcNow, [], string.Empty, 0),
                    Mock.Of<Response>()));

        return blobMock;
    }

    // ── blob path format ──────────────────────────────────────────────────────

    [Fact]
    public async Task UploadPullAsync_ReturnsCorrectBlobPath()
    {
        var serviceMock = new Mock<BlobServiceClient>();
        var containerMock = new Mock<BlobContainerClient>();
        SetupBlobUpload(containerMock);
        var client = BuildClient(serviceMock, containerMock);

        var pulledAt = new DateTimeOffset(2025, 6, 1, 14, 30, 0, TimeSpan.Zero);
        await using var stream = new MemoryStream([1, 2, 3]);

        var path = await client.UploadPullAsync("net-sec", "api-src", pulledAt, stream);

        path.Should().Be("net-sec/api-src/2025/06/01/pull_20250601143000.parquet");
    }

    [Fact]
    public async Task UploadPullAsync_PadsMonthAndDay()
    {
        var serviceMock = new Mock<BlobServiceClient>();
        var containerMock = new Mock<BlobContainerClient>();
        SetupBlobUpload(containerMock);
        var client = BuildClient(serviceMock, containerMock);

        var pulledAt = new DateTimeOffset(2025, 1, 5, 0, 0, 0, TimeSpan.Zero);
        await using var stream = new MemoryStream([1]);

        var path = await client.UploadPullAsync("t", "s", pulledAt, stream);

        path.Should().StartWith("t/s/2025/01/05/");
    }

    // ── container creation ────────────────────────────────────────────────────

    [Fact]
    public async Task UploadPullAsync_CallsCreateIfNotExists_WithNoPublicAccess()
    {
        var serviceMock = new Mock<BlobServiceClient>();
        var containerMock = new Mock<BlobContainerClient>();
        SetupBlobUpload(containerMock);
        var client = BuildClient(serviceMock, containerMock);

        await using var stream = new MemoryStream([1]);
        await client.UploadPullAsync("t", "s", DateTimeOffset.UtcNow, stream);

        containerMock.Verify(
            c => c.CreateIfNotExistsAsync(
                PublicAccessType.None,
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── ListPullsAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListPullsAsync_ToBeforeFrom_ThrowsArgumentException()
    {
        var client = new BlobStorageClient(
            new Mock<BlobServiceClient>().Object,
            NullLogger<BlobStorageClient>.Instance);

        var act = async () => await client.ListPullsAsync(
            "t", "s", new DateOnly(2025, 6, 5), new DateOnly(2025, 6, 1));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListPullsAsync_EmptyContainer_ReturnsEmpty()
    {
        var serviceMock = new Mock<BlobServiceClient>();
        var containerMock = new Mock<BlobContainerClient>();

        containerMock
            .Setup(c => c.GetBlobsAsync(
                It.IsAny<BlobTraits>(),
                It.IsAny<BlobStates>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<BlobItem>.FromPages([]));

        var client = BuildClient(serviceMock, containerMock);

        var result = await client.ListPullsAsync(
            "t", "s", new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 1));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPullsAsync_SameDay_UsesCorrectPrefix()
    {
        var serviceMock = new Mock<BlobServiceClient>();
        var containerMock = new Mock<BlobContainerClient>();

        var capturedPrefixes = new List<string?>();

        containerMock
            .Setup(c => c.GetBlobsAsync(
                It.IsAny<BlobTraits>(),
                It.IsAny<BlobStates>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<BlobTraits, BlobStates, string?, CancellationToken>(
                (_, _, prefix, _) => capturedPrefixes.Add(prefix))
            .Returns(AsyncPageable<BlobItem>.FromPages([]));

        var client = BuildClient(serviceMock, containerMock);

        await client.ListPullsAsync("my-team", "src-1", new DateOnly(2025, 3, 7), new DateOnly(2025, 3, 7));

        capturedPrefixes.Should().ContainSingle().Which.Should().Be("my-team/src-1/2025/03/07/");
    }

    // ── guard clauses ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadPullAsync_NullStream_ThrowsArgumentNull()
    {
        var client = new BlobStorageClient(
            new Mock<BlobServiceClient>().Object,
            NullLogger<BlobStorageClient>.Instance);

        var act = async () => await client.UploadPullAsync("t", "s", DateTimeOffset.UtcNow, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UploadPullAsync_EmptyTeam_ThrowsArgument()
    {
        var client = new BlobStorageClient(
            new Mock<BlobServiceClient>().Object,
            NullLogger<BlobStorageClient>.Instance);

        await using var stream = new MemoryStream([1]);
        var act = async () => await client.UploadPullAsync("", "s", DateTimeOffset.UtcNow, stream);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
