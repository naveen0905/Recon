namespace ReconPlatform.Storage;

// Stub — full implementation in Task 1.9
public sealed class SynapseClient
{
    public SynapseClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }
}
