namespace ReconPlatform.Storage;

// Stub — full implementation in Task 1.7
public sealed class CosmosDbClient
{
    public CosmosDbClient(string endpoint, string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
    }
}
