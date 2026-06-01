using ReconPlatform.Common.Models;

namespace ReconPlatform.Common.Interfaces;

// Stub — full contract finalised in Task 1.10
public interface IDeduplicationResolver
{
    CanonicalAsset Resolve(CanonicalAsset existing, CanonicalAsset incoming);
}
