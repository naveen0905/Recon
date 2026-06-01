using FluentAssertions;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using Xunit;

namespace ReconPlatform.UnitTests.Config;

public class ValidatorTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static TeamConfig MinimalValid() => new()
    {
        Team = "net-sec",
        StaleAfterDays = 7,
        Sources =
        [
            new SourceConfig
            {
                Id = "api-src",
                Type = SourceType.RestApi,
                BaseUrl = "https://api.example.com",
                Auth = new AuthConfig
                {
                    Type = AuthType.OAuth2,
                    ClientId = "{{secret:CLIENT_ID}}",
                    ClientSecret = "{{secret:CLIENT_SECRET}}",
                },
            }
        ],
    };

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidRestApiConfig_ReturnsSuccess()
    {
        var result = TeamConfigValidator.Validate(MinimalValid());
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValidAzureSqlConfig_ReturnsSuccess()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "sql-src",
                    Type = SourceType.AzureSql,
                    ConnectionString = "{{secret:SQL_CONN}}",
                    Query = "SELECT host FROM assets",
                },
            ],
        };

        TeamConfigValidator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidAzureSqlWithManagedIdentity_ReturnsSuccess()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "sql-mi",
                    Type = SourceType.AzureSql,
                    Query = "SELECT host FROM assets",
                    Auth = new AuthConfig { Type = AuthType.ManagedIdentity },
                },
            ],
        };

        TeamConfigValidator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidAdxConfig_ReturnsSuccess()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "adx-src",
                    Type = SourceType.AzureAdx,
                    Cluster = "https://cluster.eastus.kusto.windows.net",
                    Database = "TelemetryDb",
                    Query = "NetworkFlows | take 100",
                    Auth = new AuthConfig { Type = AuthType.ManagedIdentity },
                },
            ],
        };

        TeamConfigValidator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidPluginConfig_ReturnsSuccess()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "plugin-src",
                    Type = SourceType.Plugin,
                    PluginClass = "plugins.MyConnector",
                },
            ],
        };

        TeamConfigValidator.Validate(config).IsValid.Should().BeTrue();
    }

    // ── team-level validation ────────────────────────────────────────────────

    [Fact]
    public void Validate_MissingTeamName_ReturnsError()
    {
        var config = MinimalValid() with { Team = "" };
        var result = TeamConfigValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Field == "team");
    }

    [Fact]
    public void Validate_ZeroStaleAfterDays_ReturnsError()
    {
        var config = MinimalValid() with { StaleAfterDays = 0 };
        var result = TeamConfigValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Field == "stale_after_days");
    }

    // ── rest_api validation ──────────────────────────────────────────────────

    [Fact]
    public void Validate_RestApiMissingBaseUrl_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "api",
                    Type = SourceType.RestApi,
                    Auth = new AuthConfig { Type = AuthType.Bearer, BearerToken = "tok" },
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("base_url"));
    }

    [Fact]
    public void Validate_RestApiMissingAuth_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig { Id = "api", Type = SourceType.RestApi, BaseUrl = "https://x.com" },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("auth"));
    }

    [Fact]
    public void Validate_OAuth2MissingClientId_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "api",
                    Type = SourceType.RestApi,
                    BaseUrl = "https://x.com",
                    Auth = new AuthConfig { Type = AuthType.OAuth2, ClientSecret = "s" },
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("client_id"));
    }

    [Fact]
    public void Validate_ApiKeyMissingHeaderAndParam_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "api",
                    Type = SourceType.RestApi,
                    BaseUrl = "https://x.com",
                    Auth = new AuthConfig { Type = AuthType.ApiKey, ApiKey = "key123" },
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("api_key_header"));
    }

    // ── azure_sql validation ─────────────────────────────────────────────────

    [Fact]
    public void Validate_AzureSqlMissingQuery_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "sql",
                    Type = SourceType.AzureSql,
                    ConnectionString = "{{secret:CONN}}",
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("query"));
    }

    [Fact]
    public void Validate_AzureSqlWithOAuth2_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "sql",
                    Type = SourceType.AzureSql,
                    ConnectionString = "{{secret:CONN}}",
                    Query = "SELECT 1",
                    Auth = new AuthConfig { Type = AuthType.OAuth2, ClientId = "c", ClientSecret = "s" },
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("auth.type"));
    }

    // ── azure_adx validation ─────────────────────────────────────────────────

    [Fact]
    public void Validate_AdxMissingCluster_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "adx",
                    Type = SourceType.AzureAdx,
                    Database = "Db",
                    Query = "T | take 1",
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("cluster"));
    }

    [Fact]
    public void Validate_AdxWithBearerAuth_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig
                {
                    Id = "adx",
                    Type = SourceType.AzureAdx,
                    Cluster = "https://c.kusto.windows.net",
                    Database = "Db",
                    Query = "T | take 1",
                    Auth = new AuthConfig { Type = AuthType.Bearer, BearerToken = "tok" },
                },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("auth.type"));
    }

    // ── plugin validation ────────────────────────────────────────────────────

    [Fact]
    public void Validate_PluginMissingClass_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources = [new SourceConfig { Id = "p", Type = SourceType.Plugin }],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("plugin_class"));
    }

    [Fact]
    public void Validate_PluginClassOutsidePluginsDir_ReturnsError()
    {
        var config = new TeamConfig
        {
            Team = "ops",
            Sources =
            [
                new SourceConfig { Id = "p", Type = SourceType.Plugin, PluginClass = "SomeOther.Assembly.Class" },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.Errors.Should().ContainSingle(e => e.Field.Contains("plugin_class"));
    }

    // ── dedup validation ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_InvalidMatchKey_ReturnsError()
    {
        var source = new SourceConfig
        {
            Id = "api",
            Type = SourceType.RestApi,
            BaseUrl = "https://x.com",
            Auth = new AuthConfig { Type = AuthType.Bearer, BearerToken = "tok" },
            Dedup = new DeduplicationConfig { MatchKeys = ["host", "nonexistent_field"] },
        };

        var config = new TeamConfig { Team = "ops", Sources = [source] };
        var result = TeamConfigValidator.Validate(config);

        result.Errors.Should().ContainSingle(e =>
            e.Field.Contains("match_keys") && e.Message.Contains("nonexistent_field"));
    }

    [Fact]
    public void Validate_ValidMatchKeys_ReturnsSuccess()
    {
        var source = new SourceConfig
        {
            Id = "api",
            Type = SourceType.RestApi,
            BaseUrl = "https://x.com",
            Auth = new AuthConfig { Type = AuthType.Bearer, BearerToken = "tok" },
            Dedup = new DeduplicationConfig { MatchKeys = ["host", "ip", "port"] },
        };

        var config = new TeamConfig { Team = "ops", Sources = [source] };
        TeamConfigValidator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_CustomResolverOutsidePluginsDir_ReturnsError()
    {
        var source = new SourceConfig
        {
            Id = "api",
            Type = SourceType.RestApi,
            BaseUrl = "https://x.com",
            Auth = new AuthConfig { Type = AuthType.Bearer, BearerToken = "tok" },
            Dedup = new DeduplicationConfig { CustomResolver = "External.Library.MyResolver" },
        };

        var config = new TeamConfig { Team = "ops", Sources = [source] };
        var result = TeamConfigValidator.Validate(config);

        result.Errors.Should().ContainSingle(e => e.Field.Contains("custom_resolver"));
    }

    // ── multiple errors ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_MultipleErrors_AllReturned()
    {
        var config = new TeamConfig
        {
            Team = "",
            StaleAfterDays = -1,
            Sources =
            [
                new SourceConfig { Id = "", Type = SourceType.RestApi },
            ],
        };

        var result = TeamConfigValidator.Validate(config);
        result.IsValid.Should().BeFalse();
        result.Errors.Count.Should().BeGreaterThan(2);
    }
}
