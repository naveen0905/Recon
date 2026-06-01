using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Storage;

/// <summary>
/// Writes raw pull snapshots to Azure Blob Storage as Parquet files and lists
/// previously stored pulls for a given team/source/date range.
///
/// Path convention: {container}/{team}/{source}/{yyyy}/{MM}/{dd}/pull_{timestamp:yyyyMMddHHmmss}.parquet
/// Managed identity is used when no connection string is provided.
/// </summary>
public sealed class BlobStorageClient
{
    private readonly BlobServiceClient _serviceClient;
    private readonly ILogger<BlobStorageClient> _logger;

    // Default container name; callers can override per operation.
    private const string DefaultContainer = "recon-pulls";

    public BlobStorageClient(string accountUrl, ILogger<BlobStorageClient> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUrl);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceClient = new BlobServiceClient(new Uri(accountUrl), new DefaultAzureCredential());
        _logger = logger;
    }

    // For unit tests — allows injecting a pre-built/mocked BlobServiceClient.
    internal BlobStorageClient(BlobServiceClient serviceClient, ILogger<BlobStorageClient> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceClient = serviceClient;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a Parquet payload for a completed connector pull.
    /// Returns the full blob path that was written.
    /// </summary>
    public async Task<string> UploadPullAsync(
        string team,
        string sourceId,
        DateTimeOffset pulledAt,
        Stream parquetData,
        CancellationToken ct = default,
        string container = DefaultContainer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(parquetData);

        var blobPath = BuildBlobPath(team, sourceId, pulledAt);
        var containerClient = _serviceClient.GetBlobContainerClient(container);

        await containerClient.CreateIfNotExistsAsync(
            PublicAccessType.None, metadata: null, encryptionScopeOptions: null, ct).ConfigureAwait(false);

        var blobClient = containerClient.GetBlobClient(blobPath);

        var uploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
            Metadata = new Dictionary<string, string>
            {
                ["team"] = team,
                ["source_id"] = sourceId,
                ["pulled_at"] = pulledAt.ToString("O"),
            },
        };

        await blobClient.UploadAsync(parquetData, uploadOptions, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Uploaded pull snapshot: container={Container} path={BlobPath}",
            container, blobPath);

        return blobPath;
    }

    /// <summary>
    /// Lists blob paths for a given team/source between <paramref name="from"/> and
    /// <paramref name="to"/> (inclusive, day granularity). Returns paths in ascending order.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListPullsAsync(
        string team,
        string sourceId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default,
        string container = DefaultContainer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(team);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        if (to < from)
            throw new ArgumentException("'to' must be >= 'from'.", nameof(to));

        var containerClient = _serviceClient.GetBlobContainerClient(container);
        var results = new List<string>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var prefix = $"{team}/{sourceId}/{date.Year:D4}/{date.Month:D2}/{date.Day:D2}/";

            await foreach (var item in containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
                results.Add(item.Name);
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    /// <summary>
    /// Downloads a previously stored pull snapshot by its blob path.
    /// </summary>
    public async Task<Stream> DownloadPullAsync(
        string blobPath,
        CancellationToken ct = default,
        string container = DefaultContainer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);

        var blobClient = _serviceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
        return response.Value.Content;
    }

    // {team}/{source}/{yyyy}/{MM}/{dd}/pull_{yyyyMMddHHmmss}.parquet
    private static string BuildBlobPath(string team, string sourceId, DateTimeOffset pulledAt)
    {
        var ts = pulledAt.UtcDateTime;
        return $"{team}/{sourceId}/{ts.Year:D4}/{ts.Month:D2}/{ts.Day:D2}/pull_{ts:yyyyMMddHHmmss}.parquet";
    }
}
