using Microsoft.Extensions.Logging;
using ReconPlatform.Common.Models;

namespace ReconPlatform.Engine;

/// <summary>
/// Computes a structured diff between two versions of a <see cref="CanonicalAsset"/>.
/// Used to detect meaningful changes before persisting an upsert and to drive
/// change-notification workflows.
/// </summary>
public sealed class DiffEngine
{
    private readonly ILogger<DiffEngine> _logger;

    public DiffEngine(ILogger<DiffEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Compares <paramref name="previous"/> and <paramref name="current"/> and returns
    /// a structured <see cref="AssetDiff"/>.
    /// </summary>
    public AssetDiff Compute(CanonicalAsset previous, CanonicalAsset current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var changedFields = new List<FieldChange>();

        void CheckString(string fieldName, string? prev, string? curr)
        {
            if (!string.Equals(prev, curr, StringComparison.Ordinal))
                changedFields.Add(new FieldChange(fieldName, prev, curr));
        }

        void CheckInt(string fieldName, int? prev, int? curr)
        {
            if (prev != curr)
                changedFields.Add(new FieldChange(fieldName, prev?.ToString(), curr?.ToString()));
        }

        void CheckDouble(string fieldName, double prev, double curr)
        {
            // Use a small epsilon to avoid floating-point noise
            if (Math.Abs(prev - curr) > 1e-9)
                changedFields.Add(new FieldChange(fieldName,
                    prev.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    curr.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
        }

        CheckString(nameof(CanonicalAsset.Host),       previous.Host,       current.Host);
        CheckString(nameof(CanonicalAsset.Ip),         previous.Ip,         current.Ip);
        CheckInt   (nameof(CanonicalAsset.Port),        previous.Port,       current.Port);
        CheckString(nameof(CanonicalAsset.Service),    previous.Service,    current.Service);
        CheckString(nameof(CanonicalAsset.VersionStr), previous.VersionStr, current.VersionStr);
        CheckString(nameof(CanonicalAsset.Severity),   previous.Severity,   current.Severity);
        CheckString(nameof(CanonicalAsset.Finding),    previous.Finding,    current.Finding);
        CheckString(nameof(CanonicalAsset.Evidence),   previous.Evidence,   current.Evidence);
        CheckString(nameof(CanonicalAsset.Owner),      previous.Owner,      current.Owner);
        CheckDouble(nameof(CanonicalAsset.ConfidenceScore), previous.ConfidenceScore, current.ConfidenceScore);

        // Tag diff
        var prevTags = previous.Tags.ToHashSet(StringComparer.Ordinal);
        var currTags = current.Tags.ToHashSet(StringComparer.Ordinal);

        var addedTags   = currTags.Except(prevTags).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var removedTags = prevTags.Except(currTags).OrderBy(t => t, StringComparer.Ordinal).ToList();

        var hasChanges = changedFields.Count > 0 || addedTags.Count > 0 || removedTags.Count > 0;

        if (hasChanges)
        {
            _logger.LogInformation(
                "DiffEngine: asset={AssetId} changed_fields={FieldCount} tags_added={Added} tags_removed={Removed}",
                current.AssetId, changedFields.Count, addedTags.Count, removedTags.Count);
        }
        else
        {
            _logger.LogDebug("DiffEngine: asset={AssetId} no changes detected", current.AssetId);
        }

        return new AssetDiff(
            HasChanges: hasChanges,
            AddedTags: addedTags,
            RemovedTags: removedTags,
            ChangedFields: changedFields);
    }
}

/// <summary>A single scalar field change between two asset versions.</summary>
public sealed record FieldChange(string Field, string? From, string? To);

/// <summary>Structured difference between two <see cref="CanonicalAsset"/> versions.</summary>
public sealed record AssetDiff(
    bool HasChanges,
    IReadOnlyList<string> AddedTags,
    IReadOnlyList<string> RemovedTags,
    IReadOnlyList<FieldChange> ChangedFields);
