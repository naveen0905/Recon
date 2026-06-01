namespace ReconPlatform.Config.Models;

public enum AuthType
{
    OAuth2,
    ApiKey,
    Bearer,
    ManagedIdentity,
}

public sealed record AuthConfig
{
    public AuthType Type { get; init; }

    // OAuth2 client credentials
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? Scope { get; init; }
    public string? TokenUrl { get; init; }

    // API key
    public string? ApiKey { get; init; }
    public string? ApiKeyHeader { get; init; }   // header name, default: X-API-Key
    public string? ApiKeyParam { get; init; }    // query param name if in query string

    // Bearer
    public string? BearerToken { get; init; }
}
