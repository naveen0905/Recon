using ReconPlatform.Config.Models;

namespace ReconPlatform.Connectors.Interfaces;

// Stub — full contract finalised in Task 2.1
public interface IConnector
{
    Task<IEnumerable<Dictionary<string, object>>> PullAsync(SourceConfig config, CancellationToken ct);
    Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct);
}
