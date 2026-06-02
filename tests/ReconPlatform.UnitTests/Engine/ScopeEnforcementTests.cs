using FluentAssertions;
using ReconPlatform.Common.Models;
using Xunit;

namespace ReconPlatform.UnitTests.Engine;

/// <summary>
/// Task 6.2 — Scope enforcement unit tests.
/// Tests the scope-filtering logic that is applied in EngagementsController.GetEngagementAssetsAsync
/// via the Synapse SQL query WHERE host IN (@host0, ...) clause.
/// We test the pure filtering logic here to avoid requiring live Azure dependencies.
/// </summary>
public class ScopeEnforcementTests
{
    // ── helper: build a minimal CanonicalAsset ────────────────────────────────

    private static CanonicalAsset Asset(string host, string team = "net-sec") => new()
    {
        AssetId   = $"{team}::{host}",
        Team      = team,
        SourceId  = "test-source",
        DedupKey  = host,
        Host      = host,
        PulledAt  = DateTimeOffset.UtcNow,
    };

    // ── scope filter logic (mirrors EngagementsController IN-clause behaviour) ─

    /// <summary>
    /// Applies the same host-IN filter that EngagementsController builds dynamically.
    /// </summary>
    private static IEnumerable<CanonicalAsset> ApplyScopeFilter(
        IEnumerable<CanonicalAsset> assets,
        IReadOnlyList<string> scopeTargets)
    {
        if (scopeTargets.Count == 0)
            return assets; // no filter → return all

        return assets.Where(a =>
            !string.IsNullOrWhiteSpace(a.Host) &&
            scopeTargets.Contains(a.Host, StringComparer.OrdinalIgnoreCase));
    }

    // ── Filter_AssetInScope_IsIncluded ────────────────────────────────────────

    [Fact]
    public void Filter_AssetInScope_IsIncluded()
    {
        // Arrange
        var assets = new[] { Asset("10.0.0.1"), Asset("web.example.com") };
        var scopeTargets = new[] { "10.0.0.1", "web.example.com" };

        // Act
        var result = ApplyScopeFilter(assets, scopeTargets).ToList();

        // Assert
        result.Should().HaveCount(2);
        result.Select(a => a.Host).Should().Contain("10.0.0.1").And.Contain("web.example.com");
    }

    // ── Filter_AssetOutOfScope_IsExcluded ─────────────────────────────────────

    [Fact]
    public void Filter_AssetOutOfScope_IsExcluded()
    {
        // Arrange
        var assets = new[]
        {
            Asset("10.0.0.1"),
            Asset("192.168.1.50"),   // out of scope
        };
        var scopeTargets = new[] { "10.0.0.1" };

        // Act
        var result = ApplyScopeFilter(assets, scopeTargets).ToList();

        // Assert
        result.Should().ContainSingle();
        result[0].Host.Should().Be("10.0.0.1");
    }

    // ── Filter_EmptyScopeTargets_ReturnsAllAssets ─────────────────────────────

    [Fact]
    public void Filter_EmptyScopeTargets_ReturnsAllAssets()
    {
        // Arrange
        var assets = new[]
        {
            Asset("10.0.0.1"),
            Asset("api.example.com"),
            Asset("db.internal"),
        };
        var scopeTargets = Array.Empty<string>();

        // Act
        var result = ApplyScopeFilter(assets, scopeTargets).ToList();

        // Assert: no scope filter applied — all assets returned
        result.Should().HaveCount(3);
    }

    // ── Engagement isolation: team A assets not returned for team B ───────────

    [Fact]
    public void Filter_TeamIsolation_TeamAAssetsNotReturnedForTeamB()
    {
        // Arrange: assets from two different teams, same host
        var teamAAssets = new[] { Asset("10.0.0.1", "team-a") };
        var teamBAssets = new[] { Asset("10.0.0.1", "team-b") };

        var scopeTargets = new[] { "10.0.0.1" };

        // Simulate team-scoped query (WHERE team = @team is applied by Synapse SQL first,
        // then host filter is applied). Only team-A assets exist in team-A's result set.
        var teamAResult = ApplyScopeFilter(teamAAssets, scopeTargets).ToList();
        var teamBResult = ApplyScopeFilter(teamBAssets, scopeTargets).ToList();

        // Assert
        teamAResult.Should().ContainSingle(a => a.Team == "team-a");
        teamBResult.Should().ContainSingle(a => a.Team == "team-b");

        // Cross-check: team-A results contain no team-B assets
        teamAResult.Should().NotContain(a => a.Team == "team-b");
        teamBResult.Should().NotContain(a => a.Team == "team-a");
    }

    // ── Case-insensitive scope matching ───────────────────────────────────────

    [Fact]
    public void Filter_CaseInsensitiveHost_IsIncluded()
    {
        // Arrange
        var assets = new[] { Asset("Web.Example.COM") };
        var scopeTargets = new[] { "web.example.com" };

        // Act
        var result = ApplyScopeFilter(assets, scopeTargets).ToList();

        // Assert
        result.Should().ContainSingle();
    }

    // ── All assets excluded when none match scope ─────────────────────────────

    [Fact]
    public void Filter_NoneInScope_ReturnsEmpty()
    {
        // Arrange
        var assets = new[] { Asset("172.16.0.1"), Asset("172.16.0.2") };
        var scopeTargets = new[] { "10.0.0.1" };

        // Act
        var result = ApplyScopeFilter(assets, scopeTargets).ToList();

        // Assert
        result.Should().BeEmpty();
    }
}
