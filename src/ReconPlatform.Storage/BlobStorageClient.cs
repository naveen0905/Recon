namespace ReconPlatform.Storage;

// Stub — full implementation in Task 1.6
public sealed class BlobStorageClient
{
    public BlobStorageClient(string accountUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUrl);
    }
}
