using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Common.Models;
using ReconPlatform.Engine;
using Xunit;

namespace ReconPlatform.UnitTests.Engine;

public class StalenessCheckerTests
{
    private static readonly StalenessChecker Checker =
        new(NullLogger<StalenessChecker>.Instance);

    private static CanonicalAsset Asset(DateTimeOffset pulledAt) => new()
    {
        AssetId = "t::k",
        Team = "t",
        SourceId = "s",
        DedupKey = "k",
        PulledAt = pulledAt,
    };

    // ── IsStale ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsStale_PulledAtWithinWindow_ReturnsFalse()
    {
        var asset = Asset(DateTimeOffset.UtcNow.AddDays(-3));
        Checker.IsStale(asset, staleAfterDays: 7).Should().BeFalse();
    }

    [Fact]
    public void IsStale_PulledAtBeyondWindow_ReturnsTrue()
    {
        var asset = Asset(DateTimeOffset.UtcNow.AddDays(-8));
        Checker.IsStale(asset, staleAfterDays: 7).Should().BeTrue();
    }

    [Fact]
    public void IsStale_ExactlyAtBoundary_ReturnsFalse()
    {
        // PulledAt == UtcNow - staleAfterDays is NOT stale (strictly less than)
        var asset = Asset(DateTimeOffset.UtcNow.AddDays(-7).AddSeconds(1));
        Checker.IsStale(asset, staleAfterDays: 7).Should().BeFalse();
    }

    [Fact]
    public void IsStale_ZeroStaleAfterDays_ThrowsArgumentOutOfRange()
    {
        var asset = Asset(DateTimeOffset.UtcNow);
        var act = () => Checker.IsStale(asset, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsStale_NegativeStaleAfterDays_ThrowsArgumentOutOfRange()
    {
        var asset = Asset(DateTimeOffset.UtcNow);
        var act = () => Checker.IsStale(asset, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsStale_SingleDayWindow_OldAsset_ReturnsTrue()
    {
        var asset = Asset(DateTimeOffset.UtcNow.AddDays(-2));
        Checker.IsStale(asset, staleAfterDays: 1).Should().BeTrue();
    }
}
