using FluentAssertions;
using ReconPlatform.Connectors;
using System.Threading.RateLimiting;
using Xunit;

namespace ReconPlatform.UnitTests.Connectors;

/// <summary>
/// Task 6.3 — Unit tests for <see cref="ConnectorRateLimiter"/>.
/// </summary>
public class ConnectorRateLimiterTests
{
    // ── AcquireAsync_BelowLimit_Succeeds ──────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_BelowLimit_Succeeds()
    {
        // Arrange
        using var limiter = new ConnectorRateLimiter();

        // Act
        using var lease = await limiter.AcquireAsync("source-1", permitLimit: 10);

        // Assert
        lease.Should().NotBeNull();
        lease.IsAcquired.Should().BeTrue();
    }

    // ── AcquireAsync_ExceedsLimit_IsThrottled ─────────────────────────────────

    [Fact]
    public async Task AcquireAsync_ExceedsLimit_IsThrottled()
    {
        // Arrange: limit=1 so second call should fail (QueueLimit=0 means fail-fast)
        using var limiter = new ConnectorRateLimiter();

        // Act
        using var lease1 = await limiter.AcquireAsync("source-throttle", permitLimit: 1);
        using var lease2 = await limiter.AcquireAsync("source-throttle", permitLimit: 1);

        // Assert: first lease acquired, second denied
        lease1.IsAcquired.Should().BeTrue();
        lease2.IsAcquired.Should().BeFalse("sliding window with 1 permit should deny the second request");
    }

    // ── AcquireAsync_DifferentSources_IndependentLimits ───────────────────────

    [Fact]
    public async Task AcquireAsync_DifferentSources_IndependentLimits()
    {
        // Arrange
        using var limiter = new ConnectorRateLimiter();

        // Act: acquire on two distinct sources — each has its own window
        using var leaseA1 = await limiter.AcquireAsync("source-a", permitLimit: 1);
        using var leaseA2 = await limiter.AcquireAsync("source-a", permitLimit: 1); // should fail
        using var leaseB1 = await limiter.AcquireAsync("source-b", permitLimit: 1); // independent

        // Assert
        leaseA1.IsAcquired.Should().BeTrue();
        leaseA2.IsAcquired.Should().BeFalse();
        leaseB1.IsAcquired.Should().BeTrue("source-b has its own fresh window");
    }

    // ── AcquireAsync_EmptySourceId_ThrowsArgument ─────────────────────────────

    [Fact]
    public async Task AcquireAsync_EmptySourceId_ThrowsArgument()
    {
        using var limiter = new ConnectorRateLimiter();
        var act = async () => await limiter.AcquireAsync("", permitLimit: 60);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Dispose_ReleasesResources ─────────────────────────────────────────────

    [Fact]
    public void Dispose_ReleasesResources()
    {
        // Arrange
        var limiter = new ConnectorRateLimiter();

        // Act
        var act = () => limiter.Dispose();

        // Assert: no exception
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var limiter = new ConnectorRateLimiter();

        // Act
        limiter.Dispose();
        var act = () => limiter.Dispose();

        // Assert: idempotent dispose
        act.Should().NotThrow();
    }
}
