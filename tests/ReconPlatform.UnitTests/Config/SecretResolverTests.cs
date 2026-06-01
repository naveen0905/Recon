using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReconPlatform.Config;
using Xunit;

namespace ReconPlatform.UnitTests.Config;

/// <summary>
/// Unit tests for SecretResolver.
/// Azure Key Vault is never called in unit tests — all scenarios use either
/// the env-var fallback (isDevelopment=true) or a no-KeyVault configuration.
/// </summary>
public class SecretResolverTests : IDisposable
{
    // Environment variable names used in tests — prefixed to avoid collisions.
    private const string EnvKey = "RECON_TEST_SECRET_ALPHA";
    private const string EnvKey2 = "RECON_TEST_SECRET_BETA";

    public SecretResolverTests()
    {
        Environment.SetEnvironmentVariable(EnvKey, "env-value-alpha");
        Environment.SetEnvironmentVariable(EnvKey2, "env-value-beta");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvKey, null);
        Environment.SetEnvironmentVariable(EnvKey2, null);
    }

    private static SecretResolver DevResolver(TimeSpan? ttl = null) =>
        new(keyVaultUrl: null, isDevelopment: true, NullLogger<SecretResolver>.Instance, ttl);

    // ── ResolveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoPlaceholder_ReturnsValueUnchanged()
    {
        var resolver = DevResolver();
        var result = await resolver.ResolveAsync("https://example.com/api");
        result.Should().Be("https://example.com/api");
    }

    [Fact]
    public async Task ResolveAsync_EmptyString_ReturnsEmpty()
    {
        var resolver = DevResolver();
        var result = await resolver.ResolveAsync(string.Empty);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_SinglePlaceholder_ResolvedFromEnv()
    {
        var resolver = DevResolver();
        var result = await resolver.ResolveAsync($"{{{{secret:{EnvKey}}}}}");
        result.Should().Be("env-value-alpha");
    }

    [Fact]
    public async Task ResolveAsync_MultiplePlaceholders_AllResolved()
    {
        var resolver = DevResolver();
        var template = $"user={{{{secret:{EnvKey}}}}};pass={{{{secret:{EnvKey2}}}}}";
        var result = await resolver.ResolveAsync(template);
        result.Should().Be("user=env-value-alpha;pass=env-value-beta");
    }

    [Fact]
    public async Task ResolveAsync_PlaceholderMixedWithLiteral_ReplacesOnlyPlaceholder()
    {
        var resolver = DevResolver();
        var result = await resolver.ResolveAsync($"prefix-{{{{secret:{EnvKey}}}}}-suffix");
        result.Should().Be("prefix-env-value-alpha-suffix");
    }

    // ── env-var fallback ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetSecretAsync_DevMode_ResolvesFromEnv()
    {
        var resolver = DevResolver();
        var value = await resolver.GetSecretAsync(EnvKey);
        value.Should().Be("env-value-alpha");
    }

    [Fact]
    public async Task GetSecretAsync_DevModeEnvNotSet_ThrowsInvalidOperation()
    {
        var resolver = DevResolver();
        var act = async () => await resolver.GetSecretAsync("RECON_NONEXISTENT_KEY_XYZ");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*RECON_NONEXISTENT_KEY_XYZ*");
    }

    [Fact]
    public async Task GetSecretAsync_NotDevModeNoKeyVault_ThrowsInvalidOperation()
    {
        var resolver = new SecretResolver(
            keyVaultUrl: null,
            isDevelopment: false,
            NullLogger<SecretResolver>.Instance);

        var act = async () => await resolver.GetSecretAsync(EnvKey);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSecretAsync_CalledTwice_ReturnsCachedValue()
    {
        var resolver = DevResolver(ttl: TimeSpan.FromHours(1));

        var first = await resolver.GetSecretAsync(EnvKey);
        // Change the env var — cached value should still be returned.
        Environment.SetEnvironmentVariable(EnvKey, "changed-value");
        var second = await resolver.GetSecretAsync(EnvKey);

        first.Should().Be("env-value-alpha");
        second.Should().Be("env-value-alpha");  // still from cache
    }

    [Fact]
    public async Task GetSecretAsync_AfterCacheExpiry_ReFetches()
    {
        var resolver = DevResolver(ttl: TimeSpan.FromMilliseconds(10));

        var first = await resolver.GetSecretAsync(EnvKey);
        Environment.SetEnvironmentVariable(EnvKey, "rotated-value");

        await Task.Delay(50); // let TTL expire
        var second = await resolver.GetSecretAsync(EnvKey);

        first.Should().Be("env-value-alpha");
        second.Should().Be("rotated-value");
    }

    // ── cache invalidation (rotation support) ─────────────────────────────────

    [Fact]
    public async Task InvalidateCache_ForcesRefetch()
    {
        var resolver = DevResolver(ttl: TimeSpan.FromHours(1));

        var first = await resolver.GetSecretAsync(EnvKey);
        Environment.SetEnvironmentVariable(EnvKey, "rotated-value");

        resolver.InvalidateCache(EnvKey);
        var second = await resolver.GetSecretAsync(EnvKey);

        first.Should().Be("env-value-alpha");
        second.Should().Be("rotated-value");
    }

    [Fact]
    public async Task InvalidateAllCache_ForcesRefetchForAllSecrets()
    {
        var resolver = DevResolver(ttl: TimeSpan.FromHours(1));

        await resolver.GetSecretAsync(EnvKey);
        await resolver.GetSecretAsync(EnvKey2);

        Environment.SetEnvironmentVariable(EnvKey, "rotated-alpha");
        Environment.SetEnvironmentVariable(EnvKey2, "rotated-beta");

        resolver.InvalidateAllCache();

        var alpha = await resolver.GetSecretAsync(EnvKey);
        var beta = await resolver.GetSecretAsync(EnvKey2);

        alpha.Should().Be("rotated-alpha");
        beta.Should().Be("rotated-beta");
    }
}
