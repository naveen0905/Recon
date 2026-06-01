using ReconPlatform.Config.Models;

namespace ReconPlatform.Config;

public static class TeamConfigValidator
{
    private static readonly HashSet<string> KnownAssetFields =
    [
        "host", "ip", "port", "service", "version_str",
        "severity", "tags", "owner", "evidence", "finding", "confidence_score",
    ];

    public static ValidationResult Validate(TeamConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(config.Team))
            errors.Add(new ValidationError("team", "team is required"));

        if (config.StaleAfterDays <= 0)
            errors.Add(new ValidationError("stale_after_days", "stale_after_days must be greater than 0"));

        for (var i = 0; i < config.Sources.Count; i++)
            ValidateSource(config.Sources[i], $"sources[{i}]", errors);

        return errors.Count > 0 ? ValidationResult.Failure(errors) : ValidationResult.Success();
    }

    private static void ValidateSource(SourceConfig source, string prefix, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(source.Id))
            errors.Add(new ValidationError($"{prefix}.id", "id is required"));

        if (source.StaleAfterDays.HasValue && source.StaleAfterDays.Value <= 0)
            errors.Add(new ValidationError($"{prefix}.stale_after_days", "stale_after_days must be greater than 0"));

        switch (source.Type)
        {
            case SourceType.RestApi:
                ValidateRestApi(source, prefix, errors);
                break;
            case SourceType.AzureSql:
                ValidateAzureSql(source, prefix, errors);
                break;
            case SourceType.AzureAdx:
                ValidateAzureAdx(source, prefix, errors);
                break;
            case SourceType.Plugin:
                ValidatePlugin(source, prefix, errors);
                break;
        }

        ValidateDedup(source.Dedup, $"{prefix}.dedup", errors);
    }

    private static void ValidateRestApi(SourceConfig source, string prefix, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
            errors.Add(new ValidationError($"{prefix}.base_url", "base_url is required for rest_api sources"));

        if (source.Auth is null)
        {
            errors.Add(new ValidationError($"{prefix}.auth", "auth is required for rest_api sources"));
        }
        else
        {
            ValidateAuth(source.Auth, $"{prefix}.auth", source.Type, errors);
        }
    }

    private static void ValidateAzureSql(SourceConfig source, string prefix, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(source.ConnectionString) && source.Auth?.Type != AuthType.ManagedIdentity)
            errors.Add(new ValidationError($"{prefix}.connection_string", "connection_string is required for azure_sql unless using managed_identity auth"));

        if (string.IsNullOrWhiteSpace(source.Query))
            errors.Add(new ValidationError($"{prefix}.query", "query is required for azure_sql sources"));

        if (source.Auth is not null && source.Auth.Type == AuthType.OAuth2)
            errors.Add(new ValidationError($"{prefix}.auth.type", "oauth2 auth is not supported for azure_sql; use managed_identity or connection_string"));

        if (source.Auth is not null && source.Auth.Type == AuthType.ApiKey)
            errors.Add(new ValidationError($"{prefix}.auth.type", "api_key auth is not supported for azure_sql; use managed_identity or connection_string"));
    }

    private static void ValidateAzureAdx(SourceConfig source, string prefix, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(source.Cluster))
            errors.Add(new ValidationError($"{prefix}.cluster", "cluster is required for azure_adx sources"));

        if (string.IsNullOrWhiteSpace(source.Database))
            errors.Add(new ValidationError($"{prefix}.database", "database is required for azure_adx sources"));

        if (string.IsNullOrWhiteSpace(source.Query))
            errors.Add(new ValidationError($"{prefix}.query", "query is required for azure_adx sources"));

        if (source.Auth is not null && source.Auth.Type != AuthType.ManagedIdentity)
            errors.Add(new ValidationError($"{prefix}.auth.type", "azure_adx only supports managed_identity auth"));
    }

    private static void ValidatePlugin(SourceConfig source, string prefix, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(source.PluginClass))
        {
            errors.Add(new ValidationError($"{prefix}.plugin_class", "plugin_class is required for plugin sources"));
        }
        else if (!source.PluginClass.StartsWith("plugins.", StringComparison.Ordinal))
        {
            errors.Add(new ValidationError($"{prefix}.plugin_class", "plugin_class must be in the plugins/ directory (must start with 'plugins.')"));
        }
    }

    private static void ValidateAuth(AuthConfig auth, string prefix, SourceType sourceType, List<ValidationError> errors)
    {
        switch (auth.Type)
        {
            case AuthType.OAuth2:
                if (string.IsNullOrWhiteSpace(auth.ClientId))
                    errors.Add(new ValidationError($"{prefix}.client_id", "client_id is required for oauth2 auth"));
                if (string.IsNullOrWhiteSpace(auth.ClientSecret))
                    errors.Add(new ValidationError($"{prefix}.client_secret", "client_secret is required for oauth2 auth"));
                break;

            case AuthType.ApiKey:
                if (string.IsNullOrWhiteSpace(auth.ApiKey))
                    errors.Add(new ValidationError($"{prefix}.api_key", "api_key is required for api_key auth"));
                if (string.IsNullOrWhiteSpace(auth.ApiKeyHeader) && string.IsNullOrWhiteSpace(auth.ApiKeyParam))
                    errors.Add(new ValidationError($"{prefix}.api_key_header", "api_key_header or api_key_param is required for api_key auth"));
                break;

            case AuthType.Bearer:
                if (string.IsNullOrWhiteSpace(auth.BearerToken))
                    errors.Add(new ValidationError($"{prefix}.bearer_token", "bearer_token is required for bearer auth"));
                break;

            case AuthType.ManagedIdentity:
                break;
        }
    }

    private static void ValidateDedup(DeduplicationConfig dedup, string prefix, List<ValidationError> errors)
    {
        if (dedup.SourcePriority <= 0)
            errors.Add(new ValidationError($"{prefix}.source_priority", "source_priority must be greater than 0"));

        foreach (var key in dedup.MatchKeys)
        {
            if (!KnownAssetFields.Contains(key))
                errors.Add(new ValidationError($"{prefix}.match_keys", $"'{key}' is not a valid canonical asset field; valid fields: {string.Join(", ", KnownAssetFields)}"));
        }

        if (!string.IsNullOrWhiteSpace(dedup.CustomResolver) &&
            !dedup.CustomResolver.StartsWith("plugins.", StringComparison.Ordinal))
        {
            errors.Add(new ValidationError($"{prefix}.custom_resolver", "custom_resolver must be in the plugins/ directory (must start with 'plugins.')"));
        }
    }
}
