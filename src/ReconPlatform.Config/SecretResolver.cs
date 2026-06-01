using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace ReconPlatform.Config;

/// <summary>
/// Resolves {{secret:KEY_NAME}} placeholders from Azure Key Vault (or env vars in dev).
/// Resolved values are cached per secret with a configurable TTL to support rotation
/// without a restart — the cache expires and Key Vault is re-queried automatically.
/// Resolved secret values are NEVER logged (SOC2).
/// </summary>
public sealed class SecretResolver
{
    private static readonly Regex SecretPattern =
        new(@"\{\{secret:([A-Za-z0-9_\-]+)\}\}", RegexOptions.Compiled);

    private readonly SecretClient? _keyVaultClient;
    private readonly bool _isDevelopment;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<SecretResolver> _logger;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public SecretResolver(
        string? keyVaultUrl,
        bool isDevelopment,
        ILogger<SecretResolver> logger,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _isDevelopment = isDevelopment;
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);

        if (!string.IsNullOrWhiteSpace(keyVaultUrl))
            _keyVaultClient = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
    }

    /// <summary>
    /// Resolves all {{secret:KEY_NAME}} placeholders in <paramref name="value"/>.
    /// Returns <paramref name="value"/> unchanged if it contains no placeholders.
    /// </summary>
    public async Task<string> ResolveAsync(string value, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("{{secret:", StringComparison.Ordinal))
            return value;

        var result = value;
        foreach (Match match in SecretPattern.Matches(value))
        {
            var keyName = match.Groups[1].Value;
            var secretValue = await GetSecretAsync(keyName, ct).ConfigureAwait(false);
            result = result.Replace(match.Value, secretValue, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Resolves a single secret by key name. Checks cache first; re-fetches on expiry.
    /// </summary>
    public async Task<string> GetSecretAsync(string keyName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        if (_cache.TryGetValue(keyName, out var entry) && !entry.IsExpired)
            return entry.Value;

        var resolved = await FetchSecretAsync(keyName, ct).ConfigureAwait(false);
        _cache[keyName] = new CacheEntry(resolved, DateTimeOffset.UtcNow.Add(_cacheTtl));

        _logger.LogInformation("Resolved secret {KeyName} (source: {Source})",
            keyName, _isDevelopment && _keyVaultClient is null ? "env" : "keyvault");

        return resolved;
    }

    /// <summary>Evict a cached secret to force re-resolution on next access (rotation).</summary>
    public void InvalidateCache(string keyName) => _cache.TryRemove(keyName, out _);

    /// <summary>Evict all cached secrets.</summary>
    public void InvalidateAllCache() => _cache.Clear();

    private async Task<string> FetchSecretAsync(string keyName, CancellationToken ct)
    {
        // In dev mode, check environment variables first.
        if (_isDevelopment)
        {
            var envValue = Environment.GetEnvironmentVariable(keyName);
            if (!string.IsNullOrWhiteSpace(envValue))
                return envValue;
        }

        if (_keyVaultClient is not null)
        {
            var response = await _keyVaultClient.GetSecretAsync(keyName, version: null, ct)
                .ConfigureAwait(false);
            return response.Value.Value;
        }

        throw new InvalidOperationException(
            $"Secret '{keyName}' not found: no Key Vault configured and environment variable '{keyName}' is not set.");
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
