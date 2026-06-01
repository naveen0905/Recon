using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Common.Interfaces;
using ReconPlatform.Common.Models;
using ReconPlatform.Config.Models;
using ReconPlatform.Engine;
using Xunit;

namespace ReconPlatform.UnitTests.Engine;

public class DeduplicationEngineTests
{
    private static readonly DeduplicationEngine Engine =
        new(NullLogger<DeduplicationEngine>.Instance);

    private static CanonicalAsset Asset(
        string sourceId = "src-a",
        string? host = "api.example.com",
        string? ip = "1.2.3.4",
        int? port = 443,
        double confidence = 0.9,
        int priority = 100) => new()
        {
            AssetId = $"team::{sourceId}",
            Team = "net-sec",
            SourceId = sourceId,
            DedupKey = string.Empty,
            Host = host,
            Ip = ip,
            Port = port,
            ConfidenceScore = confidence,
            SourcePriority = priority,
        };

    private static DeduplicationConfig Config(
        ConflictResolution strategy = ConflictResolution.LastWrite,
        params string[] matchKeys) =>
        new()
        {
            ConflictResolution = strategy,
            MatchKeys = [..matchKeys],
        };

    // ── ComputeDedupKey ───────────────────────────────────────────────────────

    [Fact]
    public void ComputeDedupKey_SameAsset_ReturnsSameKey()
    {
        var cfg = Config(matchKeys: ["host", "port"]);
        var k1 = Engine.ComputeDedupKey(Asset(), cfg);
        var k2 = Engine.ComputeDedupKey(Asset(), cfg);
        k1.Should().Be(k2).And.NotBeEmpty();
    }

    [Fact]
    public void ComputeDedupKey_DifferentPort_ReturnsDifferentKey()
    {
        var cfg = Config(matchKeys: ["host", "port"]);
        var k1 = Engine.ComputeDedupKey(Asset(port: 443), cfg);
        var k2 = Engine.ComputeDedupKey(Asset(port: 80), cfg);
        k1.Should().NotBe(k2);
    }

    [Fact]
    public void ComputeDedupKey_NoMatchKeys_ReturnsEmpty()
    {
        var cfg = Config();
        Engine.ComputeDedupKey(Asset(), cfg).Should().BeEmpty();
    }

    [Fact]
    public void ComputeDedupKey_KeyOrderDoesNotMatter()
    {
        var a = Asset();
        var k1 = Engine.ComputeDedupKey(a, Config(matchKeys: ["host", "port"]));
        var k2 = Engine.ComputeDedupKey(a, Config(matchKeys: ["port", "host"]));
        k1.Should().Be(k2);
    }

    [Fact]
    public void ComputeDedupKey_ReturnsLowercaseHex()
    {
        var key = Engine.ComputeDedupKey(Asset(), Config(matchKeys: ["host"]));
        key.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ── Resolve — last_write ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_LastWrite_IncomingAlwaysWins()
    {
        var existing = Asset("src-a", confidence: 0.99);
        var incoming = Asset("src-b", confidence: 0.1);
        var cfg = Config(ConflictResolution.LastWrite);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.SourceId.Should().Be("src-b");
    }

    // ── Resolve — highest_confidence ─────────────────────────────────────────

    [Fact]
    public void Resolve_HighestConfidence_KeepsHigherScore()
    {
        var existing = Asset("src-a", confidence: 0.95);
        var incoming = Asset("src-b", confidence: 0.5);
        var cfg = Config(ConflictResolution.HighestConfidence);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.SourceId.Should().Be("src-a");
    }

    [Fact]
    public void Resolve_HighestConfidence_IncomingWinsWhenEqual()
    {
        var existing = Asset("src-a", confidence: 0.8);
        var incoming = Asset("src-b", confidence: 0.8);
        var cfg = Config(ConflictResolution.HighestConfidence);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.SourceId.Should().Be("src-b");
    }

    // ── Resolve — source_priority ─────────────────────────────────────────────

    [Fact]
    public void Resolve_SourcePriority_LowerNumberWins()
    {
        var existing = Asset("src-a", priority: 50);
        var incoming = Asset("src-b", priority: 200);
        var cfg = Config(ConflictResolution.SourcePriority);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.SourceId.Should().Be("src-a");
    }

    [Fact]
    public void Resolve_SourcePriority_IncomingWinsOnTie()
    {
        var existing = Asset("src-a", priority: 100);
        var incoming = Asset("src-b", priority: 100);
        var cfg = Config(ConflictResolution.SourcePriority);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.SourceId.Should().Be("src-b");
    }

    // ── contributing sources merge ────────────────────────────────────────────

    [Fact]
    public void Resolve_MergesContributingSources()
    {
        var existing = Asset("src-a") with { ContributingSources = ["src-a"] };
        var incoming = Asset("src-b") with { ContributingSources = ["src-b"] };
        var cfg = Config(ConflictResolution.LastWrite);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.ContributingSources.Should().Contain("src-a").And.Contain("src-b");
    }

    [Fact]
    public void Resolve_DeduplicatesContributingSources()
    {
        var existing = Asset("src-a") with { ContributingSources = ["src-a", "src-c"] };
        var incoming = Asset("src-b") with { ContributingSources = ["src-a", "src-b"] };
        var cfg = Config(ConflictResolution.LastWrite);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.ContributingSources.Should().HaveCount(3)
            .And.Contain(["src-a", "src-b", "src-c"]);
    }

    // ── version increment ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_IncrementsVersion()
    {
        var existing = Asset() with { Version = 3 };
        var incoming = Asset();
        var cfg = Config(ConflictResolution.LastWrite);

        var result = Engine.Resolve(existing, incoming, cfg);

        result.Version.Should().Be(4);
    }

    // ── custom resolver ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_CustomResolver_IsUsedInsteadOfStrategy()
    {
        var existing = Asset("src-a");
        var incoming = Asset("src-b");
        var cfg = Config(ConflictResolution.LastWrite);

        // Custom resolver always returns existing
        var customResolver = new AlwaysExistingResolver();
        var result = Engine.Resolve(existing, incoming, cfg, customResolver);

        result.SourceId.Should().Be("src-a");
    }

    private sealed class AlwaysExistingResolver : IDeduplicationResolver
    {
        public CanonicalAsset Resolve(CanonicalAsset existing, CanonicalAsset incoming) => existing;
    }
}
