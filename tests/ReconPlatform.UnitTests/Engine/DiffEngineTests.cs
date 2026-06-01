using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Common.Models;
using ReconPlatform.Engine;
using Xunit;

namespace ReconPlatform.UnitTests.Engine;

public class DiffEngineTests
{
    private static readonly DiffEngine Engine = new(NullLogger<DiffEngine>.Instance);

    private static CanonicalAsset Base() => new()
    {
        AssetId = "t::key",
        Team = "t",
        SourceId = "s",
        DedupKey = "key",
        Host = "api.example.com",
        Ip = "10.0.0.1",
        Port = 443,
        Service = "HTTPS",
        VersionStr = "1.0",
        Severity = "medium",
        Finding = null,
        Evidence = null,
        Owner = "team-a",
        ConfidenceScore = 0.9,
        Tags = ["internet-facing"],
    };

    // ── no changes ────────────────────────────────────────────────────────────

    [Fact]
    public void Compute_IdenticalAssets_HasChanges_False()
    {
        var a = Base();
        var result = Engine.Compute(a, a);
        result.HasChanges.Should().BeFalse();
        result.ChangedFields.Should().BeEmpty();
        result.AddedTags.Should().BeEmpty();
        result.RemovedTags.Should().BeEmpty();
    }

    // ── scalar field changes ──────────────────────────────────────────────────

    [Fact]
    public void Compute_HostChanged_ReportedInChangedFields()
    {
        var prev = Base();
        var curr = prev with { Host = "new.example.com" };

        var result = Engine.Compute(prev, curr);

        result.HasChanges.Should().BeTrue();
        result.ChangedFields.Should().ContainSingle(f =>
            f.Field == "Host" && f.From == "api.example.com" && f.To == "new.example.com");
    }

    [Fact]
    public void Compute_PortChanged_ReportedAsString()
    {
        var prev = Base();
        var curr = prev with { Port = 8080 };

        var result = Engine.Compute(prev, curr);

        result.ChangedFields.Should().ContainSingle(f =>
            f.Field == "Port" && f.From == "443" && f.To == "8080");
    }

    [Fact]
    public void Compute_ConfidenceScoreChanged_Detected()
    {
        var prev = Base();
        var curr = prev with { ConfidenceScore = 0.5 };

        Engine.Compute(prev, curr).HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Compute_ConfidenceScoreFloatingPointNoise_NotDetected()
    {
        var prev = Base();
        // Difference smaller than epsilon 1e-9 — should be ignored
        var curr = prev with { ConfidenceScore = prev.ConfidenceScore + 1e-12 };

        Engine.Compute(prev, curr).HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Compute_MultipleFieldsChanged_AllReported()
    {
        var prev = Base();
        var curr = prev with { Severity = "critical", Owner = "team-b" };

        var result = Engine.Compute(prev, curr);

        result.ChangedFields.Should().HaveCount(2);
        result.ChangedFields.Should().Contain(f => f.Field == "Severity");
        result.ChangedFields.Should().Contain(f => f.Field == "Owner");
    }

    // ── tag diff ──────────────────────────────────────────────────────────────

    [Fact]
    public void Compute_TagAdded_ReportedInAddedTags()
    {
        var prev = Base();
        var curr = prev with { Tags = ["internet-facing", "critical-asset"] };

        var result = Engine.Compute(prev, curr);

        result.AddedTags.Should().ContainSingle("critical-asset");
        result.RemovedTags.Should().BeEmpty();
        result.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Compute_TagRemoved_ReportedInRemovedTags()
    {
        var prev = Base();
        var curr = prev with { Tags = [] };

        var result = Engine.Compute(prev, curr);

        result.RemovedTags.Should().ContainSingle("internet-facing");
        result.AddedTags.Should().BeEmpty();
    }

    [Fact]
    public void Compute_TagReordered_NoChangesDetected()
    {
        var prev = Base() with { Tags = ["b", "a"] };
        var curr = prev with { Tags = ["a", "b"] };

        Engine.Compute(prev, curr).HasChanges.Should().BeFalse();
    }

    // ── guard clauses ─────────────────────────────────────────────────────────

    [Fact]
    public void Compute_NullPrevious_ThrowsArgumentNull()
    {
        var act = () => Engine.Compute(null!, Base());
        act.Should().Throw<ArgumentNullException>();
    }
}
