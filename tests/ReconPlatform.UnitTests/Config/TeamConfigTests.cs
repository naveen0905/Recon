using FluentAssertions;
using ReconPlatform.Config;
using ReconPlatform.Config.Models;
using Xunit;

namespace ReconPlatform.UnitTests.Config;

public class TeamConfigTests
{
    [Fact]
    public void Deserialize_MinimalConfig_Succeeds()
    {
        const string yaml = """
            team: network-security
            stale_after_days: 7
            sources: []
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        config.Team.Should().Be("network-security");
        config.StaleAfterDays.Should().Be(7);
        config.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_RestApiSource_ParsesAllFields()
    {
        const string yaml = """
            team: network-security
            stale_after_days: 7
            sources:
              - id: asset-inventory-api
                type: rest_api
                stale_after_days: 3
                base_url: https://internal-api.corp.com/assets
                auth:
                  type: oauth2
                  client_id: "{{secret:ASSET_API_CLIENT_ID}}"
                  scope: asset.read
                mapping:
                  host: "$.data[*].hostname"
                  ip: "$.data[*].ip_address"
                dedup:
                  match_keys: [host, ip]
                  conflict_resolution: source_priority
                  source_priority: 10
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        config.Sources.Should().HaveCount(1);
        var source = config.Sources[0];
        source.Id.Should().Be("asset-inventory-api");
        source.Type.Should().Be(SourceType.RestApi);
        source.StaleAfterDays.Should().Be(3);
        source.BaseUrl.Should().Be("https://internal-api.corp.com/assets");
        source.Auth.Should().NotBeNull();
        source.Auth!.Type.Should().Be(AuthType.OAuth2);
        source.Auth.ClientId.Should().Be("{{secret:ASSET_API_CLIENT_ID}}");
        source.Mapping!.Host.Should().Be("$.data[*].hostname");
        source.Dedup.ConflictResolution.Should().Be(ConflictResolution.SourcePriority);
        source.Dedup.SourcePriority.Should().Be(10);
        source.Dedup.MatchKeys.Should().BeEquivalentTo(["host", "ip"]);
    }

    [Fact]
    public void Deserialize_AzureSqlSource_ParsesCorrectly()
    {
        const string yaml = """
            team: security-ops
            sources:
              - id: vuln-db
                type: azure_sql
                stale_after_days: 1
                connection_string: "{{secret:VULN_DB_CONN}}"
                query: "SELECT host, cve, severity FROM findings WHERE active=1"
                mapping:
                  host: host
                  finding: cve
                  severity: severity
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        var source = config.Sources[0];
        source.Type.Should().Be(SourceType.AzureSql);
        source.ConnectionString.Should().Be("{{secret:VULN_DB_CONN}}");
        source.Query.Should().Be("SELECT host, cve, severity FROM findings WHERE active=1");
    }

    [Fact]
    public void Deserialize_AzureAdxSource_ParsesCorrectly()
    {
        const string yaml = """
            team: network-security
            sources:
              - id: network-telemetry
                type: azure_adx
                cluster: https://telemetry.eastus.kusto.windows.net
                database: NetworkLogs
                query: "NetworkFlows | where timestamp > ago(7d)"
                auth:
                  type: managed_identity
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        var source = config.Sources[0];
        source.Type.Should().Be(SourceType.AzureAdx);
        source.Cluster.Should().Be("https://telemetry.eastus.kusto.windows.net");
        source.Database.Should().Be("NetworkLogs");
        source.Auth!.Type.Should().Be(AuthType.ManagedIdentity);
    }

    [Fact]
    public void Deserialize_PluginSource_ParsesCorrectly()
    {
        const string yaml = """
            team: red-team
            sources:
              - id: custom-source
                type: plugin
                plugin_class: plugins.MyCustomConnector
                stale_after_days: 14
                config:
                  endpoint: https://custom.internal/api
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        var source = config.Sources[0];
        source.Type.Should().Be(SourceType.Plugin);
        source.PluginClass.Should().Be("plugins.MyCustomConnector");
        source.Config["endpoint"].Should().Be("https://custom.internal/api");
    }

    [Fact]
    public void Deserialize_StaleAfterDays_DefaultsToTeamLevelWhenNotOverridden()
    {
        const string yaml = """
            team: network-security
            stale_after_days: 14
            sources:
              - id: no-override-source
                type: rest_api
                base_url: https://example.com
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        config.StaleAfterDays.Should().Be(14);
        config.Sources[0].StaleAfterDays.Should().BeNull();
    }

    [Fact]
    public void Deserialize_SecretPlaceholders_PreservedAsLiteralStrings()
    {
        const string yaml = """
            team: network-security
            sources:
              - id: api
                type: rest_api
                base_url: https://example.com
                auth:
                  type: oauth2
                  client_id: "{{secret:MY_CLIENT_ID}}"
                  client_secret: "{{secret:MY_CLIENT_SECRET}}"
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        var auth = config.Sources[0].Auth!;
        auth.ClientId.Should().Be("{{secret:MY_CLIENT_ID}}");
        auth.ClientSecret.Should().Be("{{secret:MY_CLIENT_SECRET}}");
    }

    [Fact]
    public void Deserialize_EmptyYaml_ThrowsArgumentException()
    {
        var act = () => TeamConfigSerializer.Deserialize(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deserialize_DeduplicationDefaults_AreApplied()
    {
        const string yaml = """
            team: network-security
            sources:
              - id: api
                type: rest_api
                base_url: https://example.com
            """;

        var config = TeamConfigSerializer.Deserialize(yaml);

        var dedup = config.Sources[0].Dedup;
        dedup.ConflictResolution.Should().Be(ConflictResolution.LastWrite);
        dedup.SourcePriority.Should().Be(100);
        dedup.MatchKeys.Should().BeEmpty();
        dedup.CustomResolver.Should().BeNull();
    }
}
