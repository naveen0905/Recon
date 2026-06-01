using ReconPlatform.Config.Models;
using ReconPlatform.Shared.Models;

namespace ReconPlatform.Engine;

// Stub — full implementation in Task 1.10
public sealed class DeduplicationEngine
{
    public string ComputeDedupKey(CanonicalAsset asset, DeduplicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(config);
        throw new NotImplementedException("Task 1.10");
    }

    public CanonicalAsset Resolve(CanonicalAsset existing, CanonicalAsset incoming, DeduplicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(config);
        throw new NotImplementedException("Task 1.10");
    }
}
