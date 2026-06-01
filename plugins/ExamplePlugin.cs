using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;

namespace ReconPlatform.Plugins;

/// <summary>
/// Example drop-in connector plugin. Copy this file, rename the class,
/// and register it in your team YAML config under type: plugin.
/// </summary>
public sealed class ExamplePlugin : IConnector
{
    public string ConnectorType => "plugin";

    public async Task<IEnumerable<Dictionary<string, object>>> PullAsync(
        SourceConfig config, CancellationToken ct)
    {
        await Task.Delay(0, ct);
        return [];
    }

    public Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct)
        => Task.FromResult(true);
}
