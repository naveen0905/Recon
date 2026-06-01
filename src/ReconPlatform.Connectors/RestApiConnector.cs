using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using ReconPlatform.Connectors.Interfaces;

namespace ReconPlatform.Connectors;

public sealed class RestApiConnector : IConnector
{
    public string ConnectorType => "rest_api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SecretResolver _secretResolver;
    private readonly ILogger<RestApiConnector> _logger;

    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();

    public RestApiConnector(
        IHttpClientFactory httpClientFactory,
        SecretResolver secretResolver,
        ILogger<RestApiConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public async Task<IEnumerable<Dictionary<string, object>>> PullAsync(
        SourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var client = _httpClientFactory.CreateClient();
        await ApplyAuthAsync(client, config, ct).ConfigureAwait(false);

        var results = new List<Dictionary<string, object>>();
        var pagination = config.Pagination ?? new PaginationConfig { Style = "none" };

        string? cursor = null;
        int page = 0;
        bool hasMore = true;

        while (hasMore)
        {
            var url = BuildUrl(config.BaseUrl!, pagination, page, cursor);

            JToken responseToken = await ConnectorPolicy.Default.ExecuteAsync(
                async pct =>
                {
                    var response = await client.GetAsync(url, pct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync(pct).ConfigureAwait(false);
                    return JToken.Parse(content);
                }, ct).ConfigureAwait(false);

            var items = ExtractItems(responseToken);
            foreach (var item in items)
                results.Add(MapFields(item, config.Mapping));

            _logger.LogInformation(
                "RestApiConnector pulled {Count} items from {SourceId} page {Page}",
                items.Count, config.Id, page);

            switch (pagination.Style)
            {
                case "offset":
                    page++;
                    hasMore = items.Count >= pagination.PageSize;
                    break;

                case "cursor":
                    if (!string.IsNullOrWhiteSpace(pagination.CursorPath))
                    {
                        var nextToken = responseToken.SelectToken(pagination.CursorPath);
                        cursor = nextToken?.Value<string>();
                    }
                    hasMore = !string.IsNullOrEmpty(cursor);
                    break;

                default:
                    hasMore = false;
                    break;
            }
        }

        return results;
    }

    public async Task<bool> TestConnectionAsync(SourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var client = _httpClientFactory.CreateClient();
            await ApplyAuthAsync(client, config, ct).ConfigureAwait(false);

            var request = new HttpRequestMessage(HttpMethod.Head, config.BaseUrl);
            var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var getResponse = await client.GetAsync(config.BaseUrl, ct).ConfigureAwait(false);
                return getResponse.IsSuccessStatusCode;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("TestConnectionAsync failed for {SourceId}: {Message}", config.Id, ex.Message);
            return false;
        }
    }

    private async Task ApplyAuthAsync(HttpClient client, SourceConfig config, CancellationToken ct)
    {
        if (config.Auth is null)
            return;

        switch (config.Auth.Type)
        {
            case AuthType.OAuth2:
                var token = await GetOrRefreshOAuthTokenAsync(config, ct).ConfigureAwait(false);
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                break;

            case AuthType.ApiKey:
                var apiKey = await _secretResolver.ResolveAsync(config.Auth.ApiKey ?? string.Empty, ct)
                    .ConfigureAwait(false);
                var headerName = config.Auth.ApiKeyHeader ?? "X-API-Key";
                if (!string.IsNullOrWhiteSpace(config.Auth.ApiKeyParam))
                {
                    // query-param auth is applied at URL-build time; store key for that path
                    client.DefaultRequestHeaders.TryAddWithoutValidation("X-ApiKey-Param-Name", config.Auth.ApiKeyParam);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("X-ApiKey-Param-Value", apiKey);
                }
                else
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, apiKey);
                }
                break;

            case AuthType.Bearer:
                var bearer = await _secretResolver.ResolveAsync(config.Auth.BearerToken ?? string.Empty, ct)
                    .ConfigureAwait(false);
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", bearer);
                break;
        }
    }

    private async Task<string> GetOrRefreshOAuthTokenAsync(SourceConfig config, CancellationToken ct)
    {
        var cacheKey = config.Id;

        if (_tokenCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            return cached.AccessToken;

        var clientId = await _secretResolver.ResolveAsync(config.Auth!.ClientId ?? string.Empty, ct)
            .ConfigureAwait(false);
        var clientSecret = await _secretResolver.ResolveAsync(config.Auth.ClientSecret ?? string.Empty, ct)
            .ConfigureAwait(false);

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };
        if (!string.IsNullOrWhiteSpace(config.Auth.Scope))
            body["scope"] = config.Auth.Scope;

        var tokenClient = _httpClientFactory.CreateClient();
        var response = await tokenClient.PostAsync(
            config.Auth.TokenUrl,
            new FormUrlEncodedContent(body),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var tokenResponse = JObject.Parse(json);

        var accessToken = tokenResponse["access_token"]?.Value<string>()
            ?? throw new InvalidOperationException("OAuth2 token response missing access_token.");
        var expiresIn = tokenResponse["expires_in"]?.Value<int>() ?? 3600;

        _tokenCache[cacheKey] = new CachedToken(
            accessToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));

        _logger.LogInformation("OAuth2 token acquired for source {SourceId}", config.Id);
        return accessToken;
    }

    private static string BuildUrl(string baseUrl, PaginationConfig pagination, int page, string? cursor)
    {
        if (pagination.Style == "none")
            return baseUrl;

        var separator = baseUrl.Contains('?') ? "&" : "?";

        if (pagination.Style == "offset" && !string.IsNullOrWhiteSpace(pagination.PageParam))
            return $"{baseUrl}{separator}{pagination.PageParam}={page}&pageSize={pagination.PageSize}";

        if (pagination.Style == "cursor" && !string.IsNullOrWhiteSpace(cursor) && !string.IsNullOrWhiteSpace(pagination.PageParam))
            return $"{baseUrl}{separator}{pagination.PageParam}={Uri.EscapeDataString(cursor)}";

        return baseUrl;
    }

    private static List<JObject> ExtractItems(JToken token)
    {
        if (token is JArray array)
            return array.OfType<JObject>().ToList();

        if (token is JObject obj)
        {
            // Try common envelope properties
            foreach (var key in new[] { "data", "items", "results", "value", "records" })
            {
                if (obj[key] is JArray nested)
                    return nested.OfType<JObject>().ToList();
            }
            return [obj];
        }

        return [];
    }

    private static Dictionary<string, object> MapFields(JObject item, FieldMapping? mapping)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (mapping is null)
        {
            foreach (var prop in item.Properties())
                result[prop.Name] = prop.Value.ToObject<object>() ?? string.Empty;
            return result;
        }

        void Map(string targetKey, string? jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath)) return;
            var token = item.SelectToken(jsonPath);
            if (token is not null)
                result[targetKey] = token.ToObject<object>() ?? string.Empty;
        }

        Map("host", mapping.Host);
        Map("ip", mapping.Ip);
        Map("port", mapping.Port);
        Map("service", mapping.Service);
        Map("version", mapping.VersionStr);
        Map("severity", mapping.Severity);
        Map("tags", mapping.Tags);
        Map("owner", mapping.Owner);
        Map("evidence", mapping.Evidence);
        Map("finding", mapping.Finding);
        Map("confidence_score", mapping.ConfidenceScore);

        foreach (var (key, path) in mapping.Extra)
            Map(key, path);

        return result;
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
