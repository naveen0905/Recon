namespace ReconPlatform.Storage;

// Stub — full implementation in Task 1.8
public sealed class SqlMetadataClient
{
    public SqlMetadataClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }
}
